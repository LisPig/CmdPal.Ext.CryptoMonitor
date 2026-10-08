// Command provider: wires settings, market service, pages, fallback search and the dock band.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Pages;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;
using CmdPal.Ext.CryptoMonitor.Dock;

namespace CmdPal.Ext.CryptoMonitor;

public partial class CryptoMonitorCommandsProvider : CommandProvider
{
    public const string ProviderId = "dev.crypto.monitor";

    private readonly CryptoMonitorSettings _settings = new();
    private readonly MarketService _service = new();
    private readonly CoinIconCache _icons = new();
    private readonly ChartCache _charts = new();
    private readonly ChartPages _chartPages;
    private readonly MarketPage _mainPage;
    private readonly MarketPage _fallbackPage;
    private readonly Dictionary<string, PriceDockItem> _dockItems = new(StringComparer.OrdinalIgnoreCase);
    private WrappedDockItem? _dockBand;
    private ICommandItem[]? _topLevelCommands;
    private IFallbackCommandItem[]? _fallbackCommands;
    private int _chartCursor;

    public CryptoMonitorCommandsProvider()
    {
        DisplayName = "Crypto Monitor";
        Id = ProviderId;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Settings = _settings.Settings;

        _chartPages = new ChartPages(_service, _settings, _charts);
        _mainPage = new MarketPage(_service, _settings, _icons, _charts, _chartPages);
        _fallbackPage = new MarketPage(_service, _settings, _icons, _charts, _chartPages);

        _settings.Changed += OnSettingsChanged;
        _service.Updated += OnServiceUpdated;
        _icons.Ready += OnIconReady;
        _charts.Ready += OnChartReady;

        Log.Info("provider constructed");
        try
        {
            _service.Configure(_settings);
            _service.LogState("configured");
        }
        catch (Exception ex)
        {
            Log.Error($"Configure failed: {ex}");
        }
        RebuildDock();
        Log.Info($"dock items: {_dockItems.Count}");
    }

    // The host asks for these over and over (thousands of times a day). Every
    // fresh instance registers a PropChanged listener on its command, and the
    // command keeps that listener (and therefore the item) alive forever — the
    // dump showed ~1.6k of each still rooted after 8 hours. So: one instance,
    // created once. The items are stateless views of pages that live just as
    // long, so reusing them is what the host expects anyway.
    public override ICommandItem[] TopLevelCommands() =>
        _topLevelCommands ??=
        [
            new CommandItem(_mainPage) { Title = DisplayName },
        ];

    public override IFallbackCommandItem[] FallbackCommands() =>
        _fallbackCommands ??=
        [
            new FallbackCommandItem(_fallbackPage, "搜索加密货币行情", "dev.crypto.monitor.fallback"),
        ];

    public override ICommandItem[]? GetDockBands() =>
        _dockBand is null ? null : [_dockBand];

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _service.Configure(_settings);
        RebuildDock();
        RaiseItemsChanged();
    }

    private void OnServiceUpdated()
    {
        foreach (var item in _dockItems.Values)
        {
            item.Refresh();
        }
        PrefetchCharts();
    }

    /// Keeps charts warm so a dock click shows an image immediately instead of a
    /// "generating" placeholder.
    ///
    /// Only a couple of coins per poll cycle, rotating through the watchlist:
    /// re-rendering all of them every minute produced a new PNG per coin per
    /// minute for the host to load (and a 6 MB canvas for us). Each coin is now
    /// at most a few minutes stale, and opening its chart still refreshes it on
    /// the spot (CoinChartPage calls ChartCache.EnsureAsync itself).
    private void PrefetchCharts()
    {
        var tokens = _service.WatchTokens;
        if (tokens.Count == 0)
        {
            return;
        }

        var start = Interlocked.Increment(ref _chartCursor) - 1;
        _ = Task.Run(async () =>
        {
            try
            {
                var rate = await _service.GetDisplayRateAsync().ConfigureAwait(false);
                var currency = _service.Currency;
                var count = Math.Min(2, tokens.Count);
                for (var i = 0; i < count; i++)
                {
                    var token = tokens[((start + i) % tokens.Count + tokens.Count) % tokens.Count];
                    var quote = _service.QuoteForToken(token);
                    if (quote?.CurrentPrice is not > 0)
                    {
                        continue;
                    }
                    var symbol = quote.Symbol.ToUpperInvariant();
                    _charts.Prefetch(new ChartRequest(quote.Id, symbol, symbol, quote.Name, ChartRange.Day1, currency, rate));
                }
            }
            catch (Exception ex)
            {
                Log.Info($"chart prefetch failed: {ex.Message}");
            }
        });
    }

    // A coin chart was (re)rendered: refresh the dock so the details pane update
    // without waiting for the next price poll.
    private void OnChartReady(string coinId)
    {
        foreach (var item in _dockItems.Values)
        {
            item.Refresh();
        }
        RaiseItemsChanged();
    }

    // A coin logo finished downloading: refresh the dock so the icon appears
    // without waiting for the next price poll.
    private void OnIconReady(string url)
    {
        foreach (var item in _dockItems.Values)
        {
            item.Refresh();
        }
    }

    private void RebuildDock()
    {
        var symbols = _settings.WatchSymbols.Take(_settings.DockCoinCount).ToList();
        foreach (var (sym, item) in _dockItems.ToList())
        {
            if (!symbols.Contains(sym))
            {
                item.Dispose();
                _dockItems.Remove(sym);
            }
        }
        foreach (var sym in symbols)
        {
            if (!_dockItems.ContainsKey(sym))
            {
                var quote = _service.QuoteForToken(sym);
                var id = quote?.Id ?? sym;
                var display = quote?.Symbol ?? sym;
                var name = quote?.Name ?? sym.ToUpperInvariant();
                _dockItems[sym] = new PriceDockItem(_service, _settings, _icons, sym, _chartPages.For(id, display, name));
            }
        }
        foreach (var item in _dockItems.Values)
        {
            item.Refresh();
        }
        // Order must follow the watchlist order, NOT dictionary insertion order.
        var ordered = symbols.Where(s => _dockItems.ContainsKey(s)).Select(s => _dockItems[s]).ToArray();
        if (_dockBand is null)
        {
            _dockBand = new WrappedDockItem(ordered, "dev.crypto.monitor.prices", "Crypto Monitor");
        }
        else
        {
            // Keep the same band instance the host already holds; swap its items.
            _dockBand.Items = ordered;
        }
    }
}
