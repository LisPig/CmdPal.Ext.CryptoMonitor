// Chart page: the price trend as an inline image plus a text summary.
//
// The CmdPal extension API has no chart control, so the page hands the host a
// markdown inline image pointing at a locally rendered PNG (`file://` URLs are
// supported for markdown images) plus a MarkdownContent block with the numbers.
//
// It deliberately does NOT use IImageContent: on CmdPal builds older than
// 2026-07-17 the image content viewer crashes the whole command palette — see
// ChartCache for the details. Markdown images use a separate host loader that
// is not affected.
//
// GetContent() is synchronous, so the page renders through ChartCache: the dock
// prefetches one chart per watched coin, and a cold range is rendered on a
// background thread while the page shows "正在生成走势图…" — when the image
// lands, RaiseItemsChanged() makes the host ask for the content again.
//
// Identity is resolved lazily from MarketService: the dock builds its items
// before the first poll lands, so the watchlist token ("zcash") is only turned
// into its CoinGecko id / ticker ("ZEC") once quotes are in.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Pages;

internal sealed partial class CoinChartPage : ContentPage
{
    private readonly MarketService _service;
    private readonly CryptoMonitorSettings _settings;
    private readonly ChartCache _charts;
    private readonly string _token;
    private readonly string _symbolHint;
    private readonly string _nameHint;

    private ChartRange _range = ChartRange.Day1;
    private string? _imagePath;
    private bool _subscribed;

    public CoinChartPage(
        MarketService service,
        CryptoMonitorSettings settings,
        ChartCache charts,
        string tokenOrId,
        string? symbolHint,
        string? nameHint)
    {
        _service = service;
        _settings = settings;
        _charts = charts;
        _token = tokenOrId;
        _symbolHint = (symbolHint ?? tokenOrId).ToUpperInvariant();
        _nameHint = string.IsNullOrWhiteSpace(nameHint) ? _symbolHint : nameHint;

        Title = $"{Symbol} 走势";
        Icon = new IconInfo("\uE9D9");
        Commands = BuildCommands();
        Details = BuildDetails();

        _ = LoadAsync();
    }

    /// Visible range, switched from the page's context commands.
    public ChartRange Range
    {
        get => _range;
        set
        {
            if (_range == value)
            {
                return;
            }
            _range = value;
            Commands = BuildCommands();
            _ = LoadAsync();
        }
    }

    /// CoinGecko id once quotes are in, otherwise the raw watchlist token.
    private string Id => _service.ResolveId(_token);

    private string Symbol => _service.ById(Id)?.Symbol is { Length: > 0 } s ? s.ToUpperInvariant() : _symbolHint;

    private string Name => _service.ById(Id)?.Name is { Length: > 0 } n ? n : _nameHint;

    public override IContent[] GetContent()
    {
        EnsureSubscribed();
        SyncTitle();

        var entry = _charts.PathsFor(Id, _range);
        if (entry is not null && !string.Equals(entry.Path, _imagePath, StringComparison.Ordinal))
        {
            _imagePath = entry.Path;
        }

        return [new MarkdownContent(BuildSummary(_imagePath))];
    }

    private void SyncTitle()
    {
        var title = $"{Symbol} 走势";
        if (!string.Equals(Title, title, StringComparison.Ordinal))
        {
            Title = title;
        }
    }

    private string BuildSummary(string? chartPath)
    {
        var currency = _service.Currency;
        var quote = _service.ById(Id);
        var lines = new List<string>
        {
            $"### {Symbol} · {Name} · {_range.Label()}",
        };

        // The chart is a markdown inline image on purpose: `file://` images go
        // through the host's markdown image loader, which is independent of the
        // icon loader (see ChartCache for why that matters).
        if (chartPath is not null)
        {
            var uri = new Uri(chartPath).AbsoluteUri;
            var alt = $"{Symbol} {_range.Label()}价格走势";
            lines.Add($"![{alt}]({uri}?--x-cmdpal-maxwidth={ChartRenderer.Width}&--x-cmdpal-fit=fit)");
        }
        else
        {
            lines.Add("_正在生成走势图…_");
        }

        if (quote?.CurrentPrice is double price)
        {
            var pct = quote.Pct24h ?? quote.Pct24hInCurrency;
            lines.Add($"**{Format.Money(currency, price)}**  {Format.Percent(pct)}");
        }

        var parts = new List<string>();
        if (quote?.High24h is double high)
        {
            parts.Add($"高 {Format.Money(currency, high)}");
        }
        if (quote?.Low24h is double low)
        {
            parts.Add($"低 {Format.Money(currency, low)}");
        }
        if (_service.LastSuccessUtc is DateTime updated)
        {
            var source = _service.ActiveSource.Length > 0 ? _service.ActiveSource : "未知";
            parts.Add($"{source} · {Format.Age(DateTime.UtcNow - updated)}前");
        }
        if (parts.Count > 0)
        {
            lines.Add(string.Join(" · ", parts));
        }

        if (_service.LastError is { } error)
        {
            lines.Add($"⚠️ {error}");
        }

        lines.Add("_右键切换周期 / 复制价格_");
        return string.Join("\n\n", lines);
    }

    private async Task LoadAsync()
    {
        try
        {
            // Before the first poll the ticker of a non-canonical watchlist entry is
            // unknown ("zcash" is not yet "ZEC"), so an early attempt would query a
            // pair that does not exist and burn the failure backoff. Wait for quotes;
            // OnUpdated retries as soon as they arrive.
            if (_service.ById(Id) is null && _service.LastSuccessUtc is null)
            {
                return;
            }

            IsLoading = true;
            var rate = await _service.GetDisplayRateAsync().ConfigureAwait(false);
            var request = new ChartRequest(Id, Symbol, Symbol, Name, _range, _service.Currency, rate);
            await _charts.EnsureAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"chart page load failed ({_token}): {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            Details = BuildDetails();
            RaiseItemsChanged();
        }
    }

