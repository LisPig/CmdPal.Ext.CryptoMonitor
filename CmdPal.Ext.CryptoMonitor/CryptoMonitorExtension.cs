// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace CmdPal.Ext.CryptoMonitor;

/// <summary>
/// Entry point handed to the Command Palette host: it is instantiated once per
/// extension process and asked for the <see cref="CryptoMonitorCommandsProvider"/>.
/// </summary>
[Guid("AD414658-83D0-450E-ADDF-CD54BDDF7331")]
public sealed partial class CryptoMonitorExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;

    private readonly CryptoMonitorCommandsProvider _provider = new();

    public CryptoMonitorExtension(ManualResetEvent extensionDisposedEvent)
    {
        this._extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.Commands => _provider,
            _ => null,
        };
    }

    public void Dispose() => this._extensionDisposedEvent.Set();
}
