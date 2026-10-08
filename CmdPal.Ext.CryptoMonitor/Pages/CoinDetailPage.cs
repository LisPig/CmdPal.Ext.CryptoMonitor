// Detail page for one coin: price rows + quick actions.
using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Pages;

internal sealed partial class CoinDetailPage : ListPage
{
    private readonly MarketService _service;
    private readonly CryptoMonitorSettings _settings;
    private readonly CoinIconCache _icons;
    private readonly ChartPages _chartPages;
    private readonly string _id;
    private readonly string _symbolLower;
    private readonly string _displayName;
    private readonly string? _imageUrl;
    private string? _pageIconPath;
    private bool _subscribed;

    public CoinDetailPage(MarketService service, CryptoMonitorSettings settings, CoinIconCache icons, ChartPages chartPages, string id, string symbol, string? displayName, string? imageUrl)
    {
        _service = service;
        _settings = settings;
        _icons = icons;
        _chartPages = chartPages;
        _id = id;
        _symbolLower = symbol.ToLowerInvariant();
        _displayName = string.IsNullOrWhiteSpace(displayName) ? symbol : displayName;
        _imageUrl = imageUrl;

        Title = _displayName;
        Name = "详情";

        if (!string.IsNullOrWhiteSpace(_imageUrl))
        {
            // Local file only — the host cannot reliably fetch remote http icons
            // (its image stack ignores the system proxy; see CoinIconCache).
            var iconPath = _icons.IconPath(_imageUrl);
            if (iconPath is not null)
            {
                _pageIconPath = iconPath;
                Icon = new IconInfo(iconPath);
            }
            else
            {
                Icon = new IconInfo("\uE821");
                _icons.Prefetch(_imageUrl);
            }
        }
    }

    public override IListItem[] GetItems()
    {
        EnsureSubscribedAndLoaded();
        var q = _service.ById(_id);
        var cur = _service.Currency;
        var sym = Format.CurrencySymbol(cur);

        // Upgrade the page icon once the logo is available locally (set-once to
        // avoid re-raising the change on every poll refresh).
        if (_pageIconPath is null && !string.IsNullOrWhiteSpace(_imageUrl) && _icons.IconPath(_imageUrl) is { } pageIcon)
        {
            _pageIconPath = pageIcon;
            Icon = new IconInfo(pageIcon);
        }

        if (q is null || !q.CurrentPrice.HasValue)
        {
            var msg = _service.LastError ?? "加载中…";
            return
            [
                new ListItem(Noop.KeepOpen())
                {
                    Title = "暂无数据",
                    Subtitle = msg,
                },
                new ListItem(new RetryCommand(_service)) { Title = "重试", Icon = new IconInfo("\uE72C") },
            ];
        }

        var price = Format.Money(cur, q.CurrentPrice.Value);
        var pct = q.Pct24h ?? q.Pct24hInCurrency;
        var pct7 = q.Pct7dInCurrency;
        var token = MarketService.WatchTokenFor(_id, _symbolLower);
        var isWatched = _settings.InWatchlist(token);

        // --- actions first so they are visible without scrolling ---
        var watchCmd = new WatchToggleCommand(_settings, token, !isWatched, _displayName);
        var copyCmd = new CopyTextCommand(price);
        var openCmd = new OpenUrlCommand($"https://www.coingecko.com/en/coins/{Uri.EscapeDataString(_id)}");

        var items = new System.Collections.Generic.List<IListItem>
        {
            new ListItem(_chartPages.For(_id, _symbolLower, _displayName))
            {
                Title = "📈 查看走势图",
                Subtitle = "价格曲线 · 可切换 1 小时 / 24 小时 / 7 天 / 30 天",
                Icon = new IconInfo("\uE9D9"),
            },
            new ListItem(watchCmd)
            {
                Title = isWatched ? "从监控列表移除" : "加入监控列表",
                Subtitle = "回车或双击执行 · 立即同步监控列表与 Dock",
                Icon = new IconInfo(isWatched ? "\uE711" : "\uE710"),
            },
            new ListItem(copyCmd) { Title = "复制价格", Icon = new IconInfo("\uE8C8") },
            new ListItem(openCmd) { Title = "在 CoinGecko 打开", Icon = new IconInfo("\uE774") },
        };

        // --- price alerts for THIS coin ---
        if (q.CurrentPrice is double now && now > 0)
        {
            var bell = new IconInfo("\uE91A");
            items.Add(new ListItem(Noop.KeepOpen())
            {
                Title = "价格提醒 ⏰",
                Subtitle = _settings.AlertsEnabled ? "达到阈值时弹出通知" : "开启后将自动启用通知",
            });
            var rule = _settings.RuleFor(token);
            if (rule is not null)
            {
                items.Add(new ListItem(Noop.KeepOpen())
                {
                    Title = "当前提醒",
                    Subtitle = _settings.RuleText(rule),
                    Icon = new IconInfo("\uEA8F"),
                });
                items.Add(new ListItem(new RemoveAlertCommand(_settings, token, _displayName))
                {
                    Title = "删除此币提醒",
                    Icon = new IconInfo("\uE711"),
                });
            }
            foreach (var (factor, above) in new[] { (1.05, true), (1.02, true), (0.98, false), (0.95, false) })
            {
                var threshold = now * factor;
                var deltaPct = Math.Abs(factor - 1) * 100;
                var label = above ? $"高于现价 {deltaPct:0}% 提醒" : $"低于现价 {deltaPct:0}% 提醒";
                items.Add(new ListItem(new AlertRuleCommand(_settings, token, above, threshold, _displayName))
                {
                    Title = label,
                    Subtitle = $"触发价约 {Format.Money(cur, threshold)}",
                    Icon = bell,
                });
            }
            items.Add(new ListItem(new CustomAlertPage(_settings, token, _displayName, Format.Money(cur, now)))
            {
                Title = "✏️ 自定义提醒价格…",
                Subtitle = $"现价 {Format.Money(cur, now)},可直接输入任意触发价",
            });
        }

        // --- price row with context menu ---
        var priceItem = new ListItem(Noop.KeepOpen())
        {
            Title = $"价格 ({sym})",
            Subtitle = price,
            Tags = pct is double p ? [PctTag(p)] : [],
            MoreCommands =
            [
                new CommandContextItem(watchCmd) { Title = isWatched ? "从监控列表移除" : "加入监控列表" },
                new CommandContextItem(copyCmd) { Title = "复制价格" },
                new CommandContextItem(openCmd) { Title = "在 CoinGecko 打开" },
            ],
        };
        if (!string.IsNullOrWhiteSpace(_imageUrl))
        {
            var iconPath = _icons.IconPath(_imageUrl);
            if (iconPath is not null)
            {
                priceItem.Icon = new IconInfo(iconPath);
            }
        }
        items.Add(priceItem);

        // --- stats ---
        items.Add(Row("24h 涨跌", Format.Percent(pct), pct is double p2 ? PctTag(p2) : null));
        if (pct7 is double p7)
        {
            items.Add(Row("7d 涨跌", Format.Percent(p7), p7 >= 0 ? PctTag(p7) : PctTag(p7)));
        }
        if (q.High24h.HasValue)
        {
            items.Add(Row($"24h 最高 ({sym})", Format.Money(cur, q.High24h.Value), null));
        }
        if (q.Low24h.HasValue)
        {
            items.Add(Row($"24h 最低 ({sym})", Format.Money(cur, q.Low24h.Value), null));
        }
        if (q.MarketCap.HasValue)
        {
            items.Add(Row("市值", Format.CurrencySymbol(cur) + Format.BigNumber(q.MarketCap.Value) + (q.MarketCapRank is int r ? $"  (#{r})" : ""), null));
        }
        if (q.TotalVolume.HasValue)
        {
            items.Add(Row("24h 成交量", Format.CurrencySymbol(cur) + Format.BigNumber(q.TotalVolume.Value), null));
        }

        // --- provenance: which source produced these prices, and how old they are ---
        if (_service.LastSuccessUtc is DateTime updated)
        {
            var age = DateTime.UtcNow - updated;
            var source = _service.ActiveSource.Length > 0 ? _service.ActiveSource : "未知";
            items.Add(Row("数据源", $"{source} · {Format.Age(age)}前更新", null));
        }
        if (_service.LastError is { } pollError)
        {
            items.Add(Row("⚠️ 最近一次刷新失败", pollError + (" (以上为缓存价格)"), null));
        }

        return items.ToArray();
    }