    private IDetails BuildDetails()
    {
        var currency = _service.Currency;
        var quote = _service.ById(Id);
        var metadata = new List<IDetailsElement>();

        if (quote?.High24h is double high)
        {
            metadata.Add(Element("24h 最高", Format.Money(currency, high)));
        }
        if (quote?.Low24h is double low)
        {
            metadata.Add(Element("24h 最低", Format.Money(currency, low)));
        }
        if (quote?.MarketCap is double cap)
        {
            metadata.Add(Element("市值", Format.BigNumber(cap) + (quote.MarketCapRank is int rank ? $" (#{rank})" : "")));
        }
        if (_service.LastSuccessUtc is DateTime updated)
        {
            metadata.Add(Element("数据源", $"{_service.ActiveSource} · {Format.Age(DateTime.UtcNow - updated)}前"));
        }

        return new Details
        {
            Title = $"{Symbol} · {Name}",
            Body = $"{_range.Label()}走势 · 交易所 K 线",
            Metadata = [.. metadata],
        };
    }

    private static IDetailsElement Element(string key, string value) => new DetailsElement
    {
        Key = key,
        Data = new DetailsTags { Tags = [new Tag(value)] },
    };

    private IContextItem[] BuildCommands()
    {
        var items = new List<IContextItem>
        {
            new CommandContextItem(new CopyPriceCommand(this))
            {
                Title = "复制价格",
                Icon = new IconInfo("\uE8C8"),
            },
        };

        foreach (var range in ChartRanges.All)
        {
            items.Add(new CommandContextItem(new SetRangeCommand(this, range))
            {
                Title = range == _range ? $"✓ {range.Label()}走势" : $"{range.Label()}走势",
                Icon = new IconInfo("\uE9D9"),
            });
        }

        var token = MarketService.WatchTokenFor(Id, Symbol.ToLowerInvariant());
        var isWatched = _settings.InWatchlist(token);
        items.Add(new CommandContextItem(new WatchToggleCommand(_settings, token, !isWatched, Name))
        {
            Title = isWatched ? "从监控列表移除" : "加入监控列表",
            Icon = new IconInfo(isWatched ? "\uE711" : "\uE710"),
        });

        items.Add(new CommandContextItem(new OpenUrlCommand($"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(Id)}"))
        {
            Title = "在 CoinGecko 打开",
            Icon = new IconInfo("\uE774"),
        });

        return [.. items];
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }
        _subscribed = true;
        _service.Updated += OnUpdated;
        _charts.Ready += OnChartReady;
    }

    private void OnUpdated()
    {
        RaiseItemsChanged();

        // First poll may have just taught us this coin's ticker — or the chart may
        // still be missing because an earlier attempt failed.
        if (_imagePath is null)
        {
            _ = LoadAsync();
        }
    }

    private void OnChartReady(string coinId)
    {
        if (string.Equals(coinId, Id, StringComparison.OrdinalIgnoreCase))
        {
            RaiseItemsChanged();
        }
    }

    private sealed class SetRangeCommand(CoinChartPage page, ChartRange range) : InvokableCommand
    {
        public override string Name => $"{range.Label()}走势";

        public override IconInfo Icon => new("\uE9D9");

        public override CommandResult Invoke()
        {
            page.Range = range;
            return CommandResult.KeepOpen();
        }
    }

    private sealed class CopyPriceCommand(CoinChartPage page) : InvokableCommand
    {
        public override string Name => "复制价格";

        public override IconInfo Icon => new("\uE8C8");

        public override CommandResult Invoke()
        {
            var quote = page._service.ById(page.Id);
            var symbol = page.Symbol;
            var text = quote?.CurrentPrice is double price
                ? $"{symbol} {Format.Money(page._service.Currency, price)} (24h {Format.Percent(quote.Pct24h ?? quote.Pct24hInCurrency)})"
                : $"{symbol} 价格不可用";
            if (Win32Clipboard.TrySetText(text))
            {
                new ToastStatusMessage($"已复制 {symbol} 价格").Show();
            }
            else
            {
                new ToastStatusMessage(text).Show();
            }
            return CommandResult.Hide();
        }
    }

    private sealed class WatchToggleCommand(CryptoMonitorSettings settings, string token, bool add, string label) : InvokableCommand
    {
        public override string Name => add ? "加入监控" : "移除监控";

        public override IconInfo Icon => new(add ? "\uE710" : "\uE711");

        public override CommandResult Invoke()
        {
            if (add)
            {
                settings.SetWatchSymbols([.. settings.WatchSymbols, token]);
            }
            else
            {
                settings.SetWatchSymbols(settings.WatchSymbols.Where(t => !t.Equals(token, StringComparison.OrdinalIgnoreCase)));
            }
            new ToastStatusMessage($"{(add ? "已加入监控列表" : "已从监控列表移除")}: {label}").Show();
            return CommandResult.KeepOpen();
        }
    }
}

/// <summary>One chart page per coin, shared by the dock band and the pages.</summary>
internal sealed class ChartPages(MarketService service, CryptoMonitorSettings settings, ChartCache charts)
{
    private readonly Dictionary<string, CoinChartPage> _pages = new(StringComparer.OrdinalIgnoreCase);

    public CoinChartPage For(string tokenOrId, string? symbol, string? name)
    {
        if (!_pages.TryGetValue(tokenOrId, out var page))
        {
            page = new CoinChartPage(service, settings, charts, tokenOrId, symbol, name);
            _pages[tokenOrId] = page;
        }
        return page;
    }
}
