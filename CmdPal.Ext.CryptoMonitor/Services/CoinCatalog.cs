// Coin identity: CoinGecko id <-> ticker symbol <-> display name.
//
// The whole cross-source design rests on this table. CoinGecko addresses coins
// by id ("bitcoin"), every exchange addresses them by ticker ("BTC"), and the
// watchlist mixes both (a canonical favorite is stored as "btc", anything else
// as its CoinGecko id, e.g. "zcash"). So we need a mapping that works *offline*:
//
//  * a built-in seed of well-known coins (always available, even on a machine
//    where CoinGecko has never been reachable), plus
//  * a persisted snapshot of CoinGecko's /coins/list (id + symbol + name), which
//    is authoritative and covers ~17k coins. It is fetched at most once a day
//    and stored in %LOCALAPPDATA%\CryptoMonitor\coins.tsv.
//
// It also backs offline search, so the search box keeps working while the
// CoinGecko endpoint is unreachable.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CmdPal.Ext.CryptoMonitor.Services;

public sealed record CoinInfo(string Id, string Symbol, string Name);

public sealed class CoinCatalog
{
    /// id|SYMBOL|Name — one per line. Only entries whose ticker symbol is
    /// well-known are listed; the CoinGecko snapshot fills in everything else.
    private const string Seed = """
        bitcoin|BTC|Bitcoin
        ethereum|ETH|Ethereum
        tether|USDT|Tether
        ripple|XRP|XRP
        bnb|BNB|BNB
        solana|SOL|Solana
        usd-coin|USDC|USDC
        dogecoin|DOGE|Dogecoin
        cardano|ADA|Cardano
        tron|TRX|TRON
        avalanche-2|AVAX|Avalanche
        chainlink|LINK|Chainlink
        shiba-inu|SHIB|Shiba Inu
        polkadot|DOT|Polkadot
        litecoin|LTC|Litecoin
        bitcoin-cash|BCH|Bitcoin Cash
        near|NEAR|NEAR Protocol
        uniswap|UNI|Uniswap
        pepe|PEPE|Pepe
        aptos|APT|Aptos
        sui|SUI|Sui
        arbitrum|ARB|Arbitrum
        optimism|OP|Optimism
        injective|INJ|Injective
        stacks|STX|Stacks
        filecoin|FIL|Filecoin
        cosmos|ATOM|Cosmos Hub
        toncoin|TON|Toncoin
        dai|DAI|Dai
        wrapped-bitcoin|WBTC|Wrapped Bitcoin
        pyth-network|PYTH|Pyth Network
        sei-network|SEI|Sei
        celo|CELO|Celo
        aave|AAVE|Aave
        pancakeswap-token|CAKE|PancakeSwap
        gmx|GMX|GMX
        jupiter-exchange-solana|JUP|Jupiter
        celestia|TIA|Celestia
        ordi|ORDI|ORDI
        dogwifcoin|WIF|dogwifcoin
        zcash|ZEC|Zcash
        hyperliquid|HYPE|Hyperliquid
        tether-gold|XAUT|Tether Gold
        ethereum-classic|ETC|Ethereum Classic
        monero|XMR|Monero
        hedera-hashgraph|HBAR|Hedera
        vechain|VET|VeChain
        algorand|ALGO|Algorand
        fantom|FTM|Fantom
        the-sandbox|SAND|The Sandbox
        decentraland|MANA|Decentraland
        axie-infinity|AXS|Axie Infinity
        eos|EOS|EOS
        tezos|XTZ|Tezos
        flow|FLOW|Flow
        kusama|KSM|Kusama
        chiliz|CHZ|Chiliz
        curve-dao-token|CRV|Curve DAO Token
        maker|MKR|Maker
        the-graph|GRT|The Graph
        thorchain|RUNE|THORChain
        immutable-x|IMX|Immutable
        render-token|RENDER|Render
        floki|FLOKI|FLOKI
        bonk|BONK|Bonk
        jito-governance-token|JTO|Jito
        ethena|ENA|Ethena
        ondo-finance|ONDO|Ondo
        wormhole|W|Wormhole
        stellar|XLM|Stellar
        neo|NEO|Neo
        iota|IOTA|IOTA
        dash|DASH|Dash
        zilliqa|ZIL|Zilliqa
        enjincoin|ENJ|Enjin Coin
        basic-attention-token|BAT|Basic Attention Token
        0x|ZRX|0x Protocol
        compound-governance-token|COMP|Compound
        havven|SNX|Synthetix
        lido-dao|LDO|Lido DAO
        worldcoin-wld|WLD|Worldcoin
        starknet|STRK|Starknet
        zksync|ZK|ZKsync
        bittensor|TAO|Bittensor
        arweave|AR|Arweave
        internet-computer|ICP|Internet Computer
        multiversx-egld|EGLD|MultiversX
        theta-token|THETA|Theta Network
        gala|GALA|Gala
        apecoin|APE|ApeCoin
        kaspa|KAS|Kaspa
        mantle|MNT|Mantle
        dydx-chain|DYDX|dYdX
        osmosis|OSMO|Osmosis
        akash-network|AKT|Akash Network
        kava|KAVA|Kava
        conflux-token|CFX|Conflux
        trust-wallet-token|TWT|Trust Wallet Token
        crypto-com-chain|CRO|Cronos
        okb|OKB|OKB
        leo-token|LEO|LEO Token
        pudgy-penguins|PENGU|Pudgy Penguins
        official-trump|TRUMP|OFFICIAL TRUMP
        virtuals-protocol|VIRTUAL|Virtuals Protocol
        artificial-superintelligence-alliance|FET|Artificial Superintelligence Alliance
        jasmycoin|JASMY|JasmyCoin
        oasis-network|ROSE|Oasis
        mina-protocol|MINA|Mina
        1inch|1INCH|1inch
        woo-network|WOO|WOO
        loopring|LRC|Loopring
        balancer|BAL|Balancer
        yearn-finance|YFI|yearn.finance
        sushi|SUSHI|SushiSwap
        waves|WAVES|Waves
        ravencoin|RVN|Ravencoin
        harmony|ONE|Harmony
        bitcoin-sv|BSV|Bitcoin SV
        theta-fuel|TFUEL|Theta Fuel
        """;

