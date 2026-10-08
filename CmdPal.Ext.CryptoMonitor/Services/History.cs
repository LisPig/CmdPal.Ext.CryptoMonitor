// Price history (OHLC) for the chart.
//
// History comes from the same free, directly reachable endpoints as the prices —
// Binance Vision first, Gate.io and CoinEx as fallbacks. No chart image service
// and no API key is involved: the chart is drawn locally (see ChartRenderer).
//
// Candles are cached on disk for a couple of minutes because a 24-point line
// does not change meaningfully faster than that, and the dock prefetches one
// chart per watched coin.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

public sealed record Candle(long TimeMs, double Open, double High, double Low, double Close);

public enum ChartRange
{
    Hour1,
    Day1,
    Week1,
    Month1,
}

internal static class ChartRanges
{
    public static readonly ChartRange[] All =
        [ChartRange.Hour1, ChartRange.Day1, ChartRange.Week1, ChartRange.Month1];

    public static string Interval(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => "1m",
        ChartRange.Day1 => "1h",
        ChartRange.Week1 => "4h",
        _ => "1d",
    };

    public static string CoinExPeriod(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => "1min",
        ChartRange.Day1 => "1hour",
        ChartRange.Week1 => "4hour",
        _ => "1day",
    };

    /// Number of candles that covers the range at the interval above.
    public static int Limit(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => 60,
        ChartRange.Day1 => 24,
        ChartRange.Week1 => 42,
        _ => 30,
    };

    public static string Label(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => "1 小时",
        ChartRange.Day1 => "24 小时",
        ChartRange.Week1 => "7 天",
        _ => "30 天",
    };

    public static string Key(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => "1h",
        ChartRange.Day1 => "24h",
        ChartRange.Week1 => "7d",
        _ => "30d",
    };

    /// Short ASCII tag drawn on the chart itself (the bitmap font has no CJK).
    public static string Tag(this ChartRange range) => range switch
    {
        ChartRange.Hour1 => "1H",
        ChartRange.Day1 => "24H",
        ChartRange.Week1 => "7D",
        _ => "30D",
    };
}

