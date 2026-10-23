#nullable enable
using System;
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

    public string Id => "kraken";
    public string Name => "Kraken";

    public string KeyPermissionsHelp =>
        "In Kraken: Settings > API > Create key. Tick only Query Funds, Deposit Funds, Query Open Orders & Trades, " +
        "Query Closed Orders & Trades and Create & Modify Orders. Never tick Withdraw Funds: Sweep & Sell never withdraws.";

    /// <summary>Our ticker as Kraken's REST API takes it.</summary>
    public static string ToKraken(string asset) => asset.ToUpperInvariant() switch
    {
        "BTC" => "XBT",
        "DOGE" => "XDG",
        var other => other
    };

    /// <summary>Kraken's asset name as our ticker. Null for balances in Earn, staking or futures (suffixed ".F", ".S", ".M", ".B"...), which can't be sold.</summary>
    public static string? FromKrakenAsset(string asset)
    {
        if (asset.Contains('.'))
            return null;
        return FromKraken.TryGetValue(asset, out var mapped) ? mapped : asset.ToUpperInvariant();
    }

    public static string Pair(string baseAsset, string quoteAsset) => ToKraken(baseAsset) + ToKraken(quoteAsset);

    public async Task<IReadOnlyDictionary<string, decimal>> GetAvailableBalancesAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var result = (JObject)await PrivateAsync(credentials, "BalanceEx", [], cancellationToken);
        var balances = new Dictionary<string, decimal>();
        foreach (var (asset, value) in result)
        {
            if (FromKrakenAsset(asset) is not { } ticker || value is not JObject entry)
                continue;
            var available = Decimal(entry["balance"]) - Decimal(entry["hold_trade"]);
            if (available > 0)
                balances[ticker] = balances.GetValueOrDefault(ticker) + available;
        }
        return balances;
    }

    public async Task<string> GetDepositAddressAsync(ExchangeCredentials credentials, string asset, bool fresh, CancellationToken cancellationToken)
    {
        var method = await DepositMethodAsync(credentials, asset, cancellationToken);
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("asset", ToKraken(asset)),
            new("method", method.Value<string>("method")!)
        };
        // Kraken refuses "new" for methods that can't generate addresses; reuse the existing one there.
        if (fresh && method.Value<bool?>("gen-address") == true)
            parameters.Add(new("new", "true"));
        var result = (JArray)await PrivateAsync(credentials, "DepositAddresses", parameters, cancellationToken);
        var address = result.OfType<JObject>().Select(a => a.Value<string>("address")).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        return address ?? throw new ExchangeException($"Kraken returned no {asset} deposit address. Create one in Kraken first.");
    }

    public async Task<decimal> GetMinimumDepositAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
    {
        var method = await DepositMethodAsync(credentials, asset, cancellationToken);
        return Decimal(method["minimum"]);
    }

    public async Task<decimal> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken)
    {
        var result = (JObject)await PublicAsync("Ticker", Pair(baseAsset, quoteAsset), cancellationToken);
        var ticker = result.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault()
                     ?? throw new ExchangeException($"Kraken has no {baseAsset}/{quoteAsset} market.");
        var last = Decimal(ticker["c"]?[0]);
        return last > 0 ? last : throw new ExchangeException($"Kraken returned no price for {baseAsset}/{quoteAsset}.");
    }

    public async Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken)
    {
        var result = (JObject)await PublicAsync("AssetPairs", Pair(baseAsset, quoteAsset), cancellationToken);
        var pair = result.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault()
                   ?? throw new ExchangeException($"Kraken has no {baseAsset}/{quoteAsset} market.");
        return new MarketRules(baseAsset.ToUpperInvariant(), quoteAsset.ToUpperInvariant(),
            Decimal(pair["ordermin"]), pair.Value<int?>("lot_decimals") ?? 8);
    }

    public async Task<OrderResult> SellAtMarketAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount,
        bool validateOnly, CancellationToken cancellationToken)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount to sell must be positive.");
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("ordertype", "market"),
            new("type", "sell"),
            new("pair", Pair(baseAsset, quoteAsset)),
            new("volume", amount.ToString(CultureInfo.InvariantCulture))
        };
        if (validateOnly)
            parameters.Add(new("validate", "true"));
        var result = (JObject)await PrivateAsync(credentials, "AddOrder", parameters, cancellationToken);
        var ids = result["txid"] is JArray txids ? txids.Select(t => t.ToString()).ToList() : [];
        var description = result["descr"]?.Value<string>("order") ?? $"sell {amount} {baseAsset} for {quoteAsset} @ market";
        return new OrderResult(!validateOnly, ids, description);
    }

    public async Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>
        {
            await CheckAsync("Query Funds", () => PrivateAsync(credentials, "BalanceEx", [], cancellationToken)),
            await CheckAsync("Deposit Funds", () => PrivateAsync(credentials, "DepositMethods", [new("asset", "XBT")], cancellationToken))
        };
        // A validate-only order checks the trading permission without trading.
        checks.Add(await CheckAsync("Create & Modify Orders", async () =>
        {
            var rules = await GetMarketRulesAsync("BTC", "USD", cancellationToken);
            return await PrivateAsync(credentials, "AddOrder",
            [
                new("ordertype", "market"), new("type", "sell"), new("pair", Pair("BTC", "USD")),
                new("volume", rules.MinimumAmount.ToString(CultureInfo.InvariantCulture)), new("validate", "true")
            ], cancellationToken);
        }));
        return new CredentialsCheck(checks);
    }

    static async Task<PermissionCheck> CheckAsync(string permission, Func<Task<JToken>> call)
    {
        try
        {
            await call();
            return new PermissionCheck(permission, true, null);
        }
        catch (ExchangeException e)
        {
            // Not enough funds still proves the key may trade.
            return e.Message.Contains("EOrder:Insufficient funds", StringComparison.Ordinal)
                ? new PermissionCheck(permission, true, null)
                : new PermissionCheck(permission, false, e.Message);
        }
    }

    async Task<JObject> DepositMethodAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
    {
        var methods = (JArray)await PrivateAsync(credentials, "DepositMethods", [new("asset", ToKraken(asset))], cancellationToken);
        // Prefer the coin's own network, which Kraken lists first.
        return methods.OfType<JObject>().FirstOrDefault()
               ?? throw new ExchangeException($"Kraken offers no way to deposit {asset}.");
    }

    async Task<JToken> PublicAsync(string method, string pair, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"/0/public/{method}?pair={Uri.EscapeDataString(pair)}"));
        return await SendAsync(request, cancellationToken);
    }

    async Task<JToken> PrivateAsync(ExchangeCredentials credentials, string method, IEnumerable<KeyValuePair<string, string>> parameters,
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
            json = JObject.Parse(text);
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

    static decimal Decimal(JToken? token) =>
        token is null || token.Type == JTokenType.Null ? 0 :
        decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