    private static ListItem Row(string label, string value, Tag? tag)
    {
        var item = new ListItem(Noop.KeepOpen())
        {
            Title = label,
            Subtitle = value,
        };
        if (tag is not null)
        {
            item.Tags = [tag];
        }
        return item;
    }

    private static Tag PctTag(double pct) => new(pct >= 0 ? $"▲ {pct:0.00}%" : $"▼ {pct:0.00}%")
    {
        Foreground = pct >= 0 ? ColorHelpers.FromRgb(22, 163, 74) : ColorHelpers.FromRgb(220, 38, 38),
    };

    private void EnsureSubscribedAndLoaded()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _service.Updated += OnUpdated;
            _settings.Changed += OnSettingsChanged;
            if (_service.ById(_id) is null)
            {
                _ = _service.EnsureDetailAsync(_id);
            }
        }
    }

    private void OnUpdated()
    {
        RaiseItemsChanged();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RaiseItemsChanged();
    }

    private sealed class RetryCommand(MarketService service) : InvokableCommand
    {
        public override string Name => "重试";
        public override IconInfo Icon => new("\uE72C");
        public override CommandResult Invoke()
        {
            _ = service.PollNowAsync();
            return CommandResult.KeepOpen();
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

    private sealed class AlertRuleCommand(CryptoMonitorSettings settings, string token, bool above, double threshold, string label) : InvokableCommand
    {
        public override string Name => above ? "设置高于提醒" : "设置低于提醒";
        public override IconInfo Icon => new("\uE91A");
        public override CommandResult Invoke()
        {
            settings.SetRule(token, above, threshold);
            new ToastStatusMessage($"已设置提醒:{label} {(above ? ">" : "<")} {threshold:0.##} (现货币)").Show();
            return CommandResult.KeepOpen();
        }
    }

    private sealed class RemoveAlertCommand(CryptoMonitorSettings settings, string token, string label) : InvokableCommand
    {
        public override string Name => "删除提醒";
        public override IconInfo Icon => new("\uE711");
        public override CommandResult Invoke()
        {
            settings.RemoveRule(token);
            new ToastStatusMessage($"已删除 {label} 的提醒").Show();
            return CommandResult.KeepOpen();
        }
    }
}
