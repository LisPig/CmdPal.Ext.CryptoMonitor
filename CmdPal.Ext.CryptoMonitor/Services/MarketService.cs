// Owns polling, source selection, the shared quote cache and alert evaluation.
//
// Poll flow (Auto mode, the default):
//   targets -> Binance Vision (one request for the whole list) -> Gate.io ->
//   CoinEx, each one asked only for the tickers the previous ones could not
//   serve. Prices are published before anything slow runs, then CoinGecko
//   metadata (market cap / rank / 7d) is merged in the background when it is
//   reachable.
//
// Everything on the price path is reachable *directly* from this machine, so
// prices keep working when the system proxy client is stopped, starting up, or
// pointed at a dead node — which is what used to happen after every reboot.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Services;

/// <summary>Where quotes come from.</summary>
public enum QuoteMode
{
    /// Exchange APIs first (direct, no proxy), CoinGecko for metadata.
    Auto,

    /// CoinGecko only — the pre-0.3 behaviour, useful when a proxy is always up.
    CoinGecko,
}

public sealed class MarketService : IDisposable
{
    // Canonical symbol->id pin for well-known coins so duplicate symbols
    // (many "btc"-like tokens exist) never resolve to the wrong coin.
    private static readonly IReadOnlyDictionary<string, string> Canonical =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["btc"] = "bitcoin", ["eth"] = "ethereum", ["sol"] = "solana", ["bnb"] = "bnb",
            ["xrp"] = "ripple", ["doge"] = "dogecoin", ["ada"] = "cardano", ["avax"] = "avalanche-2",
            ["dot"] = "polkadot", ["link"] = "chainlink", ["ltc"] = "litecoin", ["bch"] = "bitcoin-cash",
            ["trx"] = "tron", ["ton"] = "toncoin", ["near"] = "near", ["apt"] = "aptos",
            ["sui"] = "sui", ["arb"] = "arbitrum", ["op"] = "optimism", ["inj"] = "injective",
            ["stx"] = "stacks", ["fil"] = "filecoin", ["atom"] = "cosmos", ["uni"] = "uniswap",
            ["shib"] = "shiba-inu", ["usdt"] = "tether", ["usdc"] = "usd-coin", ["dai"] = "dai",
            ["wbtc"] = "wrapped-bitcoin", ["pyth"] = "pyth-network", ["sei"] = "sei-network",
            ["celo"] = "celo", ["aave"] = "aave", ["cake"] = "pancakeswap-token",
            ["gmx"] = "gmx", ["jup"] = "jupiter-exchange-solana", ["tia"] = "celestia",
            ["ordi"] = "ordi", ["pepe"] = "pepe", ["wif"] = "dogwifcoin",
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalReverse =
        Canonical.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // CoinGecko's /coins/list snapshot is only refetched this often (it is a
    // 1.4 MB response behind a proxy that may not even be up).
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromHours(12);

    private const int MetaRefreshMinutes = 5;
    private const int MetaFailureBackoffMinutes = 20;
    private const int MinBackoffSeconds = 5;
    private const int PollBudgetSeconds = 20;