    private const string HeaderPrefix = "#v1\t";

    private readonly object _gate = new();
    private readonly Dictionary<string, CoinInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CoinInfo> _bySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _symbolScore = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CoinInfo> _all = [];
    private readonly Dictionary<string, int> _indexById = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Paths.DataFile("coins.tsv");
    private DateTime _snapshotUtc = DateTime.MinValue;

    public CoinCatalog()
    {
        SeedLoad();
        SnapshotLoad();
    }

    /// When the CoinGecko snapshot was last written (UTC).
    public DateTime SnapshotUtc
    {
        get
        {
            lock (_gate)
            {
                return _snapshotUtc;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    public CoinInfo? ById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        lock (_gate)
        {
            return _byId.TryGetValue(id.Trim(), out var info) ? info : null;
        }
    }

    public CoinInfo? BySymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }
        lock (_gate)
        {
            return _bySymbol.TryGetValue(symbol.Trim(), out var info) ? info : null;
        }
    }

    /// Local (offline) search over symbol, name and id.
    public IReadOnlyList<CoinInfo> Search(string query, int limit)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            var scored = new List<(int Rank, CoinInfo Info)>();
            foreach (var info in _all)
            {
                var rank = RankOf(info, q);
                if (rank < 0)
                {
                    continue;
                }
                // Wrapped/PEG variants share the ticker of the coin they track
                // ("binance-peg-dogecoin" also answers to DOGE). Show the coin
                // that actually owns the ticker first so it is not the one that
                // gets dropped by the per-symbol dedupe below.
                if (!OwnsSymbol(info))
                {
                    rank += 10;
                }
                scored.Add((rank, info));
            }

