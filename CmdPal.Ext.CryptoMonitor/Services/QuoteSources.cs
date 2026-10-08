// Exchange quote sources, all reachable *directly* from this network (no proxy
// needed) and all keyed by plain ticker ("BTC"):
//
//   Binance Vision  -> one request for the whole watchlist, fastest (private
//                      market-data endpoint, no API key, weight 6/6000 per min)
//   Gate.io         -> one request per pair
//   CoinEx          -> one request per batch
//
// CoinGecko stays available as an optional enrichment/metadata source, but it is
// deliberately NOT on the price path: it is unreachable without the proxy here,
// which is exactly what made prices disappear after a reboot.
//
// Every source first tries a direct connection and only falls back to the
// system proxy when the direct attempt fails, so a stopped proxy client cannot
// take the prices down with it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

public sealed record SourceQuote(double? Price, double? Pct24h, double? High24h, double? Low24h, double? QuoteVolume);

internal sealed class SourceHttpException(int status, string body)
    : Exception($"HTTP {status}: {(body.Length > 160 ? body[..160] : body).ReplaceLineEndings(" ")}")
{
    public int Status { get; } = status;
}

internal abstract class ExchangeQuoteSource : IDisposable
{
    protected const string UserAgent = "CmdPal.Ext.CryptoMonitor/0.3";

    private readonly Lazy<HttpClient> _direct;
    private readonly Lazy<HttpClient> _proxied;
    private int _lastAttemptUsedProxy;

    protected ExchangeQuoteSource(TimeSpan timeout)
    {
        _direct = new Lazy<HttpClient>(() => Network.CreateClient(UserAgent, timeout, ProxyMode.Direct));
        _proxied = new Lazy<HttpClient>(() => Network.CreateClient(UserAgent, timeout, ProxyMode.SystemProxy));
    }

    /// Stable id used in settings/telemetry.
    public abstract string Id { get; }

    /// Human readable name shown in the UI next to prices.
    public abstract string Label { get; }

    public bool LastAttemptUsedProxy => Volatile.Read(ref _lastAttemptUsedProxy) != 0;

    /// Exchange-specific pair name for a base ticker (BTC -> BTCUSDT / BTC_USDT).
    protected abstract string PairOf(string baseSymbol);

    protected abstract Task<Dictionary<string, SourceQuote>> FetchCoreAsync(
        HttpClient http, IReadOnlyList<string> baseSymbols, CancellationToken ct);

    /// Quotes for the requested bases; symbols the exchange does not list are
    /// simply absent from the result.
    public async Task<Dictionary<string, SourceQuote>> FetchAsync(
        IReadOnlyList<string> baseSymbols, CancellationToken ct)
    {
        var empty = new Dictionary<string, SourceQuote>(StringComparer.OrdinalIgnoreCase);
        if (baseSymbols.Count == 0)
        {
            return empty;
        }

        try
        {
            var direct = await FetchCoreAsync(_direct.Value, baseSymbols, ct).ConfigureAwait(false);
            Volatile.Write(ref _lastAttemptUsedProxy, 0);
            if (direct.Count > 0)
            {
                return direct;
            }
            // Nothing found: either these pairs do not exist here, or the direct
            // attempt returned an empty success. Either way the proxy would not
            // help, so don't pay for a second round trip.
            return direct;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Info($"{Id}: direct request failed ({Describe(ex)}); retrying through the system proxy");
        }

        var proxied = await FetchCoreAsync(_proxied.Value, baseSymbols, ct).ConfigureAwait(false);
        Volatile.Write(ref _lastAttemptUsedProxy, 1);
        return proxied;
    }

    public void Dispose()
    {
        if (_direct.IsValueCreated)
        {
            _direct.Value.Dispose();
        }
        if (_proxied.IsValueCreated)
        {
            _proxied.Value.Dispose();
        }
    }

    protected static async Task<string> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SourceHttpException((int)resp.StatusCode, body);
        }
        return body;
    }

    protected static string Describe(Exception ex) =>
        ex is SourceHttpException http ? $"HTTP {http.Status}" : ex.Message;
}

// ---- Binance public market data ----

internal sealed class BinanceVisionSource : ExchangeQuoteSource
{
    private const string Api = "https://data-api.binance.vision";

    public BinanceVisionSource() : base(TimeSpan.FromSeconds(8))
    {
    }

    public override string Id => "binance";

    public override string Label => "Binance";

    protected override string PairOf(string baseSymbol) => baseSymbol + "USDT";

