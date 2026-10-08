// Tiny file logger — the extension host has no console, so write diagnostics to %TEMP%.
// The file rotates at 1 MB (one generation kept) because it grows forever otherwise.
using System;
using System.IO;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "CryptoMonitor-cmdpal.log");
    private static int _writes;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                if (_writes++ % 100 == 0)
                {
                    Rotate();
                }
                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // never let logging break the extension
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }
            var previous = Path + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }
            File.Move(Path, previous);
        }
        catch
        {
            // rotation is best effort
        }
    }
}
