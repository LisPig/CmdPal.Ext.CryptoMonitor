// HTTP plumbing shared by every data source.
//
// Two things are handled here that the plain `new HttpClient()` path got wrong:
//
// 1. Connection lifetime. The default handler keeps pooled connections forever,
//    so after a reboot (or a proxy/VPN restart) every request can sit on a dead
//    socket until the full timeout expires. `PooledConnectionLifetime` makes the
//    handler recycle them, which is what turned "no prices for 25 minutes after
//    boot" into "next poll recovers".
//
// 2. Proxy policy, per source. `HttpClient.DefaultProxy` is a process-wide
//    static that reads the registry once and caches it forever; a proxy client
//    that starts late (or moves to another port) is invisible to it. We read the
//    WinINET settings ourselves and build an explicit proxy, so callers can also
//    ask for a direct connection when they know the endpoint is reachable
//    without one — which is the case for the exchange APIs used here, whose
//    availability is exactly what removes the proxy from the critical path.
using System;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal enum ProxyMode
{
    /// Ignore any system proxy. Used by the exchange sources: they are reachable
    /// directly here, so they keep working even when the proxy client is down.
    Direct,

    /// Follow the Windows system proxy when one is configured (needed for hosts
    /// that are blocked from this network, e.g. api.coingecko.com).
    SystemProxy,
}

internal readonly record struct SystemProxyInfo(bool Enabled, string Server, string Pac)
{
    public static readonly SystemProxyInfo None = new(false, "", "");
}

internal static class Network
{
    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// Current WinINET proxy configuration (what Clash/v2ray/… write when they
    /// take over "system proxy"). Never throws.
    public static SystemProxyInfo ReadSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
            if (key is null)
            {
                return SystemProxyInfo.None;
            }
            var enabled = key.GetValue("ProxyEnable") is int i && i != 0;
            var server = key.GetValue("ProxyServer") as string ?? "";
            var pac = key.GetValue("AutoConfigURL") as string ?? "";
            return new SystemProxyInfo(enabled, server.Trim(), pac.Trim());
        }
        catch (Exception ex)
        {
            Log.Error($"read system proxy failed: {ex.Message}");
            return SystemProxyInfo.None;
        }
    }

    /// Changes whenever the OS proxy configuration changes; callers compare it to
    /// decide whether their cached client is still valid.
    public static string ProxySignature()
    {
        var p = ReadSystemProxy();
        return $"{p.Enabled}|{p.Server}|{p.Pac}";
    }

    public static HttpClient CreateClient(string userAgent, TimeSpan timeout, ProxyMode mode)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
        };

        if (mode == ProxyMode.Direct)
        {
            handler.UseProxy = false;
        }
        else
        {
            var info = ReadSystemProxy();
            var uri = ParseProxyUri(info.Server);
            if (info.Enabled && uri is not null)
            {
                // Explicit proxy instance: this bypasses the cached static
                // DefaultProxy and follows the registry from poll to poll.
                handler.Proxy = new WebProxy(uri);
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
                if (info.Enabled && info.Pac.Length > 0)
                {
                    // PAC scripts cannot be evaluated by .NET. A proxy client in
                    // "PAC only" mode is therefore invisible to us; direct is the
                    // best remaining option.
                    Log.Info($"PAC proxy '{info.Pac}' cannot be evaluated here; using a direct connection");
                }
            }
        }

        var http = new HttpClient(handler) { Timeout = timeout };
        if (!string.IsNullOrEmpty(userAgent))
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
        return http;
    }

    /// "127.0.0.1:7890", "http://127.0.0.1:7890" and
    /// "http=1.2.3.4:8080;https=1.2.3.4:8081" all appear in the registry.
    private static Uri? ParseProxyUri(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var candidate = raw.Trim();
        if (candidate.Contains('='))
        {
            string? pick = null;
            foreach (var part in candidate.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var scheme = part[..eq].Trim();
                if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    pick = part[(eq + 1)..].Trim();
                    break;
                }
                if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    pick = part[(eq + 1)..].Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(pick))
            {
                return null;
            }
            candidate = pick;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;
    }
}
