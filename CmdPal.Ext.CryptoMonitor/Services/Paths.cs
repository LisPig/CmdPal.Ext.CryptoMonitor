// Where the extension keeps its own state files (catalog snapshot, quote cache, fx rate).
//
// NOTE: this is deliberately a user-profile location. These files are only ever
// read back by the extension itself, and MSIX redirects the writes into the
// package's private LocalCache, which the extension can always re-read. Files
// the *host shell* has to open (coin logos) live in %ProgramData% instead — see
// CoinIconCache for why.
using System;
using System.IO;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal static class Paths
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string DataDir
    {
        get
        {
            lock (Gate)
            {
                if (_dir is not null)
                {
                    return _dir;
                }
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CryptoMonitor");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    // callers tolerate a missing directory (they re-check on use)
                }
                _dir = dir;
                return dir;
            }
        }
    }

    public static string DataFile(string name) => Path.Combine(DataDir, name);

    /// A named subdirectory of the data dir (created on demand).
    public static string SubDir(string name)
    {
        var dir = Path.Combine(DataDir, name);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // callers tolerate a missing directory
        }
        return dir;
    }
}