internal sealed class HistoryProvider : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private const string UserAgent = "CmdPal.Ext.CryptoMonitor/0.4";

    private readonly HttpClient _direct = Network.CreateClient(UserAgent, TimeSpan.FromSeconds(8), ProxyMode.Direct);
    private readonly HttpClient _proxied = Network.CreateClient(UserAgent, TimeSpan.FromSeconds(10), ProxyMode.SystemProxy);
    private readonly string _dir;

    public HistoryProvider()
    {
        _dir = Paths.SubDir("history");
    }

    /// Candles for a base ticker (BTC), oldest first. Returns the last cached set
    /// when every source is unreachable, so the chart degrades to stale data
    /// instead of disappearing.
    public async Task<IReadOnlyList<Candle>> GetAsync(string baseSymbol, ChartRange range, CancellationToken ct)
    {
        var symbol = (baseSymbol ?? string.Empty).Trim().ToUpperInvariant();
        if (symbol.Length == 0)
        {
            return [];
        }

        var cached = Load(symbol, range, out var cachedUtc);
        if (cached.Count > 1 && DateTime.UtcNow - cachedUtc <= MaxAge)
        {
            return cached;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        foreach (var http in new[] { _direct, _proxied })
        {
            foreach (var source in new[] { "binance", "gate", "coinex" })
            {
                try
                {
                    var fresh = source switch
                    {
                        "binance" => await FetchBinanceAsync(http, symbol, range, budget.Token).ConfigureAwait(false),
                        "gate" => await FetchGateAsync(http, symbol, range, budget.Token).ConfigureAwait(false),
                        _ => await FetchCoinExAsync(http, symbol, range, budget.Token).ConfigureAwait(false),
                    };
                    if (fresh.Count > 1)
                    {
                        Save(symbol, range, fresh);
                        Log.Info($"history {symbol} {range.Key()}: {fresh.Count} candles from {source}");
                        return fresh;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log.Info($"history {symbol} {range.Key()}: budget exhausted");
                    return cached;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Info($"history {source} {symbol} {range.Key()} failed: {Short(ex)}");
                }
            }
        }

        Log.Info($"history {symbol} {range.Key()}: all sources failed, using {cached.Count} cached candles");
        return cached;
    }

    public void Dispose()
    {
        _direct.Dispose();
        _proxied.Dispose();
    }

    // ---- sources ----

    /// Binance klines: [[openTime, open, high, low, close, volume, …], …]
    private static async Task<List<Candle>> FetchBinanceAsync(HttpClient http, string symbol, ChartRange range, CancellationToken ct)
    {
        var url = $"https://data-api.binance.vision/api/v3/klines?symbol={symbol}USDT" +
                  $"&interval={range.Interval()}&limit={range.Limit()}";
        var body = await GetAsync(http, url, ct).ConfigureAwait(false);

        var candles = new List<Candle>();
        using var doc = JsonDocument.Parse(body);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.GetArrayLength() < 5)
            {
                continue;
            }
            candles.Add(new Candle(
                TimeMs(row[0], seconds: false),
                Price(row[1]),
                Price(row[2]),
                Price(row[3]),
                Price(row[4])));
        }
        return candles;
    }

    /// Gate candlesticks: [[unixSeconds, quoteVolume, close, high, low, open, …], …]
    private static async Task<List<Candle>> FetchGateAsync(HttpClient http, string symbol, ChartRange range, CancellationToken ct)
    {
        var url = $"https://api.gateio.ws/api/v4/spot/candlesticks?currency_pair={symbol}_USDT" +
                  $"&interval={range.Interval()}&limit={range.Limit()}";
        var body = await GetAsync(http, url, ct).ConfigureAwait(false);

        var candles = new List<Candle>();
        using var doc = JsonDocument.Parse(body);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.GetArrayLength() < 6)
            {
                continue;
            }
            candles.Add(new Candle(
                TimeMs(row[0], seconds: true),
                Price(row[5]), // open
                Price(row[3]), // high
                Price(row[4]), // low
                Price(row[2]))); // close
        }
        return candles;
    }

    /// CoinEx v2 klines: {"code":0,"data":[{"created_at","open","high","low","close"}, …]}
    private static async Task<List<Candle>> FetchCoinExAsync(HttpClient http, string symbol, ChartRange range, CancellationToken ct)
    {
        var url = $"https://api.coinex.com/v2/spot/kline?market={symbol}USDT" +
                  $"&period={range.CoinExPeriod()}&limit={range.Limit()}";
        var body = await GetAsync(http, url, ct).ConfigureAwait(false);

        var candles = new List<Candle>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            return candles;
        }
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            return candles;
        }
        foreach (var row in data.EnumerateArray())
        {
            var t = Prop(row, "created_at");
            var o = Prop(row, "open");
            var h = Prop(row, "high");
            var l = Prop(row, "low");
            var c = Prop(row, "close");
            if (o is null || h is null || l is null || c is null)
            {
                continue;
            }
            candles.Add(new Candle((long)(t ?? 0), o.Value, h.Value, l.Value, c.Value));
        }
        return candles;
    }

    private static double? Prop(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ParseDouble(value) : null;

    /// Binance mixes numbers and numeric strings in the same array. Gate reports
    /// seconds, Binance and CoinEx milliseconds.
    private static long TimeMs(JsonElement element, bool seconds)
    {
        var value = ParseDouble(element) ?? 0;
        return (long)(seconds ? value * 1000d : value);
    }

    private static double Price(JsonElement element) => ParseDouble(element) ?? 0;

    private static double? ParseDouble(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null,
        _ => null,
    };

    private static async Task<string> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // ---- disk cache (one CSV per symbol+range) ----

    private string PathFor(string symbol, ChartRange range) =>
        Path.Combine(_dir, $"{symbol}-{range.Key()}.csv");

    private List<Candle> Load(string symbol, ChartRange range, out DateTime updatedUtc)
    {
        updatedUtc = DateTime.MinValue;
        var candles = new List<Candle>();
        try
        {
            var path = PathFor(symbol, range);
            if (!File.Exists(path))
            {
                return candles;
            }
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith('#'))
                {
                    if (DateTime.TryParse(
                            line[1..],
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var when))
                    {
                        updatedUtc = when;
                    }
                    continue;
                }
                var parts = line.Split(';');
                if (parts.Length < 5
                    || !long.TryParse(parts[0], out var t)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var o)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                    || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
                    || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
                {
                    continue;
                }
                candles.Add(new Candle(t, o, h, l, c));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"history cache load failed ({symbol} {range.Key()}): {ex.Message}");
        }
        return candles;
    }

    private void Save(string symbol, ChartRange range, List<Candle> candles)
    {
        try
        {
            var lines = new List<string>(candles.Count + 1)
            {
                "#" + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            };
            foreach (var c in candles.OrderBy(c => c.TimeMs))
            {
                lines.Add(string.Join(';',
                    c.TimeMs.ToString(CultureInfo.InvariantCulture),
                    c.Open.ToString("R", CultureInfo.InvariantCulture),
                    c.High.ToString("R", CultureInfo.InvariantCulture),
                    c.Low.ToString("R", CultureInfo.InvariantCulture),
                    c.Close.ToString("R", CultureInfo.InvariantCulture)));
            }
            var tmp = PathFor(symbol, range) + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, PathFor(symbol, range), overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"history cache save failed ({symbol} {range.Key()}): {ex.Message}");
        }
    }

    private static string Short(Exception ex)
    {
        var msg = ex.Message.ReplaceLineEndings(" ");
        return msg.Length > 110 ? msg[..110] : msg;
    }
}
