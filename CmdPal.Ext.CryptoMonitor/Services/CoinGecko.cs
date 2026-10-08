// CoinGecko REST client + DTOs for the Crypto Monitor CmdPal extension.
//
// This client is used for metadata (market cap, rank, 7d change, logos), for
// search relevance and as the fallback quote path. It always goes through the
// system proxy (api.coingecko.com is unreachable directly from this network)
// with a 2-minute pooled-connection lifetime — see Network.cs.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CmdPal.Ext.CryptoMonitor.Services;

// ---- DTOs (JSON names follow CoinGecko's camelCase) ----

public sealed class CoinListEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class CoinMarketQuote
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("current_price")] public double? CurrentPrice { get; set; }
    [JsonPropertyName("market_cap")] public double? MarketCap { get; set; }
    [JsonPropertyName("market_cap_rank")] public int? MarketCapRank { get; set; }
    [JsonPropertyName("total_volume")] public double? TotalVolume { get; set; }
    [JsonPropertyName("high_24h")] public double? High24h { get; set; }
    [JsonPropertyName("low_24h")] public double? Low24h { get; set; }
    [JsonPropertyName("price_change_percentage_24h")] public double? Pct24h { get; set; }
    [JsonPropertyName("price_change_percentage_24h_in_currency")] public double? Pct24hInCurrency { get; set; }
    [JsonPropertyName("price_change_percentage_7d_in_currency")] public double? Pct7dInCurrency { get; set; }
}

public sealed class SearchCoinHit
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("market_cap_rank")] public int? MarketCapRank { get; set; }
    [JsonPropertyName("thumb")] public string? Thumb { get; set; }
    [JsonPropertyName("large")] public string? Large { get; set; }
}

public sealed class SearchResponse
{
    [JsonPropertyName("coins")] public List<SearchCoinHit> Coins { get; set; } = [];
}

public sealed record CoinSearchResult(string Id, string Name, string Symbol, string? Image, CoinMarketQuote? Quote, int? Rank);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CoinListEntry))]
[JsonSerializable(typeof(List<CoinListEntry>))]
[JsonSerializable(typeof(CoinMarketQuote))]
[JsonSerializable(typeof(List<CoinMarketQuote>))]
[JsonSerializable(typeof(SearchResponse))]
internal sealed partial class CoinJsonContext : JsonSerializerContext
{
}

public sealed class CoinGeckoClient : IDisposable
{
    public const string SourceLabel = "CoinGecko";

    private const string BaseUrl = "https://api.coingecko.com/api/v3/";
    private const string UserAgent = "CmdPal.Ext.CryptoMonitor/0.3 (PowerToys Command Palette extension)";
    private readonly HttpClient _http;
    private readonly string? _apiKey;

    public CoinGeckoClient(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http = Network.CreateClient(UserAgent, TimeSpan.FromSeconds(20), ProxyMode.SystemProxy);
        _http.BaseAddress = new Uri(BaseUrl);
    }

    public void Dispose() => _http.Dispose();

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (_apiKey is not null)
        {
            req.Headers.Add("x-cg-demo-api-key", _apiKey);
        }
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"CoinGecko HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }
        return body;
    }

    public async Task<List<CoinMarketQuote>> GetMarketsAsync(IReadOnlyCollection<string> ids, string currency, CancellationToken ct)
    {
        var body = await GetStringAsync(
            $"coins/markets?vs_currency={Uri.EscapeDataString(currency)}&ids={Uri.EscapeDataString(string.Join(',', ids))}&order=market_cap_desc&price_change_percentage=1h%2C24h%2C7d&precision=full&per_page=250",
            ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, CoinJsonContext.Default.ListCoinMarketQuote) ?? [];
    }

    public async Task<SearchResponse> SearchAsync(string query, CancellationToken ct)
    {
        var body = await GetStringAsync($"search?query={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, CoinJsonContext.Default.SearchResponse) ?? new SearchResponse();
    }

    public async Task<List<CoinListEntry>> GetFullListAsync(CancellationToken ct)
    {
        var body = await GetStringAsync("coins/list?include_platform=false", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, CoinJsonContext.Default.ListCoinListEntry) ?? [];
    }
}
