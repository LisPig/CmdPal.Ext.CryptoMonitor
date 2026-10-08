// Downloads & disk-caches coin logos so item icons never depend on the host
// shell fetching remote http images. That host-side path silently breaks after
// an Explorer restart here: the shell's XAML image stack does not honor the
// system proxy, and assets.coingecko.com is unreachable without it.
//
// Icons are handed to the host as plain local file paths (the same mechanism
// IconHelpers.FromRelativePath uses for extension assets), which always render.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

public sealed partial class CoinIconCache : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".svg"];

    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal); // url -> local file
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);

    /// Raised on a thread-pool thread the first time a url becomes available on disk.
    public event Action<string>? Ready;

    public CoinIconCache()
    {
        // %ProgramData%\CryptoMonitor\icons — deliberately NOT a user-profile
        // folder:
        //  * This extension is loose-registered, so its "runFullTrust" is not
        //    honored: writes under the user profile get silently redirected into
        //    this package's private LocalCache. That location is visible to the
        //    extension but not reliably openable by the host shell, and the real
        //    profile folder stays empty. ProgramData writes land for real.
        //  * The host shell is a full-trust packaged app running as the same
        //    user, so it reads ProgramData like any other user file.
        //  * ProgramData survives Explorer restarts, host restarts AND package
        //    re-registration (unlike the package-scoped LocalCache).
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CryptoMonitor", "icons");
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            // non-fatal: probe/Download both re-check existence
        }

        // Default handler honors the Windows system proxy (.NET behavior already
        // relied on by CoinGeckoClient for the price API).
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CmdPal.Ext.CryptoMonitor/0.2");
    }

    /// Synchronous probe: local file path for <paramref name="url"/> when it is
    /// already cached on disk, otherwise null. Never touches the network.
    public string? IconPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsHttp(url))
        {
            return null;
        }

        if (_known.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var path = PathFor(url);
        if (File.Exists(path))
        {
            _known[url] = path;
            return path;
        }

        return null;
    }

    /// Fire-and-forget download. Surfaces re-probe with <see cref="IconPath"/>
    /// when <see cref="Ready"/> fires or on their next refresh cycle.
    public void Prefetch(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _ = DownloadAsync(url);
    }

    public void Dispose()
    {
        try
        {
            _http.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    // ---- internals ----

    private static bool IsHttp(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private string PathFor(string url)
    {
        var ext = ".png";
        try
        {
            var candidate = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (Array.IndexOf(ImageExts, candidate) >= 0)
            {
                ext = candidate;
            }
        }
        catch
        {
            // keep the default extension
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        return Path.Combine(_dir, hash + ext);
    }

    private async Task<string?> DownloadAsync(string url)
    {
        if (_inflight.TryGetValue(url, out Task<string?>? running))
        {
            return await running.ConfigureAwait(false);
        }

        var task = DownloadCoreAsync(url);
        _inflight.TryAdd(url, task);
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            // Only drop our own entry so a newer in-flight task stays cached.
            _inflight.TryRemove(new KeyValuePair<string, Task<string?>>(url, task));
        }
    }

    private async Task<string?> DownloadCoreAsync(string url)
    {
        try
        {
            var path = PathFor(url);
            if (File.Exists(path))
            {
                _known[url] = path;
                return path;
            }

            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
            {
                return null;
            }

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            _known[url] = path;
            Log.Info($"icon cached: {url} -> {path}");
            try
            {
                Ready?.Invoke(url);
            }
            catch
            {
                // subscriber errors must not break caching
            }

            return path;
        }
        catch
        {
            // offline / blocked / transient — simply retried on the next Prefetch
            return null;
        }
    }
}
