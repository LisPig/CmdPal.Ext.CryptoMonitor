// Typed wrapper over CmdPal extension settings (auto-persisted by the host)
// PLUS our own file-backed store in %LOCALAPPDATA%\CryptoMonitor so values
// survive extension reloads / package re-registration during development.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;

namespace CmdPal.Ext.CryptoMonitor.Settings;

public sealed class AlertRule
{
    public required string Symbol { get; init; }
    public bool IsAbove { get; init; }
    public double Threshold { get; init; }
}

public sealed class CryptoMonitorSettings
{
    private readonly Microsoft.CommandPalette.Extensions.Toolkit.Settings _settings = new();
    private readonly ChoiceSetSetting _currencySetting;
    private readonly TextSetting _watchlistSetting;
    private readonly TextSetting _refreshSetting;
    private readonly TextSetting _dockSetting;
    private readonly TextSetting _alertsSetting;
    private readonly ToggleSetting _alertsEnabledSetting;
    private readonly TextSetting _apiKeySetting;
    private readonly ChoiceSetSetting _dataSourceSetting;
    private readonly ToggleSetting _metaSetting;
    private bool _hydrating;

    public const string DefaultWatchlist = "btc,eth,sol,doge";

    public CryptoMonitorSettings()
    {
        _settings.SettingsChanged += (_, _) => OnChanged();

        _currencySetting = new ChoiceSetSetting(
            "currency", "显示货币", "价格以哪种法币显示(USD 或 CNY)",
            [new ChoiceSetSetting.Choice("USD ($)", "usd"), new ChoiceSetSetting.Choice("CNY (¥)", "cny")]);
        _settings.Add(_currencySetting);

        _watchlistSetting = new TextSetting(
            "watchlist", "监控列表", "要监控的币种符号/ID,逗号分隔(如 btc,eth,sol)",
            DefaultWatchlist);
        _settings.Add(_watchlistSetting);

        _refreshSetting = new TextSetting(
            "refreshSeconds", "刷新间隔(秒)", "行情轮询间隔,15–300 秒", "60");
        _settings.Add(_refreshSetting);

        _dockSetting = new TextSetting(
            "dockCoins", "Dock 显示币种数", "Dock 价格条显示前几个监控币(1–10)", "5");
        _settings.Add(_dockSetting);

        _alertsSetting = new TextSetting(
            "alerts", "价格提醒(全部)", "格式: 符号>价格 或 符号<价格,分号分隔;价格按显示货币计。例: btc>100000; eth<2500", "");
        _settings.Add(_alertsSetting);

        _alertsEnabledSetting = new ToggleSetting(
            "alertsEnabled", "启用价格提醒", "达到阈值时弹出通知(需要命令面板或 Dock 保持运行)", false);
        _settings.Add(_alertsEnabledSetting);

        _apiKeySetting = new TextSetting(
            "apiKey", "CoinGecko API Key(可选)", "留空使用匿名公共接口(限流较紧)。免费 key 在 coingecko.com 申请", "");
        _settings.Add(_apiKeySetting);

        _dataSourceSetting = new ChoiceSetSetting(
            "dataSource", "行情数据源",
            "默认走 Binance/Gate/CoinEx(国内可直连,不依赖代理);需要市值排名或只想用 CoinGecko 时可切换",
            [
                new ChoiceSetSetting.Choice("自动 · 交易所直连优先(推荐)", "auto"),
                new ChoiceSetSetting.Choice("仅 CoinGecko(需代理)", "coingecko"),
            ]);
        _settings.Add(_dataSourceSetting);

        _metaSetting = new ToggleSetting(
            "coinGeckoMeta", "用 CoinGecko 补充市值/排名/7日涨跌",
            "只补充交易所没有的字段,不参与价格;CoinGecko 不可达时自动退回", true);
        _settings.Add(_metaSetting);

        // Hydrate from our own disk store (survives host resets / package re-register).
        LoadFromDisk();
    }

    public ICommandSettings Settings => _settings;

    public event EventHandler? Changed;

