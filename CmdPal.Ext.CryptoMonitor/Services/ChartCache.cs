// Renders and caches the chart PNGs that the chart page displays.
//
// Same shape as CoinIconCache: surfaces probe synchronously for a local file
// path (the host renders images from local files, and its image pipeline caches
// by path), while the actual network + rendering work happens on a background
// thread that reports back through Ready.
//
// The file name includes a hash of the candle data, so a refreshed chart is a
// *new path* — which is what makes the host pick up the new image instead of
// reusing the one it already loaded for that path.
//
// NOTE: charts are shown through a markdown inline image, never through
// IImageContent. On CmdPal builds older than 2026-07-17 the image content viewer
// requests an icon at "natural" size (Size.Empty, whose width/height are
// negative infinity) and the host scales it anyway -> ArgumentOutOfRangeException
// on the UI thread -> the whole command palette exits. See PowerToys #49385
// ("Prevent scaling empty icon size"). Markdown images use a completely
// separate loader (Helpers/MarkdownImageProviders) and are unaffected.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal sealed record ChartRequest(
    string CoinId,
    string BaseSymbol,
    string DisplaySymbol,
    string Name,
    ChartRange Range,
    string Currency,
    double Rate);

internal sealed class ChartCache : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    // Background (dock warm-up) renders are allowed to be a few minutes older:
    // the chart only becomes visible when its coin is clicked, and the page
    // renders on demand anyway (see CoinChartPage). Without this the dock's
    // rotate-through-the-watchlist prefetch re-rendered ~3 charts a minute.
    private static readonly TimeSpan PrefetchMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FileRetention = TimeSpan.FromMinutes(20);

    private readonly HistoryProvider _history = new();
    private readonly string _dir = Paths.SubDir("charts");
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _failures = new(StringComparer.OrdinalIgnoreCase);

    // One render at a time: a chart needs a 6 MB supersampled canvas, and the
    // dock asked for every watched coin at once (seven canvases, ~50 MB peak,
    // all of it LOH). Serializing also keeps the rendering CPU off the UI.
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private DateTime _lastPruneUtc = DateTime.MinValue;

    /// Raised (on a thread-pool thread) when a coin's chart becomes available.
    public event Action<string>? Ready;

    internal sealed record Entry(string Path, DateTime RenderedUtc, double Rate);

    /// Local PNG paths for an already rendered chart, or null when none exists yet.
    public Entry? PathsFor(string coinId, ChartRange range) =>
        _entries.TryGetValue(Key(coinId, range), out var entry) ? entry : null;

    /// Fire and forget: render if missing or stale.
    public void Prefetch(ChartRequest request) => _ = EnsureAsync(request, PrefetchMaxAge);

    /// Makes sure a chart exists for the request (up to two minutes old);
    /// returns the cache key.
    public Task<string?> EnsureAsync(ChartRequest request) => EnsureAsync(request, MaxAge);

    private Task<string?> EnsureAsync(ChartRequest request, TimeSpan maxAge)
    {
        var key = Key(request.CoinId, request.Range);
        if (_entries.TryGetValue(key, out var entry)
            && DateTime.UtcNow - entry.RenderedUtc < maxAge
            && Math.Abs(entry.Rate - request.Rate) < 0.000001)
        {
            return Task.FromResult<string?>(key);
        }
        if (_failures.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < FailureBackoff)
        {
            return Task.FromResult<string?>(null);
        }

        // The placeholder is registered *before* the work starts. Doing this with
        // GetOrAdd(key, _ => RenderAsync(...)) looks equivalent but is not: a
        // render that finishes quickly can run its completion (and its removal)
        // before GetOrAdd stores the task, leaving a finished task in the map
        // forever — which would silently stop every later refresh.
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _inflight.GetOrAdd(key, completion.Task);
        if (!ReferenceEquals(running, completion.Task))
        {
            return running;
        }

        // Never tied to a caller's token: the work is shared, and a UI cancellation
        // must not kill a render another surface is waiting for.
        _ = Task.Run(async () =>
        {
            string? result = null;
            try
            {
                await _renderGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    result = await RenderCoreAsync(request, key).ConfigureAwait(false);
                }
                finally
                {
                    _renderGate.Release();
                }
            }
            catch (Exception ex)
            {
                _failures[key] = DateTime.UtcNow;
                Log.Error($"chart {request.DisplaySymbol} {request.Range.Key()} failed: {ex.Message}");
            }
            finally
            {
                completion.TrySetResult(result);
                _inflight.TryRemove(key, out _);
            }
        });
        return completion.Task;
    }

    public void Dispose()
    {
        _history.Dispose();
        _renderGate.Dispose();
    }

    private async Task<string?> RenderCoreAsync(ChartRequest request, string key)
    {
        var candles = await _history.GetAsync(request.BaseSymbol, request.Range, CancellationToken.None).ConfigureAwait(false);
        if (candles.Count < 2)
        {
            _failures[key] = DateTime.UtcNow;
            Log.Info($"chart {request.DisplaySymbol} {request.Range.Key()}: no candles");
            return null;
        }

        var hash = Hash(candles, request);
        var slug = Slug(request.CoinId);
        var png = Path.Combine(_dir, $"{slug}-{request.Range.Key()}-{hash}.png");

        if (!File.Exists(png))
        {
            Write(png, candles, new ChartStyle(request.Currency, request.Rate, ChartTheme.Transparent, request.Range.Tag()));
            Log.Info($"chart rendered: {request.DisplaySymbol} {request.Range.Key()} {request.Currency} {candles.Count} candles -> {png}");
        }

        _entries[key] = new Entry(png, DateTime.UtcNow, request.Rate);
        _failures.TryRemove(key, out _);
        Prune();

        try
        {
            Ready?.Invoke(request.CoinId);
        }
        catch
        {
            // subscriber errors must not break rendering
        }
        return key;
    }

    private static void Write(string path, IReadOnlyList<Candle> candles, ChartStyle style)
    {
        var tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024))
        {
            ChartRenderer.RenderPngTo(file, candles, style);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// One image per candle set: a new path forces the host to reload it.
    private static string Hash(IReadOnlyList<Candle> candles, ChartRequest request)
    {
        var sb = new StringBuilder()
            .Append(request.CoinId).Append('|').Append(request.Range.Key()).Append('|')
            .Append(request.Currency).Append('|').Append(request.Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(candles.Count).Append('|')
            .Append(candles[0].TimeMs).Append('|')
            .Append(candles[^1].TimeMs).Append('|')
            .Append(candles[^1].Close.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
    }

    private static string Slug(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length == 0 ? "coin" : slug.ToLowerInvariant();
    }

    private static string Key(string coinId, ChartRange range) => $"{coinId}|{range.Key()}";

    /// Rendered PNGs are dead weight once the next one is written; the host may
    /// still be showing an old one, so keep a generous window before deleting.
    /// Files the host still has open cannot be deleted (its image loader opens
    /// them without delete sharing) — that is expected, not an error.
    private void Prune()
    {
        if (DateTime.UtcNow - _lastPruneUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }
        _lastPruneUtc = DateTime.UtcNow;
        var removed = 0;
        var inUse = 0;
        try
        {
            var cutoff = DateTime.UtcNow - FileRetention;
            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles("*.png"))
            {
                if (file.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    inUse++;
                }
                catch (UnauthorizedAccessException)
                {
                    inUse++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"chart cache prune skipped: {ex.Message}");
        }

        if (removed > 0 || inUse > 0)
        {
            Log.Info($"chart cache prune: {removed} removed, {inUse} still in use");
        }
    }
}
