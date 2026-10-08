# Crypto Monitor · a PowerToys Command Palette extension

> Live crypto prices on your Command Palette dock: glance at the numbers, click for the chart.

[中文](./README.md) · **English**

`CmdPal.Ext.CryptoMonitor` is an extension for the [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) (CmdPal). It adds a market page, coin search, price charts and price alerts to the palette, and turns every watched coin into a persistent dock button.

It idles at roughly 30 MB, talks to public price APIs directly (no proxy needed) and keeps everything on disk for offline display.

---

## Features

- **Dock band** — one always-visible button per watched coin with its price and 24h change; click to open the chart
- **Watchlist page** — price, 24h change, market-cap rank, 24h high/low and volume per row, with a "data from N minutes ago" marker instead of silently stale numbers
- **Charts** — 1h / 24h / 7d / 30d, rendered locally as PNG (no chart service, no extra dependency)
- **Coin search** — by symbol or name; falls back to a local catalog of ~22k coins, so search works offline
- **Price alerts** — above/below a threshold (in your display currency) as a system toast, re-armed automatically when the price crosses back
- **Multiple quote sources** — Binance Vision → Gate.io → CoinEx, all reachable directly, with CoinGecko for market cap, rank, logos and search
- **USD / CNY** — prices are fetched in USDT and converted for display; falls back to CoinGecko's native CNY quotes when no FX rate is available
- **On-disk cache** — last quotes, coin catalog, candles and logos, so a reboot shows prices instead of an empty list

## Install

### Option 1: download the release (.msix)

1. Download `CmdPal.Ext.CryptoMonitor_x.y.z.msix` and `dev.cer` from [Releases](../../releases)
2. Trust the self-signed development certificate (once):

   ```powershell
   Import-Certificate -FilePath .\dev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```

3. Install:

   ```powershell
   Add-AppxPackage .\CmdPal.Ext.CryptoMonitor_0.1.0.0.msix
   ```

> Requires Windows 11 and PowerToys with the Command Palette enabled. The package is framework-dependent, so the **.NET 10 runtime** is required (`winget install Microsoft.DotNet.Runtime.10`).

### Option 2: build from source

Requires the .NET 10 SDK, PowerToys and Developer Mode.

```powershell
git clone https://github.com/LisPig/CmdPal.Ext.CryptoMonitor.git
cd CmdPal.Ext.CryptoMonitor
powershell -ExecutionPolicy Bypass -File build-deploy.ps1
```

The script publishes, generates the manifest, packs and signs the MSIX with a local self-signed certificate and loose-registers it. Run **Reload** in the Command Palette (or restart PowerToys) afterwards.

## Usage

- **Open it**: search `Crypto Monitor` in the palette, or just type a coin symbol (that hits the "search crypto prices" fallback item)
- **Dock**: add the Crypto Monitor band in the palette's dock settings
- **Settings**: currency, watchlist (`btc,eth,sol,doge`; non-mainstream coins can be CoinGecko ids such as `zcash`), refresh interval (15–300 s), dock coin count (1–10), alerts (`btc>100000; eth<2500`), quote source and optional CoinGecko demo API key

## Privacy

Only public market endpoints are called; no user data is sent anywhere. Local files: `%LOCALAPPDATA%\CryptoMonitor` (settings, quote cache, ~22k coin catalog, chart/candle cache), `%ProgramData%\CryptoMonitor\icons` (logos, so the host shell can read them), `%TEMP%\CryptoMonitor-cmdpal.log` (rotating log).

## Implementation notes

- Runs **out-of-process** as an MSIX-packaged COM server registered through `windows.comServer` + the `com.microsoft.commandpalette` app extension
- `Services/ChartRenderer.cs` rasterises the chart itself (3× supersampling, 5×7 bitmap font) and writes PNG/zlib by hand: no third-party graphics dependency, with pooled buffers as the render is serialized
- Memory: command items are created once (the toolkit's `WeakEventListener` strongly references its item, so re-creating them leaks), chart prefetch rotates through the watchlist, `System.GC.ConserveMemory` plus an idle `EmptyWorkingSet` keeps the process at ~30 MB
- `Services/Network.cs` reads the WinINET proxy settings itself: exchanges are called directly, CoinGecko goes through the system proxy, and the clients are rebuilt when the proxy moves

## Naming

Project, assembly and namespace are `CmdPal.Ext.CryptoMonitor` (mirroring the official `Microsoft.CmdPal.Ext.*` layout); the package identity is `LisPig.CmdPal.Ext.CryptoMonitor` (`vendor.product`, keeps it unique). The palette shows **Crypto Monitor**.

## License

MIT. Bootstrapped from the PowerToys Command Palette extension template (template parts © Microsoft, MIT).
