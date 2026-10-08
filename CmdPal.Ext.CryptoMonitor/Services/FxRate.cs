// USD -> CNY rate for the exchange sources.
//
// Exchange APIs quote in USDT, so a CNY display needs a fiat rate. The two
// endpoints below are reachable directly from here (no key, no proxy). The rate
// is cached on disk for 12h: if the endpoints are unreachable we keep using the
// last known value instead of failing the whole poll.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal sealed class FxRateProvider : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private const string UserAgent = "CmdPal.Ext.CryptoMonitor/0.3";

    private readonly object _gate = new();
    private readonly string _path = Paths.DataFile("fx.json");
    private readonly HttpClient _direct = Network.CreateClient(UserAgent, TimeSpan.FromSeconds(8), ProxyMode.Direct);
    private readonly HttpClient _proxied = Network.CreateClient(UserAgent, TimeSpan.FromSeconds(10), ProxyMode.SystemProxy);
    private double? _usdCny;
    private DateTime _fetchedUtc = DateTime.MinValue;

    public FxRateProvider()
    {
        Load();
    }

    /// Last known USD->CNY rate, refreshing it when older than 12h. Returns null
    /// when no rate was ever obtained.
    public async Task<double?> UsdToCnyAsync(CancellationToken ct)
    {
        double? cached;
        lock (_gate)
        {
            cached = _usdCny;
            if (DateTime.UtcNow - _fetchedUtc <= MaxAge)
            {
                return cached;
            }
        }

        var fresh = await FetchAsync(ct).ConfigureAwait(false);
        if (fresh is not > 0)
        {
            return cached;
        }

        lock (_gate)
        {
            _usdCny = fresh;
            _fetchedUtc = DateTime.UtcNow;
        }
        Save();
        Log.Info($"fx: USD/CNY = {fresh:0.####}");
        return fresh;
    }

    public void Dispose()
    {
        _direct.Dispose();
        _proxied.Dispose();
    }

    private async Task<double?> FetchAsync(CancellationToken ct)
    {
        var endpoints = new (string Url, Func<string, double?> Parse)[]
        {
            (
                "https://open.er-api.com/v6/latest/USD",
                body =>
                {
                    var dto = JsonSerializer.Deserialize(body, AppJsonContext.Default.ErApiResponse);
                    return dto?.Rates is { } r && r.TryGetValue("CNY", out var v) ? v : null;
                }),
            (
                "https://api.frankfurter.dev/v1/latest?base=USD&symbols=CNY",
                body =>
                {
                    var dto = JsonSerializer.Deserialize(body, AppJsonContext.Default.FrankfurterResponse);
                    return dto?.Rates is { } r && r.TryGetValue("CNY", out var v) ? v : null;
                }),
        };

        foreach (var (url, parse) in endpoints)
        {
            foreach (var http in new[] { _direct, _proxied })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (parse(body) is > 0 and var rate)
                    {
                        return rate;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Info($"fx {new Uri(url).Host}: {ex.Message}");
                }
            }
        }
        return null;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var dto = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.FxCacheFile);
            if (dto?.UsdCny is > 0)
            {
                _usdCny = dto.UsdCny;
                _fetchedUtc = dto.UpdatedUtc.UtcDateTime;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"fx cache load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dto = new FxCacheFile { UsdCny = _usdCny ?? 0, UpdatedUtc = DateTime.UtcNow };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, AppJsonContext.Default.FxCacheFile));
        }
        catch (Exception ex)
        {
            Log.Error($"fx cache save failed: {ex.Message}");
        }
    }
}