    private void OnChanged()
    {
        if (!_hydrating)
        {
            SaveToDisk();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- disk persistence (independent of the host) ----

    private static string PrefsPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CryptoMonitor");
        return Path.Combine(dir, "prefs.ini");
    }

    private void LoadFromDisk()
    {
        _hydrating = true;
        try
        {
            var path = PrefsPath();
            if (!File.Exists(path))
            {
                return;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#'))
                {
                    continue;
                }
                var eq = t.IndexOf('=');
                if (eq > 0)
                {
                    map[t[..eq].Trim()] = t[(eq + 1)..].Trim();
                }
            }
            if (map.TryGetValue("currency", out var cur) && cur is "usd" or "cny")
            {
                _currencySetting.Value = cur;
            }
            if (map.TryGetValue("watchlist", out var wl) && wl.Length > 0)
            {
                _watchlistSetting.Value = wl;
            }
            if (map.TryGetValue("refreshSeconds", out var rs) && int.TryParse(rs, out _))
            {
                _refreshSetting.Value = rs;
            }
            if (map.TryGetValue("dockCoins", out var dc) && int.TryParse(dc, out _))
            {
                _dockSetting.Value = dc;
            }
            if (map.TryGetValue("alerts", out var al))
            {
                _alertsSetting.Value = al;
            }
            if (map.TryGetValue("alertsEnabled", out var ae) && bool.TryParse(ae, out var aeb))
            {
                _alertsEnabledSetting.Value = aeb;
            }
            if (map.TryGetValue("apiKey", out var ak))
            {
                _apiKeySetting.Value = ak;
            }
            if (map.TryGetValue("dataSource", out var ds) && ds is "auto" or "coingecko")
            {
                _dataSourceSetting.Value = ds;
            }
            if (map.TryGetValue("coinGeckoMeta", out var cm) && bool.TryParse(cm, out var cmb))
            {
                _metaSetting.Value = cmb;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"prefs load failed: {ex.Message}");
        }
        finally
        {
            _hydrating = false;
        }
        SaveToDisk(); // refresh the file with current canonical values
    }

    private void SaveToDisk()
    {
        try
        {
            var path = PrefsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new[]
            {
                "currency=" + Currency,
                "watchlist=" + WatchSymbolsRaw,
                "refreshSeconds=" + RefreshSeconds,
                "dockCoins=" + DockCoinCount,
                "alerts=" + AlertsRaw,
                "alertsEnabled=" + AlertsEnabled,
                "apiKey=" + ApiKey,
                "dataSource=" + _dataSourceSetting.Value,
                "coinGeckoMeta=" + UseCoinGeckoMeta,
            };
            File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Log.Error($"prefs save failed: {ex.Message}");
        }
    }

    private string WatchSymbolsRaw => _watchlistSetting.Value ?? DefaultWatchlist;
    private string AlertsRaw => _alertsSetting.Value ?? "";

    // ---- typed accessors ----

    public string Currency
    {
        get
        {
            var v = _currencySetting.Value;
            return string.IsNullOrWhiteSpace(v) ? "usd" : v.Trim().ToLowerInvariant();
        }
    }

    public string CurrencySymbol => Format.CurrencySymbol(Currency);

    public string ApiKey => _apiKeySetting.Value ?? "";

    /// Where quotes come from; see <see cref="QuoteMode"/>. Defaults to Auto.
    public QuoteMode QuoteMode =>
        string.Equals(_dataSourceSetting.Value, "coingecko", StringComparison.OrdinalIgnoreCase)
            ? QuoteMode.CoinGecko
            : QuoteMode.Auto;

    /// Let CoinGecko fill in market cap / rank / 7d change (never the price).
    public bool UseCoinGeckoMeta => _metaSetting.Value;

    public int RefreshSeconds
    {
        get
        {
            var raw = _refreshSetting.Value;
            return int.TryParse(raw, out var s) ? Math.Clamp(s, 15, 300) : 60;
        }
    }

    public int DockCoinCount
    {
        get
        {
            var raw = _dockSetting.Value;
            return int.TryParse(raw, out var n) ? Math.Clamp(n, 1, 10) : 5;
        }
    }

    /// Tokens in the watchlist, in order, trimmed, deduped.
    public IReadOnlyList<string> WatchSymbols
    {
        get
        {
            var raw = WatchSymbolsRaw;
            return raw.Split(',', ';', ' ', '\n', '\t')
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToArray();
        }
    }

    public bool AlertsEnabled => _alertsEnabledSetting.Value;

    public IReadOnlyList<AlertRule> AlertRules
    {
        get
        {
            var raw = AlertsRaw;
            var rules = new List<AlertRule>();
            foreach (var part in raw.Split(';', '\n'))
            {
                var p = part.Trim();
                if (p.Length == 0)
                {
                    continue;
                }
                int opIdx = p.IndexOf('<');
                if (opIdx < 0)
                {
                    opIdx = p.IndexOf('>');
                }
                if (opIdx < 1)
                {
                    continue; // malformed, skip
                }
                var sym = p[..opIdx].Trim().ToLowerInvariant();
                var isAbove = p[opIdx] == '>';
                var numPart = p[(opIdx + 1)..].Trim().Replace(",", "").Replace("$", "").Replace("¥", "");
                if (sym.Length == 0 || !double.TryParse(numPart, out var th))
                {
                    continue;
                }
                rules.Add(new AlertRule { Symbol = sym, IsAbove = isAbove, Threshold = th });
            }
            return rules;
        }
    }

    // ---- mutations ----

    public bool InWatchlist(string symbol) =>
        WatchSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase);