    protected override async Task<Dictionary<string, SourceQuote>> FetchCoreAsync(
        HttpClient http, IReadOnlyList<string> baseSymbols, CancellationToken ct)
    {
        var result = new Dictionary<string, SourceQuote>(StringComparer.OrdinalIgnoreCase);
        var pairs = baseSymbols.Select(PairOf).ToList();

        try
        {
            var symbols = Uri.EscapeDataString(JsonSerializer.Serialize(pairs, AppJsonContext.Default.ListString));
            var body = await GetAsync(http, $"{Api}/api/v3/ticker/24hr?symbols={symbols}", ct).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize(body, AppJsonContext.Default.ListBinanceTicker24h) ?? [];
            foreach (var row in rows)
            {
                Absorb(result, row.Symbol, row.LastPrice, row.Pct24h, row.High24h, row.Low24h, row.QuoteVolume);
            }
            return result;
        }
        catch (SourceHttpException ex) when (ex.Status == 400)
        {
            // Binance rejects the whole batch when a single symbol is unknown, so
            // probe the symbols one by one and keep whatever is listed.
            Log.Info($"binance: batch rejected, probing {pairs.Count} symbols individually");
        }

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var body = await GetAsync(http, $"{Api}/api/v3/ticker/24hr?symbol={pair}", ct).ConfigureAwait(false);
                var row = JsonSerializer.Deserialize(body, AppJsonContext.Default.BinanceTicker24h);
                if (row is not null)
                {
                    Absorb(result, row.Symbol, row.LastPrice, row.Pct24h, row.High24h, row.Low24h, row.QuoteVolume);
                }
            }
            catch (SourceHttpException missing) when (missing.Status is 400 or 404)
            {
                Log.Info($"binance: {pair} is not listed");
            }
        }
        return result;
    }

    private static void Absorb(
        Dictionary<string, SourceQuote> result, string pair,
        double? price, double? pct, double? high, double? low, double? volume)
    {
        if (price is not > 0 || !pair.EndsWith("USDT", StringComparison.Ordinal) || pair.Length <= 4)
        {
            return;
        }
        result[pair[..^4]] = new SourceQuote(price, pct, high, low, volume);
    }
}

// ---- Gate.io v4 spot ----

internal sealed class GateSource : ExchangeQuoteSource
{
    private const string Api = "https://api.gateio.ws";

    public GateSource() : base(TimeSpan.FromSeconds(8))
    {
    }

    public override string Id => "gate";

    public override string Label => "Gate.io";

    protected override string PairOf(string baseSymbol) => baseSymbol + "_USDT";

    protected override async Task<Dictionary<string, SourceQuote>> FetchCoreAsync(
        HttpClient http, IReadOnlyList<string> baseSymbols, CancellationToken ct)
    {
        var result = new Dictionary<string, SourceQuote>(StringComparer.OrdinalIgnoreCase);
        using var throttle = new SemaphoreSlim(4);

        var jobs = baseSymbols.Select(async baseSymbol =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var pair = PairOf(baseSymbol);
                var body = await GetAsync(http, $"{Api}/api/v4/spot/tickers?currency_pair={pair}", ct).ConfigureAwait(false);
                var rows = JsonSerializer.Deserialize(body, AppJsonContext.Default.ListGateTicker);
                var row = rows?.FirstOrDefault(r => !string.IsNullOrEmpty(r.Pair));
                if (row is null || row.Last is not > 0)
                {
                    return;
                }
                lock (result)
                {
                    result[baseSymbol] = new SourceQuote(row.Last, row.Pct24h, row.High24h, row.Low24h, row.QuoteVolume);
                }
            }
            catch (SourceHttpException http) when (http.Status is 400 or 404)
            {
                // pair not listed on Gate
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(jobs).ConfigureAwait(false);
        return result;
    }
}

// ---- CoinEx v2 spot ----

internal sealed class CoinExSource : ExchangeQuoteSource
{
    private const string Api = "https://api.coinex.com";

    public CoinExSource() : base(TimeSpan.FromSeconds(8))
    {
    }

    public override string Id => "coinex";

    public override string Label => "CoinEx";

    protected override string PairOf(string baseSymbol) => baseSymbol + "USDT";

    protected override async Task<Dictionary<string, SourceQuote>> FetchCoreAsync(
        HttpClient http, IReadOnlyList<string> baseSymbols, CancellationToken ct)
    {
        var result = new Dictionary<string, SourceQuote>(StringComparer.OrdinalIgnoreCase);
        var markets = string.Join(',', baseSymbols.Select(PairOf));
        var body = await GetAsync(http, $"{Api}/v2/spot/ticker?market={Uri.EscapeDataString(markets)}", ct).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize(body, AppJsonContext.Default.CoinExEnvelope);
        if (envelope is null || envelope.Code != 0)
        {
            throw new SourceHttpException(200, envelope?.Message ?? "unexpected CoinEx response");
        }

        foreach (var row in envelope.Data ?? [])
        {
            if (row.Last is not > 0
                || !row.Market.EndsWith("USDT", StringComparison.Ordinal)
                || row.Market.Length <= 4)
            {
                continue;
            }
            double? pct = row.Open is > 0 ? (row.Last - row.Open) / row.Open * 100 : null;
            result[row.Market[..^4]] = new SourceQuote(row.Last, pct, row.High, row.Low, row.QuoteVolume);
        }
        return result;
    }
}