    /// Token to store in the watchlist for a coin found via search/detail.
    /// Favorites store their symbol (btc); anything else stores its stable CoinGecko id.
    public static string WatchTokenFor(string id, string symbol)
    {
        if (CanonicalReverse.TryGetValue(id, out var sym))
        {
            return sym;
        }
        var sym2 = symbol?.Trim().ToLowerInvariant() ?? "";
        return Canonical.ContainsKey(sym2) ? sym2 : id;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, CoinMarketQuote> _quotesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CoinMarketQuote> _quotesBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _alertFired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extraIds = new(StringComparer.OrdinalIgnoreCase); // detail pages, not watchlist
    private readonly HashSet<string> _alertSymbols = new(StringComparer.OrdinalIgnoreCase);

    private readonly CoinCatalog _catalog = new();
    private readonly QuoteCache _cache = new();
    private readonly FxRateProvider _fx = new();
    private readonly ExchangeQuoteSource[] _sources;

    private CoinGeckoClient? _client;
    private Timer? _timer;
    private bool _disposed;
    private bool _polling;
    private bool _pollQueued;
    private string? _lastError;
    private string? _lastNote;
    private string _activeSource = "";
    private DateTime? _lastSuccessUtc;
    private int _consecutiveFailures;
    private int _refreshSeconds = 60;
    private DateTime _lastMetaUtc = DateTime.MinValue;
    private DateTime _metaBackoffUntil = DateTime.MinValue;
    private DateTime _lastCatalogAttemptUtc = DateTime.MinValue;
    private string _proxySignature = "";
    private string _currency = "usd";
    private string _apiKey = "";
    private QuoteMode _mode = QuoteMode.Auto;
    private bool _metaEnabled = true;
    private IReadOnlyList<string> _watchSymbols = [];
    private IReadOnlyList<AlertRule> _alerts = [];
    private bool _alertsEnabled;
    private readonly CancellationTokenSource _cts = new();

    public MarketService()
    {
        _sources = [new BinanceVisionSource(), new GateSource(), new CoinExSource()];
        _client = new CoinGeckoClient(null);
        _proxySignature = Network.ProxySignature();
        _timer = new Timer(_ => _ = PollAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action? Updated;             // after every completed poll attempt
    public event Action<string>? PollFailed;  // with a user-facing error message

    public Task PollNowAsync() => PollAsync();

    /// Multiplier that turns a USDT-quoted exchange price into the display
    /// currency (1 for USD, the USD/CNY rate for CNY). Used by the chart, whose
    /// candles are always USDT based.
    public async Task<double> GetDisplayRateAsync()
    {
        if (!Currency.Equals("cny", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return await _fx.UsdToCnyAsync(_cts.Token).ConfigureAwait(false) ?? 1;
    }

    public string Currency
    {
        get
        {
            lock (_lock)
            {
                return _currency;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    /// Non-fatal caveat about the last poll (e.g. a coin with no quote).
    public string? Note
    {
        get
        {
            lock (_lock)
            {
                return _lastNote;
            }
        }
    }

    /// When the currently displayed quotes were fetched (null = never).
    public DateTime? LastSuccessUtc
    {
        get
        {
            lock (_lock)
            {
                return _lastSuccessUtc;
            }
        }
    }

    /// Which source the displayed prices came from ("Binance", "CoinGecko", …).
    public string ActiveSource
    {
        get
        {
            lock (_lock)
            {
                return _activeSource;
            }
        }
    }

    public IReadOnlyList<CoinMarketQuote> WatchQuotes
    {
        get
        {
            lock (_lock)
            {
                var list = new List<CoinMarketQuote>(_watchSymbols.Count);
                foreach (var token in _watchSymbols)
                {
                    var id = LookupIdLocked(token);
                    if (id is not null && _quotesById.TryGetValue(id, out var q) && q.CurrentPrice.HasValue)
                    {
                        list.Add(q);
                    }
                }
                return list;
            }
        }
    }

    /// Resolve a watchlist token (canonical symbol like "btc", arbitrary symbol,
    /// or a CoinGecko id) to the current quote for it.
    public CoinMarketQuote? QuoteForToken(string token)
    {
        lock (_lock)
        {
            var id = LookupIdLocked(token);
            return id is not null && _quotesById.TryGetValue(id, out var q) ? q : null;
        }
    }

    public IReadOnlyList<string> WatchTokens
    {
        get
        {
            lock (_lock)
            {
                return _watchSymbols.ToArray();
            }
        }
    }

    public CoinMarketQuote? BySymbol(string symbol)
    {
        lock (_lock)
        {
            var id = LookupIdLocked(symbol);
            return id is not null && _quotesById.TryGetValue(id, out var q) ? q : null;
        }
    }

    public CoinMarketQuote? ById(string id)
    {
        lock (_lock)
        {
            return _quotesById.TryGetValue(id, out var q) ? q : null;
        }
    }

    /// Canonical CoinGecko id for a watchlist token or alias ("btc" -> "bitcoin",
    /// "zcash" -> "zcash"). Falls back to the token itself for unknown coins.
    public string ResolveId(string tokenOrId)
    {
        if (string.IsNullOrWhiteSpace(tokenOrId))
        {
            return tokenOrId ?? string.Empty;
        }
        lock (_lock)
        {
            return LookupIdLocked(tokenOrId.Trim()) ?? tokenOrId.Trim();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _timer?.Dispose();
        _client?.Dispose();
        foreach (var source in _sources)
        {
            source.Dispose();
        }
        _fx.Dispose();
    }

    public void LogState(string tag)
    {
        lock (_lock)
        {
            Log.Info($"{tag}: currency={_currency} mode={_mode} meta={_metaEnabled} watch={string.Join(',', _watchSymbols)} " +
                     $"alerts={_alerts.Count} enabled={_alertsEnabled} source={_activeSource} catalog={_catalog.Count} lastError={_lastError}");
        }
    }

    /// Called on construction and whenever settings change.
    public void Configure(CryptoMonitorSettings s)
    {
        var newKey = s.ApiKey;
        var needsClient = false;
        lock (_lock)
        {
            _currency = s.Currency;
            _alertsEnabled = s.AlertsEnabled;
            _watchSymbols = s.WatchSymbols;
            _alerts = s.AlertRules;
            _mode = s.QuoteMode;
            _metaEnabled = s.UseCoinGeckoMeta;
            _refreshSeconds = s.RefreshSeconds;
            _alertFired.Clear();
            _alertSymbols.Clear();
            foreach (var r in _alerts)
            {
                _alertSymbols.Add(r.Symbol);
            }
            if (_client is null || !string.Equals(_apiKey, newKey, StringComparison.Ordinal))
            {
                needsClient = true;
                _apiKey = newKey;
            }
        }
        if (needsClient)
        {
            RebuildCoinGeckoClient();
        }
        _proxySignature = Network.ProxySignature();
        _consecutiveFailures = 0;

        // First run (or a fresh install): show the last known prices while the
        // first poll is still in flight, instead of an empty panel.
        if (_quotesById.Count == 0)
        {
            var currency = Currency;
            if (_cache.Load(currency) is { } snapshot)
            {
                HydrateFrom(snapshot);
            }
        }

        _ = PollAsync(); // immediate refresh so the UI is fresh on open (this also schedules the timer)
    }

    /// Full-text coin search (symbol or name) with live price enrichment.
    /// CoinGecko is used for relevance when reachable; the local catalog backs it
    /// up so search keeps working offline. Prices come from the exchange sources.
    public async Task<List<CoinSearchResult>> SearchCoinsAsync(string query, CancellationToken ct)
    {
        var results = new List<CoinSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_client is { } client)
            {
                var resp = await client.SearchAsync(query, ct).ConfigureAwait(false);
                foreach (var hit in resp.Coins.Where(h => !string.IsNullOrWhiteSpace(h.Id)).Take(8))
                {
                    if (seen.Add(hit.Id))
                    {
                        results.Add(new CoinSearchResult(hit.Id, hit.Name, hit.Symbol, hit.Thumb ?? hit.Large, null, hit.MarketCapRank));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Info($"search: CoinGecko unavailable ({ex.Message}); using the local catalog");
        }

        foreach (var info in _catalog.Search(query, 8))
        {
            if (seen.Add(info.Id))
            {
                var image = ById(info.Id)?.Image;
                results.Add(new CoinSearchResult(info.Id, info.Name, info.Symbol.ToLowerInvariant(), image, null, null));
            }
        }

        if (results.Count == 0)
        {
            return [];
        }

        // Live prices for whatever we are about to show.
        var targets = results.Select(r => MakeTarget(r.Id, r.Symbol)).ToList();
        await FetchAndMergeAsync(targets, ct).ConfigureAwait(false);

        return results
            .Select(r =>
            {
                var q = ById(r.Id);
                return r with { Quote = q, Rank = r.Rank ?? q?.MarketCapRank };
            })
            .ToList();
    }

    /// Make sure one extra coin (opened detail page, not on watchlist) has a live quote.
    public Task EnsureDetailAsync(string id)
    {
        if (ById(id) is not null)
        {
            return Task.CompletedTask;
        }
        lock (_lock)
        {
            _extraIds.Add(id);
        }
        return PollAsync();
    }

    // ---- polling ----

    private async Task PollAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (_polling)
        {
            _pollQueued = true; // someone asked for a refresh while one is in flight — run again after
            return;
        }
        _polling = true;
        var ok = false;
        try
        {
            EnsureClientsCurrent();

            var targets = CollectTargets();
            if (targets.Count == 0)
            {
                lock (_lock)
                {
                    _lastError = "监控列表为空,或币种符号无法识别";
                    _lastNote = null;
                }
            }
            else
            {
                Log.Info($"poll targets[{targets.Count}]: {string.Join(",", targets.Take(12).Select(t => $"{t.DisplaySymbol}->{t.BaseSymbol}"))}" +
                         (targets.Count > 12 ? "…" : ""));
                var outcome = await FetchAndMergeAsync(targets, _cts.Token).ConfigureAwait(false);
                Apply(outcome);
                ok = outcome.Priced > 0;
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down or superseded
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _lastError = ex.Message;
            }
            Log.Error($"poll crashed: {ex}");
        }
        finally
        {
            _polling = false;
        }

        _consecutiveFailures = ok ? 0 : _consecutiveFailures + 1;
        ScheduleNext(NextDelay(ok));

        // Publish prices before any slow metadata call so the UI updates at once.
        Updated?.Invoke();
        if (LastError is { } err)
        {
            PollFailed?.Invoke(err);
            Log.Error($"poll error: {err}");
        }

        if (ok && _metaEnabled && _mode == QuoteMode.Auto && await TryMetaEnrichAsync().ConfigureAwait(false))
        {
            Updated?.Invoke();
        }

        // The catalog is only used to map unknown tickers to CoinGecko ids, so it
        // runs after prices are already on screen.
        if (await RefreshCatalogIfDueAsync().ConfigureAwait(false))
        {
            Updated?.Invoke();
        }

        if (_pollQueued)
        {
            _pollQueued = false;
            Log.Info("running queued re-poll");
            _ = PollAsync();
            return;
        }

        // A poll cycle (and the icon/chart work it triggers) has just finished and
        // the process is about to sleep again — a good moment to give back what
        // is no longer needed. Throttled internally, no-op most calls.
        Memory.TrimIdle();
    }

    private async Task<Outcome> FetchAndMergeAsync(IReadOnlyList<CoinTarget> targets, CancellationToken outerCt)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        budget.CancelAfter(TimeSpan.FromSeconds(PollBudgetSeconds));
        try
        {
            return _mode == QuoteMode.CoinGecko
                ? await FetchFromCoinGeckoAsync(targets, budget.Token).ConfigureAwait(false)
                : await FetchFromExchangesAsync(targets, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            return new Outcome(targets.Count, 0, [], "", $"行情源超时(>{PollBudgetSeconds}s),请检查网络或代理");
        }
    }

    private async Task<Outcome> FetchFromExchangesAsync(IReadOnlyList<CoinTarget> targets, CancellationToken ct)
    {
        double rate = 1;
        if (Currency.Equals("cny", StringComparison.OrdinalIgnoreCase))
        {
            var usdCny = await _fx.UsdToCnyAsync(ct).ConfigureAwait(false);
            if (usdCny is not > 0)
            {
                // No fiat rate available: CoinGecko can quote CNY natively, so use
                // it rather than showing USDT numbers with a ¥ sign.
                Log.Info("cny requested but no USD/CNY rate available; falling back to CoinGecko");
                return await FetchFromCoinGeckoAsync(targets, ct).ConfigureAwait(false);
            }
            rate = usdCny.Value;
        }

        var resolved = new Dictionary<string, (SourceQuote Quote, string Source)>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        // USDT is quoted against itself; synthesise it so a "tether" entry never
        // depends on the network.
        if (targets.Any(t => t.BaseSymbol.Equals("USDT", StringComparison.OrdinalIgnoreCase)))
        {
            resolved["USDT"] = (new SourceQuote(1, 0, null, null, null), "内置");
        }

        var bases = targets.Select(t => t.BaseSymbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var source in _sources)
        {
            var missing = bases.Where(b => !resolved.ContainsKey(b)).ToList();
            if (missing.Count == 0)
            {
                break;
            }
            try
            {
                var got = await source.FetchAsync(missing, ct).ConfigureAwait(false);
                foreach (var kv in got)
                {
                    resolved[kv.Key] = (kv.Value, source.Label);
                }
                Log.Info($"{source.Id}: {got.Count}/{missing.Count} quotes{(source.LastAttemptUsedProxy ? " (via proxy)" : "")}");
            }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
            {
                errors.Add($"{source.Label} 超时");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Label} {Short(ex)}");
                Log.Info($"{source.Id} failed: {ex.Message}");
            }
        }

        var (priced, missingSymbols, usedSource) = MergeExchangeQuotes(targets, resolved, rate);
        string? error = priced > 0
            ? null
            : errors.Count > 0
                ? $"行情源不可用: {string.Join(" / ", errors)}"
                : "所选币种在行情源中都查不到(可试试 CoinGecko 数据源)";
        return new Outcome(targets.Count, priced, missingSymbols, usedSource, error);
    }

    private async Task<Outcome> FetchFromCoinGeckoAsync(IReadOnlyList<CoinTarget> targets, CancellationToken ct)
    {
        var client = _client;
        if (client is null)
        {
            return new Outcome(targets.Count, 0, [], "", "CoinGecko 客户端未初始化");
        }

        try
        {
            var ids = targets.Select(t => t.Id).ToList();
            var quotes = await client.GetMarketsAsync(ids, Currency, ct).ConfigureAwait(false);
            if (quotes.Count == 0)
            {
                return new Outcome(targets.Count, 0, [], CoinGeckoClient.SourceLabel, "CoinGecko 未返回任何行情");
            }

            var priced = 0;
            lock (_lock)
            {
                foreach (var q in quotes)
                {
                    if (string.IsNullOrEmpty(q.Id))
                    {
                        continue;
                    }
                    _quotesById[q.Id] = q;
                    if (!string.IsNullOrEmpty(q.Symbol))
                    {
                        _quotesBySymbol[q.Symbol.ToLowerInvariant()] = q;
                    }
                    if (q.CurrentPrice is > 0)
                    {
                        priced++;
                    }
                }
            }

            var missing = targets
                .Where(t => !quotes.Any(q => string.Equals(q.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.DisplaySymbol)
                .ToList();
            return new Outcome(targets.Count, priced, missing, CoinGeckoClient.SourceLabel, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Outcome(targets.Count, 0, [], CoinGeckoClient.SourceLabel, $"CoinGecko: {Short(ex)}");
        }
    }

    /// Merge exchange quotes into the shared table, converting USDT -> display
    /// currency and preserving CoinGecko metadata (market cap/rank/7d/logo) that
    /// an exchange cannot provide.
    private (int Priced, List<string> Missing, string Source) MergeExchangeQuotes(
        IReadOnlyList<CoinTarget> targets,
        Dictionary<string, (SourceQuote Quote, string Source)> resolved,
        double rate)
    {
        var missing = new List<string>();
        var perSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var priced = 0;

        lock (_lock)
        {
            foreach (var t in targets)
            {
                if (!resolved.TryGetValue(t.BaseSymbol, out var hit) || hit.Quote.Price is not > 0)
                {
                    missing.Add(t.DisplaySymbol);
                    continue;
                }

                _quotesById.TryGetValue(t.Id, out var prev);
                var price = hit.Quote.Price.Value * rate;
                var quote = new CoinMarketQuote
                {
                    Id = t.Id,
                    Symbol = t.DisplaySymbol.ToLowerInvariant(),
                    Name = prev?.Name is { Length: > 0 } name ? name : t.Name,
                    Image = prev?.Image,
                    CurrentPrice = price,
                    Pct24h = hit.Quote.Pct24h,
                    High24h = hit.Quote.High24h * rate,
                    Low24h = hit.Quote.Low24h * rate,
                    TotalVolume = hit.Quote.QuoteVolume * rate,
                    MarketCap = prev?.MarketCap,
                    MarketCapRank = prev?.MarketCapRank,
                    Pct7dInCurrency = prev?.Pct7dInCurrency,
                    Pct24hInCurrency = hit.Quote.Pct24h,
                };
                _quotesById[t.Id] = quote;
                _quotesBySymbol[quote.Symbol] = quote;
                priced++;
                perSource[hit.Source] = perSource.GetValueOrDefault(hit.Source) + 1;
            }
        }

        var source = perSource.Count > 0
            ? perSource.OrderByDescending(kv => kv.Value).First().Key
            : "";
        return (priced, missing, source);
    }

    private void Apply(Outcome outcome)
    {
        List<CoinMarketQuote>? snapshot = null;
        lock (_lock)
        {
            _lastError = outcome.Error;
            _lastNote = outcome.Missing.Count > 0
                ? $"无行情: {string.Join(",", outcome.Missing.Take(6))}{(outcome.Missing.Count > 6 ? "…" : "")}"
                : null;
            if (outcome.Priced > 0)
            {
                _lastSuccessUtc = DateTime.UtcNow;
                if (outcome.Source.Length > 0)
                {
                    _activeSource = outcome.Source;
                }
                snapshot = _quotesById.Values.ToList();
            }
        }
        if (snapshot is not null)
        {
            _cache.Save(Currency, ActiveSource, snapshot);
            if (_alertsEnabled)
            {
                EvaluateAlerts();
            }
        }
    }

    /// CoinGecko metadata pass: market cap, rank, 7d change and logos, merged on
    /// top of the exchange prices. Never touches the price, never fails a poll.
    private async Task<bool> TryMetaEnrichAsync()
    {
        if (DateTime.UtcNow < _metaBackoffUntil || DateTime.UtcNow - _lastMetaUtc < TimeSpan.FromMinutes(MetaRefreshMinutes))
        {
            return false;
        }
        _lastMetaUtc = DateTime.UtcNow;

        List<string> ids;
        lock (_lock)
        {
            ids = _quotesById.Keys.Take(60).ToList();
        }
        if (ids.Count == 0 || _client is null)
        {
            return false;
        }

        try
        {
            var meta = await _client.GetMarketsAsync(ids, Currency, _cts.Token).ConfigureAwait(false);
            if (meta.Count == 0)
            {
                return false;
            }
            lock (_lock)
            {
                foreach (var m in meta)
                {
                    if (!_quotesById.TryGetValue(m.Id, out var cur))
                    {
                        continue;
                    }
                    _quotesById[m.Id] = new CoinMarketQuote
                    {
                        Id = cur.Id,
                        Symbol = cur.Symbol,
                        Name = string.IsNullOrWhiteSpace(m.Name) ? cur.Name : m.Name,
                        Image = string.IsNullOrWhiteSpace(m.Image) ? cur.Image : m.Image,
                        CurrentPrice = cur.CurrentPrice, // exchange price wins
                        Pct24h = cur.Pct24h,
                        Pct24hInCurrency = cur.Pct24hInCurrency,
                        High24h = cur.High24h,
                        Low24h = cur.Low24h,
                        TotalVolume = cur.TotalVolume,
                        MarketCap = m.MarketCap ?? cur.MarketCap,
                        MarketCapRank = m.MarketCapRank ?? cur.MarketCapRank,
                        Pct7dInCurrency = m.Pct7dInCurrency ?? cur.Pct7dInCurrency,
                    };
                }
            }
            Log.Info($"coinGecko metadata merged for {meta.Count} coins");
            List<CoinMarketQuote> snapshot;
            lock (_lock)
            {
                snapshot = _quotesById.Values.ToList();
            }
            _cache.Save(Currency, ActiveSource, snapshot);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Expected whenever the proxy is down — back off instead of paying a
            // 20s timeout on every poll.
            _metaBackoffUntil = DateTime.UtcNow.AddMinutes(MetaFailureBackoffMinutes);
            Log.Info($"coinGecko metadata skipped: {Short(ex)} (backing off {MetaFailureBackoffMinutes} min)");
            return false;
        }
    }

    private async Task<bool> RefreshCatalogIfDueAsync()
    {
        if (!_catalog.SnapshotIsStale(CatalogMaxAge)
            || DateTime.UtcNow - _lastCatalogAttemptUtc < TimeSpan.FromHours(1)
            || _client is null)
        {
            return false;
        }
        _lastCatalogAttemptUtc = DateTime.UtcNow;
        try
        {
            var list = await _client.GetFullListAsync(_cts.Token).ConfigureAwait(false);
            if (list.Count > 0)
            {
                _catalog.IngestCoinList(list);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Log.Info($"coin catalog refresh skipped: {Short(ex)}");
        }
        return false;
    }

    private void EnsureClientsCurrent()
    {
        var signature = Network.ProxySignature();
        if (string.Equals(signature, _proxySignature, StringComparison.Ordinal))
        {
            return;
        }
        // The system proxy moved (proxy client started/stopped/changed port):
        // rebuild so we don't keep talking to a dead endpoint until restart.
        Log.Info($"system proxy changed -> {signature}; rebuilding CoinGecko client");
        _proxySignature = signature;
        RebuildCoinGeckoClient();
    }

    private void RebuildCoinGeckoClient()
    {
        string key;
        lock (_lock)
        {
            key = _apiKey;
        }
        try
        {
            var old = Interlocked.Exchange(ref _client, new CoinGeckoClient(key));
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"rebuild CoinGecko client failed: {ex.Message}");
        }
    }

    private void ScheduleNext(TimeSpan delay)
    {
        if (_disposed || _timer is null)
        {
            return;
        }
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }
        try
        {
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private TimeSpan NextDelay(bool ok)
    {
        int refresh;
        lock (_lock)
        {
            refresh = _refreshSeconds;
        }
        var target = TimeSpan.FromSeconds(Math.Max(5, refresh));
        if (ok)
        {
            return target;
        }
        // 5s, 10s, 20s, … capped at the configured interval, with jitter so a
        // flapping network is not polled in lockstep.
        var seconds = MinBackoffSeconds * Math.Pow(2, Math.Min(_consecutiveFailures, 6));
        var capped = TimeSpan.FromSeconds(Math.Min(seconds, target.TotalSeconds));
        var jitter = 0.9 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromMilliseconds(capped.TotalMilliseconds * jitter);
    }

    private List<CoinTarget> CollectTargets()
    {
        lock (_lock)
        {
            var result = new List<CoinTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string token)
            {
                var id = LookupIdLocked(token);
                if (id is null || !seen.Add(id))
                {
                    return;
                }
                result.Add(MakeTarget(id, token));
            }

            foreach (var token in _watchSymbols)
            {
                Add(token);
            }
            foreach (var token in _alertSymbols)
            {
                Add(token);
            }
            foreach (var id in _extraIds)
            {
                Add(id);
            }
            return result;
        }
    }

    /// Id + display ticker + exchange base symbol for one coin.
    private CoinTarget MakeTarget(string id, string? tokenHint)
    {
        var info = _catalog.ById(id);
        var symbol = info?.Symbol;
        if (string.IsNullOrWhiteSpace(symbol) && CanonicalReverse.TryGetValue(id, out var canonical))
        {
            symbol = canonical.ToUpperInvariant();
        }
        if (string.IsNullOrWhiteSpace(symbol))
        {
            symbol = (tokenHint ?? id).ToUpperInvariant();
        }
        var name = info?.Name is { Length: > 0 } n ? n : symbol!;
        return new CoinTarget(id, symbol!, symbol!.ToUpperInvariant(), name);
    }

    private string? LookupIdLocked(string symbolOrId)
    {
        // 1) canonical favorites (btc -> bitcoin …)
        if (Canonical.TryGetValue(symbolOrId, out var canonicalId))
        {
            return canonicalId;
        }
        // 2) CoinGecko id straight from the catalog / watchlist ("zcash")
        if (_catalog.ById(symbolOrId) is { } byId)
        {
            return byId.Id;
        }
        // 3) unambiguous ticker from the catalog snapshot ("zec" -> zcash)
        if (_catalog.BySymbol(symbolOrId) is { } bySymbol)
        {
            return bySymbol.Id;
        }
        // 4) anything else is treated as a raw CoinGecko id. Watchlist entries for
        //    non-canonical coins are stored as ids, so this must not depend on the
        //    (slow/fail-prone) full catalog having loaded.
        return symbolOrId;
    }

    private void HydrateFrom(QuoteCache.Snapshot snapshot)
    {
        lock (_lock)
        {
            if (!snapshot.Source.Equals("", StringComparison.Ordinal))
            {
                _activeSource = snapshot.Source;
            }
            foreach (var q in snapshot.Quotes)
            {
                if (string.IsNullOrEmpty(q.Id))
                {
                    continue;
                }
                _quotesById[q.Id] = q;
                if (!string.IsNullOrEmpty(q.Symbol))
                {
                    _quotesBySymbol[q.Symbol.ToLowerInvariant()] = q;
                }
            }
            _lastSuccessUtc = snapshot.UpdatedUtc;
            _lastError = null;
        }
        Log.Info($"quote cache: {snapshot.Quotes.Count} quotes from {snapshot.UpdatedUtc:u} ({snapshot.Source})");
    }

    private static string Short(Exception ex)
    {
        var msg = ex.Message.ReplaceLineEndings(" ");
        return msg.Length > 120 ? msg[..120] : msg;
    }

    private void EvaluateAlerts()
    {
        var fired = new List<(CoinMarketQuote Q, AlertRule Rule)>();
        lock (_lock)
        {
            foreach (var rule in _alerts)
            {
                var id = LookupIdLocked(rule.Symbol);
                var q = id is not null && _quotesById.TryGetValue(id, out var x) ? x : null;
                if (q?.CurrentPrice is not double price)
                {
                    continue;
                }
                var key = $"{(rule.IsAbove ? ">" : "<")}|{rule.Symbol}|{rule.Threshold}";
                bool condition = rule.IsAbove ? price > rule.Threshold : price < rule.Threshold;
                if (condition)
                {
                    if (!_alertFired.TryGetValue(key, out var firedBefore) || !firedBefore)
                    {
                        _alertFired[key] = true;
                        fired.Add((q, rule));
                    }
                }
                else
                {
                    _alertFired[key] = false; // re-arm when price moves back across the line
                }
            }
        }

        foreach (var (q, rule) in fired)
        {
            var sym = q.Symbol.ToUpperInvariant();
            var op = rule.IsAbove ? "高于" : "低于";
            var price = Format.Money(Currency, q.CurrentPrice!.Value);
            var th = Format.Money(Currency, rule.Threshold);
            new ToastStatusMessage($"Crypto Monitor ⚠️ {sym} {op} {th},现价 {price}").Show();
        }
    }

    private sealed record CoinTarget(string Id, string DisplaySymbol, string BaseSymbol, string Name);

    private sealed record Outcome(int Requested, int Priced, List<string> Missing, string Source, string? Error);
}
