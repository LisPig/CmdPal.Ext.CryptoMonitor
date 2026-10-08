// Small shared command helpers.
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CmdPal.Ext.CryptoMonitor.Services;

internal static class Noop
{
    /// A command that keeps the palette open (used for read-only info rows).
    public static AnonymousCommand KeepOpen() => new(() => { }) { Result = CommandResult.KeepOpen() };
}
