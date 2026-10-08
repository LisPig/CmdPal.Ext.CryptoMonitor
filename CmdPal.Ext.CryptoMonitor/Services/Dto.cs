// Wire + on-disk DTOs for every source that is not CoinGecko, plus the single
// source-generated JSON context they all serialize through.
//
// Source generation (not reflection) is mandatory here: Release builds are
// trimmed/AOT-analysed (see CryptoMonitor.csproj), and reflection-based
// System.Text.Json would be stripped.
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CmdPal.Ext.CryptoMonitor.Services;

// ---- Binance public market data (data-api.binance.vision) ----
// Numbers arrive as strings ("83672.80000000"), hence AllowReadingFromString.

public sealed class BinanceTicker24h
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("lastPrice")] public double? LastPrice { get; set; }
    [JsonPropertyName("priceChangePercent")] public double? Pct24h { get; set; }
    [JsonPropertyName("highPrice")] public double? High24h { get; set; }
    [JsonPropertyName("lowPrice")] public double? Low24h { get; set; }
    [JsonPropertyName("quoteVolume")] public double? QuoteVolume { get; set; }
}

public sealed class BinanceError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

// ---- Gate.io v4 spot ----

public sealed class GateTicker
{
    [JsonPropertyName("currency_pair")] public string Pair { get; set; } = "";
    [JsonPropertyName("last")] public double? Last { get; set; }
    [JsonPropertyName("change_percentage")] public double? Pct24h { get; set; }
    [JsonPropertyName("high_24h")] public double? High24h { get; set; }
    [JsonPropertyName("low_24h")] public double? Low24h { get; set; }
    [JsonPropertyName("quote_volume")] public double? QuoteVolume { get; set; }
}

// ---- CoinEx v2 spot ----

public sealed class CoinExEnvelope
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public List<CoinExTicker>? Data { get; set; }
}

public sealed class CoinExTicker
{
    [JsonPropertyName("market")] public string Market { get; set; } = "";
    [JsonPropertyName("last")] public double? Last { get; set; }
    [JsonPropertyName("open")] public double? Open { get; set; }
    [JsonPropertyName("high")] public double? High { get; set; }
    [JsonPropertyName("low")] public double? Low { get; set; }
    [JsonPropertyName("value")] public double? QuoteVolume { get; set; }
}

// ---- USD/CNY fx rate ----

public sealed class ErApiResponse
{
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("rates")] public Dictionary<string, double>? Rates { get; set; }
}

public sealed class FrankfurterResponse
{
    [JsonPropertyName("rates")] public Dictionary<string, double>? Rates { get; set; }
}

public sealed class FxCacheFile
{
    [JsonPropertyName("usdCny")] public double UsdCny { get; set; }
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; }
}

// ---- last known good quotes (so a reboot shows prices instead of blanks) ----

public sealed class QuoteCacheFile
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "usd";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; }
    [JsonPropertyName("quotes")] public List<CoinMarketQuote> Quotes { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(BinanceTicker24h))]
[JsonSerializable(typeof(List<BinanceTicker24h>))]
[JsonSerializable(typeof(BinanceError))]
[JsonSerializable(typeof(List<GateTicker>))]
[JsonSerializable(typeof(CoinExEnvelope))]
[JsonSerializable(typeof(ErApiResponse))]
[JsonSerializable(typeof(FrankfurterResponse))]
[JsonSerializable(typeof(FxCacheFile))]
[JsonSerializable(typeof(QuoteCacheFile))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
