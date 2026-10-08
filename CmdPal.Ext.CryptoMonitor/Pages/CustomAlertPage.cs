// Let the user type an arbitrary trigger price (palette search box acts as the input).
using System;
using System.Globalization;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using CmdPal.Ext.CryptoMonitor.Services;
using CmdPal.Ext.CryptoMonitor.Settings;

namespace CmdPal.Ext.CryptoMonitor.Pages;

internal sealed partial class CustomAlertPage : DynamicListPage
{
    private readonly CryptoMonitorSettings _settings;
    private readonly string _token;
    private readonly string _label;
    private readonly string _currentText;
    private string _raw = string.Empty;

    public CustomAlertPage(CryptoMonitorSettings settings, string token, string label, string currentText)
    {
        _settings = settings;
        _token = token;
        _label = label;
        _currentText = currentText;
        Title = $"提醒价格 · {_label}";
        Name = "设置";
        PlaceholderText = $"输入触发价(如 86000),按 {_settings.CurrencySymbol} 计";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        _raw = newSearch.Trim();
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var sym = _settings.CurrencySymbol;
        var parsed = TryParsePrice(_raw, out var value);
        if (!parsed || value <= 0)
        {
            return
            [
                new ListItem(Noop.KeepOpen())
                {
                    Title = "请输入触发价",
                    Subtitle = $"直接输入数字,例如 86000、86,600 或 70000.5(单位:{sym}),然后选择上方/下方触发",
                    Icon = new IconInfo("\uE8F1"),
                },
            ];
        }

        var pretty = FormatMoney(_settings.Currency, value);
        return
        [
            new ListItem(new SetAlertCommand(_settings, _token, true, value, _label))
            {
                Title = $"确认:价格高于 {pretty} 时提醒",
                Subtitle = "达到或超过即触发",
                Icon = new IconInfo("\uE91A"),
            },
            new ListItem(new SetAlertCommand(_settings, _token, false, value, _label))
            {
                Title = $"确认:价格低于 {pretty} 时提醒",
                Subtitle = "跌到该价即触发",
                Icon = new IconInfo("\uE91A"),
            },
            new ListItem(Noop.KeepOpen())
            {
                Title = $"当前价格 {_currentText}",
                Subtitle = "删掉重输即可修改;Esc 返回详情",
                Icon = new IconInfo("\uE72B"),
            },
        ];
    }

    private static bool TryParsePrice(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        var cleaned = raw.Replace(",", "").Replace("$", "").Replace("¥", "").Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static string FormatMoney(string currency, double v) =>
        Services.Format.Money(currency, v);

    private sealed class SetAlertCommand(CryptoMonitorSettings settings, string token, bool above, double value, string label) : InvokableCommand
    {
        public override string Name => above ? "设置高于提醒" : "设置低于提醒";
        public override IconInfo Icon => new("\uE91A");
        public override CommandResult Invoke()
        {
            settings.SetRule(token, above, value);
            new ToastStatusMessage($"已设置提醒:{label} {(above ? "高于" : "低于")} {Services.Format.Money(settings.Currency, value)}").Show();
            return CommandResult.GoBack();
        }
    }
}
