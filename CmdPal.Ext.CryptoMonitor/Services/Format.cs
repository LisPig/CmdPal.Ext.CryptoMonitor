// Display helpers (money/percent formatting shared by pages and the dock).
using System;
using System.Globalization;

namespace CmdPal.Ext.CryptoMonitor.Services;

public static class Format
{
    public static string CurrencySymbol(string currency) => currency.Equals("cny", StringComparison.OrdinalIgnoreCase) ? "¥" : "$";

    public static string Money(string currency, double v)
    {
        var sym = CurrencySymbol(currency);
        string num;
        if (v >= 1_000_000_000)
        {
            num = (v / 1_000_000_000d).ToString("0.###", CultureInfo.InvariantCulture) + "B";
        }
        else if (v >= 1_000_000)
        {
            num = (v / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture) + "M";
        }
        else if (v >= 1_000)
        {
            num = v.ToString("#,##0", CultureInfo.InvariantCulture);
        }
        else if (v >= 1)
        {
            num = v.ToString("0.00", CultureInfo.InvariantCulture);
        }
        else if (v >= 0.01)
        {
            num = v.ToString("0.0000", CultureInfo.InvariantCulture);
        }
        else
        {
            num = v.ToString("0.######", CultureInfo.InvariantCulture);
        }
        return sym + num;
    }

    public static string Percent(double? p) =>
        p is double v ? $"{(v >= 0 ? "+" : "")}{v.ToString("0.00", CultureInfo.InvariantCulture)}%" : "—";

    public static string BigNumber(double v)
    {
        var abs = Math.Abs(v);
        string n = abs >= 1e12 ? (v / 1e12).ToString("0.##") + "T"
            : abs >= 1e9 ? (v / 1e9).ToString("0.##") + "B"
            : abs >= 1e6 ? (v / 1e6).ToString("0.##") + "M"
            : abs >= 1e3 ? (v / 1e3).ToString("0.##") + "K"
            : v.ToString("0", CultureInfo.InvariantCulture);
        return n;
    }

    /// "21 分钟" / "3 小时" — how old the displayed price data is.
    public static string Age(TimeSpan age) => age.TotalDays >= 1
        ? $"{(int)age.TotalDays} 天"
        : age.TotalHours >= 1
            ? $"{(int)age.TotalHours} 小时"
            : age.TotalMinutes >= 1
                ? $"{(int)age.TotalMinutes} 分钟"
                : $"{Math.Max(1, (int)age.TotalSeconds)} 秒";
}
