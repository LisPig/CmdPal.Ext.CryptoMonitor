// Last known good quotes on disk.
//
// This is what turns a failed poll after a reboot into "prices with an age
// marker" instead of an empty panel. Icons already had a disk cache; prices did
// not, which made every network hiccup look like a broken extension.
//
// Values are stored in the currency they were converted to, so the cache is
// ignored (not silently mislabelled) when the display currency changed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal sealed class QuoteCache
{
    private readonly object _gate = new();
    private readonly string _path = Paths.DataFile("quotes.json");

    public sealed record Snapshot(DateTime UpdatedUtc, string Source, List<CoinMarketQuote> Quotes);

    public Snapshot? Load(string currency)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }
            var dto = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.QuoteCacheFile);
            if (dto is null || dto.Quotes.Count == 0)
            {
                return null;
            }
            if (!string.Equals(dto.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"quote cache ignored: stored in {dto.Currency}, now showing {currency}");
                return null;
            }
            return new Snapshot(dto.UpdatedUtc.UtcDateTime, dto.Source, dto.Quotes);
        }
        catch (Exception ex)
        {
            Log.Error($"quote cache load failed: {ex.Message}");
            return null;
        }
    }

    public void Save(string currency, string source, IEnumerable<CoinMarketQuote> quotes)
    {
        try
        {
            var list = new List<CoinMarketQuote>();
            foreach (var q in quotes)
            {
                if (q.CurrentPrice is > 0 && !string.IsNullOrEmpty(q.Id))
                {
                    list.Add(q);
                }
            }
            if (list.Count == 0)
            {
                return;
            }

            var dto = new QuoteCacheFile
            {
                Currency = currency,
                Source = source,
                UpdatedUtc = DateTime.UtcNow,
                Quotes = list,
            };
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.QuoteCacheFile);
            lock (_gate)
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"quote cache save failed: {ex.Message}");
        }
    }
}
