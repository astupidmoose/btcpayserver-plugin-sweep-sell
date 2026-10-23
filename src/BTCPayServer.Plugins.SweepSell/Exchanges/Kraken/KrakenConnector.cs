#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;

/// <summary>Kraken over its REST API (https://docs.kraken.com/api/). Read balances, get deposit addresses, sell at market.</summary>
public class KrakenConnector(HttpClient http) : IExchangeConnector
{
    public const string HttpClientName = "SweepSell.Kraken";
    static readonly Uri BaseUri = new("https://api.kraken.com");

    // Kraken's legacy asset names (X/Z prefixed) and its own tickers for BTC and DOGE.
    static readonly Dictionary<string, string> FromKraken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XXBT"] = "BTC", ["XBT"] = "BTC", ["XXDG"] = "DOGE", ["XDG"] = "DOGE",
        ["XLTC"] = "LTC", ["XXMR"] = "XMR", ["XETH"] = "ETH", ["XXRP"] = "XRP", ["XXLM"] = "XLM",
        ["XETC"] = "ETC", ["XZEC"] = "ZEC", ["XMLN"] = "MLN", ["XREP"] = "REP",
        ["ZCAD"] = "CAD", ["ZUSD"] = "USD", ["ZEUR"] = "EUR", ["ZGBP"] = "GBP", ["ZJPY"] = "JPY", ["ZAUD"] = "AUD", ["ZCHF"] = "CHF"
    };

    static long _lastNonce;
    // Kraken rejects a nonce lower than one it has already seen for the key, so private calls with the same
    // key run one at a time, from taking the nonce until the response arrives; otherwise a later nonce can
    // overtake an earlier one on the wire and fail it with "EAPI:Invalid nonce".
    static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks = new();

    public string Id => "kraken";
    public string Name => "Kraken";

    public string KeyPermissionsHelp =>
        "In Kraken: Settings > API > Create key. Tick only Query Funds, Deposit Funds, Query Closed Orders & Trades " +
        "and Create & Modify Orders. Never tick Withdraw Funds: Sweep & Sell never withdraws.";

    /// <summary>Our ticker as Kraken's REST API takes it.</summary>
    public static string ToKraken(string asset) => asset.ToUpperInvariant() switch
    {
        "BTC" => "XBT",
        "DOGE" => "XDG",
        var other => other
    };

    /// <summary>
    /// Kraken's asset name as our ticker. Kraken Rewards balances (".F") are spendable through the base asset, so they
    /// count as it; other suffixed balances (staked ".S", ".M", ".B", ".P"...) are locked and give null.
    /// </summary>
    public static string? FromKrakenAsset(string asset)
    {
        var dot = asset.IndexOf('.');
        if (dot >= 0)
        {
            if (!asset[dot..].Equals(".F", StringComparison.OrdinalIgnoreCase))
                return null;
            asset = asset[..dot];
        }
        return FromKraken.TryGetValue(asset, out var mapped) ? mapped : asset.ToUpperInvariant();
    }

    // The deposit method of each coin's own on-chain network, by Kraken's method name. Sweeps send on-chain,
    // so a Lightning or other-network method of the same asset must never be picked.
    static readonly Dictionary<string, string> OnChainMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "Bitcoin", ["LTC"] = "Litecoin", ["DOGE"] = "Dogecoin", ["XMR"] = "Monero"
    };

    public static string Pair(string baseAsset, string quoteAsset) => ToKraken(baseAsset) + ToKraken(quoteAsset);

    public async Task<IReadOnlyDictionary<string, decimal>> GetAvailableBalancesAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var result = Expect<JObject>(await PrivateAsync(credentials, "BalanceEx", [], cancellationToken), "BalanceEx");
        var balances = new Dictionary<string, decimal>();
        foreach (var (asset, value) in result)
        {
            // Locked suffixes (staked, bonded) map to no ticker and are skipped on purpose.
            if (FromKrakenAsset(asset) is not { } ticker)
                continue;
            if (value is not JObject entry)
                throw new ExchangeException($"Kraken sent an unexpected {asset} balance entry.");
            // hold_trade is only sent when something is held; the balance itself is required.
            var available = RequiredDecimal(entry["balance"], $"{asset} balance") - OptionalDecimal(entry["hold_trade"]);
            if (available > 0)
                balances[ticker] = balances.GetValueOrDefault(ticker) + available;
        }
        return balances;
    }

    public async Task<DepositDestination> GetDepositAddressAsync(ExchangeCredentials credentials, string asset, bool fresh, CancellationToken cancellationToken)
    {
        var method = await DepositMethodAsync(credentials, asset, cancellationToken);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("asset", ToKraken(asset)),
            new("method", Text(method["method"])!)
        };
        // Kraken refuses "new" for methods that can't generate addresses; reuse the existing one there.
        if (fresh && IsTrue(method["gen-address"]))
            parameters.Add(new("new", "true"));
        var result = Expect<JArray>(await PrivateAsync(credentials, "DepositAddresses", parameters, cancellationToken), "DepositAddresses");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usable = result.OfType<JObject>()
            .Where(a => !string.IsNullOrEmpty(Text(a["address"])))
            // expiretm 0 means the address never expires; an entry without one is not trusted.
            .Where(a => long.TryParse(Text(a["expiretm"]), NumberStyles.None, CultureInfo.InvariantCulture, out var expires) && (expires == 0 || expires > now))
            .ToList();
        var entry = parameters.Any(p => p.Key == "new")
            ? usable.FirstOrDefault(a => IsTrue(a["new"]))
              ?? throw new ExchangeException($"Kraken did not return the new {asset} deposit address it was asked for.")
            : usable.FirstOrDefault()
              ?? throw new ExchangeException($"Kraken returned no unexpired {asset} deposit address. Create one in Kraken first.");
        // Kraken returns a destination tag (XRP) or memo (e.g. XLM) beside the address when the asset needs one.
        var tag = new[] { Text(entry["tag"]), Text(entry["memo"]) }.FirstOrDefault(t => !string.IsNullOrEmpty(t));
        return new DepositDestination(Text(entry["address"])!, tag);
    }

    public async Task<decimal> GetMinimumDepositAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
    {
        var method = await DepositMethodAsync(credentials, asset, cancellationToken);
        return RequiredDecimal(method["minimum"], $"{asset} minimum deposit");
    }

    public async Task<decimal> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken)
    {
        var result = Expect<JObject>(await PublicAsync("Ticker", Pair(baseAsset, quoteAsset), cancellationToken), "Ticker");
        var ticker = result.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault()
                     ?? throw new ExchangeException($"Kraken has no {baseAsset}/{quoteAsset} market.");
        var last = RequiredDecimal(ticker["c"] is JArray { Count: > 0 } c ? c[0] : null, $"{baseAsset}/{quoteAsset} last price");
        return last > 0 ? last : throw new ExchangeException($"Kraken returned no price for {baseAsset}/{quoteAsset}.");
    }

    public async Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken)
    {
        var result = Expect<JObject>(await PublicAsync("AssetPairs", Pair(baseAsset, quoteAsset), cancellationToken), "AssetPairs");
        var pair = result.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault()
                   ?? throw new ExchangeException($"Kraken has no {baseAsset}/{quoteAsset} market.");
        var market = $"{baseAsset}/{quoteAsset}";
        var decimals = pair["lot_decimals"] is JValue { Type: JTokenType.Integer } lot
            ? (long)lot
            : throw new ExchangeException($"Kraken sent no amount precision for {market}.");
        if (decimals is < 0 or > MarketRules.MaxAmountDecimals)
            throw new ExchangeException($"Kraken sent an unsupported amount precision ({decimals}) for {market}.");
        return new MarketRules(baseAsset.ToUpperInvariant(), quoteAsset.ToUpperInvariant(),
            RequiredDecimal(pair["ordermin"], $"{market} minimum order"),
            RequiredDecimal(pair["costmin"], $"{market} minimum order cost"),
            (int)decimals);
    }

    public async Task<OrderResult> SellAtMarketAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount,
        Guid clientOrderId, bool validateOnly, CancellationToken cancellationToken)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount to sell must be positive.");
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("ordertype", "market"),
            new("type", "sell"),
            new("pair", Pair(baseAsset, quoteAsset)),
            new("volume", amount.ToString(CultureInfo.InvariantCulture)),
            // Kraken's client order ID (UUID form): the order can be looked up by it if the response is lost.
            new("cl_ord_id", clientOrderId.ToString("D"))
        };
        if (validateOnly)
            parameters.Add(new("validate", "true"));
        var result = Expect<JObject>(await PrivateAsync(credentials, "AddOrder", parameters, cancellationToken), "AddOrder");
        var ids = result["txid"] is JArray txids ? txids.Select(t => Text(t) ?? "").Where(t => t.Length > 0).ToList() : [];
        if (!validateOnly && ids.Count == 0)
            throw new ExchangeException($"Kraken accepted the {baseAsset}/{quoteAsset} sell (client order {clientOrderId:D}) but returned no order ID. Check the order in Kraken before retrying.");
        var description = (result["descr"] is JObject descr ? Text(descr["order"]) : null) ?? $"sell {amount} {baseAsset} for {quoteAsset} @ market";
        return new OrderResult(!validateOnly, ids, description);
    }

    public async Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>
        {
            await CheckAsync("Query Funds", () => PrivateAsync(credentials, "BalanceEx", [], cancellationToken), orderCheck: false, cancellationToken),
            await CheckAsync("Deposit Funds", () => PrivateAsync(credentials, "DepositMethods", [new("asset", "XBT")], cancellationToken), orderCheck: false, cancellationToken),
            // Used to look an order up by its client order ID after a lost response.
            await CheckAsync("Query Closed Orders & Trades", () => PrivateAsync(credentials, "ClosedOrders", [], cancellationToken), orderCheck: false, cancellationToken)
        };
        // A validate-only order checks the trading permission without trading. Twice the minimum leaves room for the
        // price to move between the ticker and the order.
        checks.Add(await CheckAsync("Create & Modify Orders", async () =>
        {
            var rules = await GetMarketRulesAsync("BTC", "USD", cancellationToken);
            var volume = rules.MinimumSellAmount(await GetPriceAsync("BTC", "USD", cancellationToken)) * 2;
            return await PrivateAsync(credentials, "AddOrder",
            [
                new("ordertype", "market"), new("type", "sell"), new("pair", Pair("BTC", "USD")),
                new("volume", volume.ToString(CultureInfo.InvariantCulture)),
                new("validate", "true")
            ], cancellationToken);
        }, orderCheck: true, cancellationToken));
        return new CredentialsCheck(checks);
    }

    static async Task<PermissionCheck> CheckAsync(string permission, Func<Task<JToken>> call, bool orderCheck, CancellationToken cancellationToken)
    {
        try
        {
            await call();
            return new PermissionCheck(permission, true, null);
        }
        catch (ExchangeException e) when (orderCheck && e.Message.Contains("EOrder:", StringComparison.Ordinal))
        {
            // Kraken only judges the order itself (funds, size, price) once the key is allowed to trade.
            return new PermissionCheck(permission, true, null);
        }
        catch (ExchangeException e)
        {
            return new PermissionCheck(permission, false, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Network failure or timeout: the permission is unknown, not missing.
            return new PermissionCheck(permission, false, $"Could not reach Kraken ({e.GetType().Name}). Try again.");
        }
    }

    async Task<JObject> DepositMethodAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
    {
        var methods = Expect<JArray>(await PrivateAsync(credentials, "DepositMethods", [new("asset", ToKraken(asset))], cancellationToken), "DepositMethods")
            .OfType<JObject>().ToList();
        if (OnChainMethods.TryGetValue(asset, out var name))
            return methods.FirstOrDefault(m => string.Equals(Text(m["method"]), name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ExchangeException($"Kraken offers no {name} (on-chain) deposit method for {asset}.");
        // A coin we don't know the network of is only safe when Kraken offers exactly one way to deposit it.
        return methods.Count == 1
            ? methods[0]
            : throw new ExchangeException(methods.Count == 0
                ? $"Kraken offers no way to deposit {asset}."
                : $"Kraken offers several deposit networks for {asset}; Sweep & Sell doesn't know which one to use yet.");
    }

    async Task<JToken> PublicAsync(string method, string pair, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"/0/public/{method}?pair={Uri.EscapeDataString(pair)}"));
        return await SendAsync(request, cancellationToken);
    }

    async Task<JToken> PrivateAsync(ExchangeCredentials credentials, string method, IEnumerable<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        var keyLock = KeyLocks.GetOrAdd(KeyFingerprint(credentials.ApiKey), _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            return await PrivateLockedAsync(credentials, method, parameters, cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    async Task<JToken> PrivateLockedAsync(ExchangeCredentials credentials, string method, IEnumerable<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        var path = $"/0/private/{method}";
        var nonce = NextNonce().ToString(CultureInfo.InvariantCulture);
        var fields = new List<KeyValuePair<string, string>> { new("nonce", nonce) };
        fields.AddRange(parameters);
        using var content = new FormUrlEncodedContent(fields);
        var body = await content.ReadAsStringAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, path))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("API-Key", credentials.ApiKey);
        request.Headers.Add("API-Sign", KrakenSigner.Sign(credentials.ApiSecret, path, nonce, body));
        return await SendAsync(request, cancellationToken);
    }

    async Task<JToken> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JObject json;
        try
        {
            // Amounts may come as JSON numbers; read them as decimals so they never pass through double.
            using var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(text))
            {
                FloatParseHandling = Newtonsoft.Json.FloatParseHandling.Decimal,
                DateParseHandling = Newtonsoft.Json.DateParseHandling.None
            };
            json = JObject.Load(reader);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new ExchangeException($"Kraken answered HTTP {(int)response.StatusCode} without JSON.");
        }
        if (json["error"] is JArray { Count: > 0 } errors)
            throw new ExchangeException("Kraken: " + string.Join("; ", errors.Select(e => e.ToString())));
        return json["result"] ?? throw new ExchangeException($"Kraken answered HTTP {(int)response.StatusCode} without a result.");
    }

    /// <summary>Kraken wants every nonce of a key larger than the last; microseconds, never repeated in this process.</summary>
    static long NextNonce()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        while (true)
        {
            var last = Interlocked.Read(ref _lastNonce);
            var next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _lastNonce, next, last) == last)
                return next;
        }
    }

    /// <summary>Identifies a key for locking without keeping the key itself in memory.</summary>
    static string KeyFingerprint(string apiKey) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(apiKey)));

    /// <summary>The result of a call in the shape the code expects; anything else fails as an exchange error, not a crash.</summary>
    static T Expect<T>(JToken result, string method) where T : JToken =>
        result as T ?? throw new ExchangeException($"Kraken sent an unexpected {method} response.");

    /// <summary>A text or number field as text; null when missing or of another shape.</summary>
    static string? Text(JToken? token) =>
        token is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float } value
            ? Convert.ToString(value.Value, CultureInfo.InvariantCulture)
            : null;

    static bool IsTrue(JToken? token) => token is JValue { Type: JTokenType.Boolean, Value: true };

    /// <summary>A number Kraken must send; missing or malformed fails loudly instead of reading as zero.</summary>
    static decimal RequiredDecimal(JToken? token, string what)
    {
        if (token is null || token.Type == JTokenType.Null ||
            !decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new ExchangeException($"Kraken sent no valid {what}.");
        return value;
    }

    /// <summary>A number Kraken may leave out when it is zero.</summary>
    static decimal OptionalDecimal(JToken? token) =>
        token is null || token.Type == JTokenType.Null ? 0 : RequiredDecimal(token, "amount");
}
