// Top-level dynamic page: empty query => watchlist, typing => CoinGecko search.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Pages;

internal sealed partial class MarketPage : DynamicListPage
{
    private readonly MarketService _service;
    private readonly CryptoMonitorSettings _settings;
    private readonly CoinIconCache _icons;
    private readonly ChartCache _charts;
    private readonly ChartPages _chartPages;
    private readonly Dictionary<string, CoinDetailPage> _detailPages = new(StringComparer.OrdinalIgnoreCase);
    private IListItem[] _items = [];
    private string _query = string.Empty;
    private CoinSearchResult[] _searchResults = []; // last successful search, for icon refreshes
    private string _searchQuery = string.Empty;
    private CancellationTokenSource _cts = new();
    private bool _subscribed;

    public MarketPage(
        MarketService service,
        CryptoMonitorSettings settings,
        CoinIconCache icons,
        ChartCache charts,
        ChartPages chartPages)
    {
        _service = service;
        _settings = settings;
        _icons = icons;
        _charts = charts;
        _chartPages = chartPages;

        Title = "Crypto Monitor";
        Name = "打开";
        PlaceholderText = "输入符号或名称搜索币种(如 btc / bitcoin);留空查看监控列表…";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        _query = newSearch.Trim();
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        if (_query.Length == 0)
        {
            _items = BuildWatchItems();
            RaiseItemsChanged();
            return;
        }

        // Immediately drop the stale (previous-screen) list so Enter can never
        // activate an old row while the debounced search is still in flight.
        _items =
        [
            new ListItem(Noop.KeepOpen()) { Title = $"正在搜索 “{_query}”…", Icon = new IconInfo("\uE721") },
        ];
        RaiseItemsChanged();

        // Debounced async search (CoinGecko round trip).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                var results = await _service.SearchCoinsAsync(_query, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var items = new List<IListItem>
                {
                    new ListItem(new ClearSearchCommand(this))
                    {
                        Title = "← 返回监控列表",
                        Subtitle = "清空搜索词,回到 Watchlist",
                        Icon = new IconInfo("\uE72B"),
                    },
                };
                foreach (var r in results)
                {
                    items.Add(SearchItem(r));
                }
                if (items.Count == 1)
                {
                    items.Add(new ListItem(Noop.KeepOpen())
                    {
                        Title = $"没有找到 “{_query}” 相关的币种",
                        Subtitle = "试试英文符号或全名,如 btc 或 ethereum",
                    });
                }
                _searchResults = results.ToArray();
                _searchQuery = _query;
                foreach (var r in _searchResults)
                {
                    _icons.Prefetch(r.Image); // start logo downloads; rows already probe the cache
                }
                _items = items.ToArray();
                RaiseItemsChanged();
            }
            catch (OperationCanceledException)
            {
                // stale search, ignore
            }
            catch (Exception ex)
            {
                _items =
                [
                    new ListItem(Noop.KeepOpen()) { Title = "搜索失败", Subtitle = ex.Message },
                    new ListItem(new ClearSearchCommand(this)) { Title = "← 返回监控列表", Icon = new IconInfo("\uE72B") },
                ];
                RaiseItemsChanged();
            }
        }, token);
    }

    public override IListItem[] GetItems() => _items;

    private IListItem[] BuildWatchItems()
    {
        EnsureSubscribed();
        var tokens = _service.WatchTokens;
        var list = new List<IListItem>(tokens.Count + 1);
        var stale = StaleSuffix();

        if (tokens.Count == 0)
        {
            if (_service.LastError is { } err)
            {
                list.Add(new ListItem(new RetryCommand(_service))
                {
                    Title = "⚠️ 行情获取失败",
                    Subtitle = err + stale,
                    Icon = new IconInfo("\uE7BA"),
                });
            }
            else
            {
                list.Add(new ListItem(Noop.KeepOpen())
                {
                    Title = "监控列表为空",
                    Subtitle = "输入币种符号搜索,或在详情里“加入监控列表”;也可在扩展设置里修改监控列表",
                    Icon = new IconInfo("\uE8CB"),
                });
            }
            return list.ToArray();
        }

        // A failed refresh is worth its own row: the prices below are then the
        // cached ones, which the age suffix spells out.
        if (_service.LastError is { } pollError)
        {
            list.Add(new ListItem(new RetryCommand(_service))
            {
                Title = "⚠️ 行情更新失败",
                Subtitle = pollError + stale,
                Icon = new IconInfo("\uE7BA"),
            });
        }

        // Freshly added coins have no quote yet until the next poll; show a
        // placeholder row immediately so the watchlist visibly updates.
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var q = _service.QuoteForToken(token);
            if (q?.CurrentPrice is double px)
            {
                var page = DetailFor(q.Id, q.Symbol, q.Name, q.Image);
                var sym = q.Symbol.ToUpperInvariant();
                var pct = q.Pct24h ?? q.Pct24hInCurrency;
                var rank = q.MarketCapRank is int r ? $"  #{r}" : "";
                var item = new ListItem(page)
                {
                    Title = $"{sym} · {q.Name}",
                    Subtitle = $"{Format.Money(_service.Currency, px)}  {Format.Percent(pct)}{rank}{stale}",
                    MoreCommands = RowActions(token, i, tokens.Count),
                };
                if (pct is double p)
                {
                    item.Tags =
                    [
                        new Tag(p >= 0 ? $"▲ {p:0.00}%" : $"▼ {p:0.00}%")
                        {
                            Foreground = p >= 0 ? ColorHelpers.FromRgb(22, 163, 74) : ColorHelpers.FromRgb(220, 38, 38),
                        },
                    ];
                }
                if (!string.IsNullOrWhiteSpace(q.Image))
                {
                    var iconPath = _icons.IconPath(q.Image);
                    if (iconPath is not null)
                    {
                        item.Icon = new IconInfo(iconPath);
                    }
                    else
                    {
                        item.Icon = new IconInfo("\uE821"); // glyph until the logo is cached locally
                        _icons.Prefetch(q.Image);
                    }
                }

                // The chart shows up in the details pane as soon as it has been
                // rendered (the dock keeps one warm per watched coin).
                item.Details = RowDetails(q, sym, item.Subtitle ?? "");
                list.Add(item);
            }
            else
            {
                list.Add(new ListItem(Noop.KeepOpen())
                {
                    Title = DisplayToken(token),
                    Subtitle = "已加入,等待行情刷新…",
                    Icon = new IconInfo("\uE821"),
                    MoreCommands = RowActions(token, i, tokens.Count),
                });
            }
        }
        return list.ToArray();
    }

    /// Details pane for a watchlist row: the numbers the row has no space for.
    ///
    /// Deliberately text-only. A Details.HeroImage would be a second, unverified
    /// image path into the host (the chart itself is shown through the markdown
    /// image loader, see CoinChartPage), and one host crash from the icon loader
    /// is enough.
    private Details RowDetails(CoinMarketQuote quote, string symbol, string subtitle)
    {
        var currency = _service.Currency;
        var metadata = new List<IDetailsElement>();
        if (quote.High24h is double high)
        {
            metadata.Add(Element("24h 最高", Format.Money(currency, high)));
        }
        if (quote.Low24h is double low)
        {
            metadata.Add(Element("24h 最低", Format.Money(currency, low)));
        }
        if (quote.MarketCap is double cap)
        {
            metadata.Add(Element("市值", Format.BigNumber(cap)));
        }
        if (quote.TotalVolume is double volume)
        {
            metadata.Add(Element("24h 成交", Format.BigNumber(volume)));
        }

        return new Details
        {
            Title = $"{symbol} · {quote.Name}",
            Body = subtitle,
            Metadata = [.. metadata],
        };
    }

    private static IDetailsElement Element(string key, string value) => new DetailsElement
    {
        Key = key,
        Data = new DetailsTags { Tags = [new Tag(value)] },
    };

    /// "  ·  21 分钟前" while the displayed quotes are older than a couple of
    /// minutes; empty when the data is current.
    private string StaleSuffix()
    {
        if (_service.LastSuccessUtc is not DateTime last)
        {
            return "";
        }
        var age = DateTime.UtcNow - last;
        return age < TimeSpan.FromMinutes(2) ? "" : $"  ·  {Format.Age(age)}前的数据";
    }

    /// Token -> friendly display while we still lack its quote (id vs symbol).
    private static string DisplayToken(string token) => token.ToUpperInvariant();

    /// Right-click (…/MoreCommands) actions for one watchlist row.
    private IContextItem[] RowActions(string token, int index, int count)
    {
        var actions = new System.Collections.Generic.List<IContextItem>();
        if (index > 0)
        {
            actions.Add(new CommandContextItem(new WatchMoveCommand(_settings, index, index - 1))
            {
                Title = "上移",
                Icon = new IconInfo("\uE70E"),
            });
            actions.Add(new CommandContextItem(new WatchMoveCommand(_settings, index, 0))
            {
                Title = "置顶",
                Icon = new IconInfo("\uE77A"),
            });
        }
        if (index < count - 1)
        {
            actions.Add(new CommandContextItem(new WatchMoveCommand(_settings, index, index + 1))
            {
                Title = "下移",
                Icon = new IconInfo("\uE70D"),
            });
        }
        actions.Add(new CommandContextItem(new WatchRemoveCommand(_settings, token))
        {
            Title = "移除监控",
            Icon = new IconInfo("\uE711"),
            IsCritical = true,
        });
        return [.. actions];
    }

    private ListItem SearchItem(CoinSearchResult r)
    {
        var page = DetailFor(r.Id, r.Symbol, r.Name, r.Image);
        var sym = r.Symbol.ToUpperInvariant();
        var subtitle = r.Rank is int rank ? $"市值排名 #{rank}" : r.Name;
        if (r.Quote?.CurrentPrice is double price)
        {
            subtitle = $"{Format.Money(_service.Currency, price)}  {Format.Percent(r.Quote.Pct24h ?? r.Quote.Pct24hInCurrency)}  #{r.Rank}";
        }
        var item = new ListItem(page)
        {
            Title = $"{sym} · {r.Name}",
            Subtitle = subtitle,
        };
        if (!string.IsNullOrWhiteSpace(r.Image))
        {
            var iconPath = _icons.IconPath(r.Image);
            if (iconPath is not null)
            {
                item.Icon = new IconInfo(iconPath);
            }
            else
            {
                item.Icon = new IconInfo("\uE821"); // glyph until the logo is cached locally
                _icons.Prefetch(r.Image);
            }
        }
        return item;
    }

    /// Rebuild the search-result list (return row + matches) from cached results.
    private IListItem[] BuildSearchList(IReadOnlyList<CoinSearchResult> results)
    {
        var items = new List<IListItem>
        {
            new ListItem(new ClearSearchCommand(this))
            {
                Title = "← 返回监控列表",
                Subtitle = "清空搜索词,回到 Watchlist",
                Icon = new IconInfo("\uE72B"),
            },
        };
        foreach (var r in results)
        {
            items.Add(SearchItem(r));
        }
        if (items.Count == 1)
        {
            items.Add(new ListItem(Noop.KeepOpen())
            {
                Title = $"没有找到 “{_query}” 相关的币种",
                Subtitle = "试试英文符号或全名,如 btc 或 ethereum",
            });
        }
        return items.ToArray();
    }

    private CoinDetailPage DetailFor(string id, string symbol, string? name, string? image)
    {
        if (!_detailPages.TryGetValue(id, out var page))
        {
            page = new CoinDetailPage(_service, _settings, _icons, _chartPages, id, symbol, name, image);
            _detailPages[id] = page;
        }
        return page;
    }

    private void EnsureSubscribed()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _service.Updated += OnUpdated;
            _settings.Changed += OnSettingsChanged;
            _icons.Ready += OnIconReady;
            _charts.Ready += OnChartReady;
        }
    }

    // A chart finished rendering: the details pane hero image appears without
    // waiting for the next price poll.
    private void OnChartReady(string coinId)
    {
        if (_query.Length == 0)
        {
            RefreshWatchItems();
        }
    }

    // A coin logo finished downloading — refresh whichever surface shows it so
    // it appears without waiting for the next price poll.
    private void OnIconReady(string url)
    {
        if (_query.Length == 0)
        {
            RefreshWatchItems();
            return;
        }

        if (string.Equals(_query, _searchQuery, StringComparison.Ordinal)
            && _searchResults.Any(r => string.Equals(r.Image, url, StringComparison.Ordinal)))
        {
            _items = BuildSearchList(_searchResults);
            RaiseItemsChanged();
        }
    }

    private void OnUpdated()
    {
        // Only the watch area (empty query) depends on background polls.
        if (_query.Length == 0)
        {
            RefreshWatchItems();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // A coin was added/removed — reflect it immediately (placeholders until the poll lands).
        if (_query.Length == 0)
        {
            RefreshWatchItems();
        }
    }

    public void RefreshWatchItems()
    {
        _items = BuildWatchItems();
        RaiseItemsChanged();
    }

    /// Drop the search text and show the watchlist again (used by the return row).
    public void ClearSearchToWatchlist()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _query = string.Empty;
        _items = BuildWatchItems();
        RaiseItemsChanged();
        try
        {
            SearchText = string.Empty; // also clear the palette search box if writable
        }
        catch
        {
            // best effort only
        }
    }

    private sealed class ClearSearchCommand(MarketPage page) : InvokableCommand
    {
        public override string Name => "返回监控列表";
        public override IconInfo Icon => new("\uE72B");
        public override CommandResult Invoke()
        {
            page.ClearSearchToWatchlist();
            return CommandResult.KeepOpen();
        }
    }

    private sealed class WatchMoveCommand(CryptoMonitorSettings settings, int from, int to) : InvokableCommand
    {
        public override string Name => "排序";
        public override IconInfo Icon => new("\uE8CB");
        public override CommandResult Invoke()
        {
            settings.MoveWatchSymbol(from, to);
            return CommandResult.KeepOpen();
        }
    }

    private sealed class WatchRemoveCommand(CryptoMonitorSettings settings, string token) : InvokableCommand
    {
        public override string Name => "移除监控";
        public override IconInfo Icon => new("\uE711");
        public override CommandResult Invoke()
        {
            settings.SetWatchSymbols(settings.WatchSymbols.Where(t => !t.Equals(token, StringComparison.OrdinalIgnoreCase)));
            new ToastStatusMessage($"已从监控列表移除: {token.ToUpperInvariant()}").Show();
            return CommandResult.KeepOpen();
        }
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
}
