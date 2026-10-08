// One live-updating dock button per watched coin; clicking opens its chart.
using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Dock;

internal sealed class PriceDockItem : ListItem, IDisposable
{
    private readonly MarketService _service;
    private readonly CryptoMonitorSettings _settings;
    private readonly CoinIconCache _icons;
    private readonly string _symbol;
    private string? _imageUrl;
    private string? _assignedPath;

    /// <param name="chartCommand">Opened on click — the coin's chart page.</param>
    public PriceDockItem(
        MarketService service,
        CryptoMonitorSettings settings,
        CoinIconCache icons,
        string symbol,
        ICommand chartCommand)
        : base(chartCommand)
    {
        _service = service;
        _settings = settings;
        _icons = icons;
        _symbol = symbol.ToLowerInvariant();
        Title = _symbol.ToUpperInvariant();
        Subtitle = "…";
        Icon = new IconInfo("\uE821"); // generic currency/coin glyph fallback
        MoreCommands =
        [
            new CommandContextItem(new DockCopyCommand(service, settings, symbol))
            {
                Title = "复制价格",
                Icon = new IconInfo("\uE8C8"),
            },
        ];
    }

    public void Refresh()
    {
        var quote = _service.QuoteForToken(_symbol);
        Title = _symbol.ToUpperInvariant();
        if (quote?.CurrentPrice is double p)
        {
            var pct = quote.Pct24h ?? quote.Pct24hInCurrency;
            Subtitle = $"{Format.Money(_service.Currency, p)} {Format.Percent(pct)}";
        }
        else
        {
            Subtitle = _service.LastError is null ? "…" : "—";
        }

        // Coin logo: cache to a local file first, then point the host at that
        // file. Never hand the host a remote http URL — its own image fetch
        // does not use the system proxy and breaks after an Explorer restart.
        var img = quote?.Image;
        if (string.IsNullOrEmpty(img))
        {
            return;
        }

        if (!string.Equals(img, _imageUrl, StringComparison.Ordinal))
        {
            _imageUrl = img;
            _assignedPath = null; // new source image: force re-assign below
        }

        var path = _icons.IconPath(img);
        if (path is not null && !string.Equals(path, _assignedPath, StringComparison.Ordinal))
        {
            _assignedPath = path;
            try
            {
                Icon = new IconInfo(path);
            }
            catch
            {
                // keep the previous icon on failure
            }
        }
        else if (path is null)
        {
            _icons.Prefetch(img);
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class DockCopyCommand : InvokableCommand
{
    private readonly MarketService _service;
    private readonly string _symbol;

    public DockCopyCommand(MarketService service, CryptoMonitorSettings settings, string symbol)
    {
        _service = service;
        _symbol = symbol.ToLowerInvariant();
        Id = $"dev.crypto.monitor.dock.{_symbol}";
    }

    public override string Name => "复制价格";

    public override IconInfo Icon => new("\uE8C8");

    public override CommandResult Invoke()
    {
        var quote = _service.QuoteForToken(_symbol);
        var sym = _symbol.ToUpperInvariant();
        var text = quote?.CurrentPrice is double p
            ? $"{sym} {Format.Money(_service.Currency, p)} (24h {Format.Percent(quote.Pct24h ?? quote.Pct24hInCurrency)})"
            : $"{sym} 价格不可用";
        if (Win32Clipboard.TrySetText(text))
        {
            new ToastStatusMessage($"已复制 {sym} 价格").Show();
        }
        else
        {
            new ToastStatusMessage(text).Show();
        }
        return CommandResult.Hide();
    }
}