    /// Move a watchlist entry from indexFrom to indexTo (clamped).
    public void MoveWatchSymbol(int indexFrom, int indexTo)
    {
        var list = WatchSymbols.ToList();
        if (indexFrom < 0 || indexFrom >= list.Count)
        {
            return;
        }
        indexTo = Math.Clamp(indexTo, 0, list.Count - 1);
        if (indexFrom == indexTo)
        {
            return;
        }
        var token = list[indexFrom];
        list.RemoveAt(indexFrom);
        list.Insert(indexTo, token);
        _watchlistSetting.Value = string.Join(',', list);
        OnChanged();
    }

    public void MoveWatchTop(int index) => MoveWatchSymbol(index, 0);

    /// Replace the watchlist symbol list (used by add/remove context actions).
    public void SetWatchSymbols(IEnumerable<string> symbols)
    {
        var list = symbols.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (list.Count == 0)
        {
            list.Add("btc");
        }
        _watchlistSetting.Value = string.Join(',', list);
        OnChanged();
    }

    public bool HasRule(string token) => AlertRules.Any(r => r.Symbol == token);

    public AlertRule? RuleFor(string token) => AlertRules.FirstOrDefault(r => r.Symbol == token);

    /// Add/replace the alert rule for one coin (threshold in the display currency).
    public void SetRule(string token, bool isAbove, double threshold)
    {
        var kept = AlertRules.Where(r => !string.Equals(r.Symbol, token, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Symbol}{(r.IsAbove ? ">" : "<")}{FormatNum(r.Threshold)}");
        _alertsSetting.Value = string.Join(";", kept.Append($"{token}{(isAbove ? ">" : "<")}{FormatNum(threshold)}"));
        _alertsEnabledSetting.Value = true; // turning alerts on implicitly
        OnChanged();
    }

    public void RemoveRule(string token)
    {
        var kept = AlertRules.Where(r => !string.Equals(r.Symbol, token, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Symbol}{(r.IsAbove ? ">" : "<")}{FormatNum(r.Threshold)}");
        _alertsSetting.Value = string.Join(";", kept);
        OnChanged();
    }

    public string RuleText(AlertRule r)
    {
        var sym = CurrencySymbol;
        return $"{r.Symbol.ToUpperInvariant()} {(r.IsAbove ? ">" : "<")} {sym}{FormatNum(r.Threshold)}";
    }

    private static string FormatNum(double v)
    {
        if (Math.Abs(v) >= 1000)
        {
            return v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
        return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