            return scored
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Info.Symbol.Length)
                .ThenBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Info)
                .DistinctBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }
    }

    public bool SnapshotIsStale(TimeSpan maxAge)
    {
        lock (_gate)
        {
            return DateTime.UtcNow - _snapshotUtc > maxAge;
        }
    }

    /// Absorb CoinGecko's /coins/list and persist it. Ids are authoritative, so
    /// they overwrite seed entries; symbol lookups keep the seed/canonical entry
    /// (duplicate tickers exist and the well-known one must win).
    public void IngestCoinList(IReadOnlyCollection<CoinListEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var parsed = new List<(string Id, string Symbol, string Name)>(entries.Count);
        foreach (var e in entries)
        {
            if (!string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Symbol))
            {
                parsed.Add((e.Id.Trim(), e.Symbol.Trim().ToUpperInvariant(), e.Name ?? ""));
            }
        }

        lock (_gate)
        {
            IngestCore(parsed);
        }

        SnapshotSave(parsed);
    }

    // ---- internals ----

    /// True when this entry is the one a bare ticker resolves to (see _bySymbol).
    private bool OwnsSymbol(CoinInfo info) =>
        _bySymbol.TryGetValue(info.Symbol, out var owner)
        && string.Equals(owner.Id, info.Id, StringComparison.OrdinalIgnoreCase);

    /// Merge ids/symbols into the lookup tables. Caller holds the lock.
    private void IngestCore(List<(string Id, string Symbol, string Name)> entries)
    {
        foreach (var (rawId, symbol, rawName) in entries)
        {
            var id = rawId;
            var name = string.IsNullOrWhiteSpace(rawName) ? symbol : rawName.Trim();

            // Reuse the instance when nothing changed: /coins/list comes back
            // every 12 h with the same ~22k entries, and allocating a new object
            // per entry only churned the heap (worse, the previous generation
            // stayed reachable through _bySymbol — see below).
            var info = _byId.TryGetValue(id, out var existing)
                && string.Equals(existing.Symbol, symbol, StringComparison.Ordinal)
                && string.Equals(existing.Name, name, StringComparison.Ordinal)
                ? existing
                : new CoinInfo(id, symbol, name);

            // Ticker -> coin. Several coins share a ticker (wrapped/pegged
            // variants), and /coins/list order is arbitrary, so pick by score:
            // an id that *is* the ticker wins (kaia -> kaia), a seed entry wins
            // over everything (never replaced).
            var score = SymbolScore(id, symbol);
            if (!_bySymbol.TryGetValue(symbol, out var owner))
            {
                _bySymbol[symbol] = info;
                _symbolScore[symbol] = score;
            }
            else if (string.Equals(owner.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                // The same coin: a refresh must not leave the previous instance
                // pinned here (that handed out stale ids and kept a whole
                // generation of CoinInfos alive). The score is left alone.
                _bySymbol[symbol] = info;
            }
            else if (_symbolScore.TryGetValue(symbol, out var ownerScore) && ownerScore > score)
            {
                // A better owner for this ticker showed up (score 0 entries are
                // the seed and can never be displaced).
                _bySymbol[symbol] = info;
                _symbolScore[symbol] = score;
            }

            if (_byId.ContainsKey(id))
            {
                _byId[id] = info;
                if (_indexById.TryGetValue(id, out var index) && index < _all.Count)
                {
                    _all[index] = info;
                }
                else
                {
                    _indexById[id] = _all.Count;
                    _all.Add(info);
                }
            }
            else
            {
                _byId[id] = info;
                _indexById[id] = _all.Count;
                _all.Add(info);
            }
        }
        _snapshotUtc = DateTime.UtcNow;
    }

    /// Lower is better: 0 = seed (locked), 1 = the id *is* the ticker
    /// (kaia -> kaia), 2 = id and ticker are related, 3 = unrelated.
    private static int SymbolScore(string id, string symbol)
    {
        var normalized = id.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (normalized.Equals(symbol, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (normalized.StartsWith(symbol, StringComparison.OrdinalIgnoreCase)
            || symbol.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return 3;
    }

    private static int RankOf(CoinInfo info, string q)
    {
        if (string.Equals(info.Symbol, q, StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.Id, q, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (info.Symbol.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (info.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (info.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        return -1;
    }

    private void Add(CoinInfo info)
    {
        _byId[info.Id] = info;
        if (!_bySymbol.ContainsKey(info.Symbol))
        {
            _bySymbol[info.Symbol] = info;
            _symbolScore[info.Symbol] = 0; // seed: locked against snapshot entries
        }
        _indexById[info.Id] = _all.Count;
        _all.Add(info);
    }

    private void SeedLoad()
    {
        foreach (var line in Seed.Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 3)
            {
                continue;
            }
            Add(new CoinInfo(parts[0].Trim(), parts[1].Trim().ToUpperInvariant(), parts[2].Trim()));
        }
        _snapshotUtc = DateTime.MinValue;
    }

    private void SnapshotLoad()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            var parsed = new List<(string Id, string Symbol, string Name)>();
            var when = DateTime.MinValue;
            foreach (var raw in File.ReadLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                {
                    DateTime.TryParse(
                        line[HeaderPrefix.Length..],
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out when);
                    continue;
                }
                var parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                {
                    continue;
                }
                parsed.Add((
                    parts[0].Trim(),
                    parts[1].Trim().ToUpperInvariant(),
                    parts.Length >= 3 ? parts[2].Trim() : ""));
            }

            lock (_gate)
            {
                var before = _all.Count;
                IngestCore(parsed);
                _snapshotUtc = when;
                Log.Info($"coin catalog: {_all.Count} entries ({_all.Count - before} from snapshot, snapshot={_snapshotUtc:u})");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"coin catalog load failed: {ex.Message}");
        }
    }

    private void SnapshotSave(List<(string Id, string Symbol, string Name)> entries)
    {
        try
        {
            var lines = new List<string>(entries.Count + 1)
            {
                HeaderPrefix + DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            };
            foreach (var (id, symbol, name) in entries)
            {
                lines.Add($"{id}\t{symbol}\t{name}");
            }
            var tmp = _path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, _path, overwrite: true);
            Log.Info($"coin catalog saved: {lines.Count - 1} entries -> {_path}");
        }
        catch (Exception ex)
        {
            Log.Error($"coin catalog save failed: {ex.Message}");
        }
    }
}
