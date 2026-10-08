// Minimal Win32 clipboard writer (works without an owned foreground window).
using System;
using System.Runtime.InteropServices;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal static class Win32Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        // UTF-16 payload incl. terminating NUL.
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        IntPtr handle = IntPtr.Zero;
        bool success = false;
        try
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }
            try
            {
                handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    return false;
                }
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                GlobalUnlock(handle);
                if (!EmptyClipboard())
                {
                    return false;
                }
                success = SetClipboardData(CF_UNICODETEXT, handle) != IntPtr.Zero;
                return success;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            // On success the system owns the memory; free it only when it failed.
            if (!success && handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }
}
