#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static BTCPayServer.Plugins.SweepSell.Exchanges.Kraken.KrakenConnector;

namespace BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;

/// <summary>
/// Fiat payout from Kraken, with a withdraw-only key. Kraken's API has no way to add a withdrawal destination: a
/// withdrawal names a destination ("key") the owner saved in Kraken, which Kraken confirms by email before it can be used.
/// </summary>
public class KrakenWithdrawals(KrakenConnector kraken) : IWithdrawalConnector
{
    public string Id => kraken.Id;
    public string Name => kraken.Name;

    public string KeyPermissionsHelp =>
        "In Kraken: Settings > API > Create key. Use a separate key from the trading key, and tick only Query Funds and Withdraw Funds. " +
        "Never tick any order permission, nor Add or Update Withdrawal Addresses (payout refuses a key that has them). Payouts only go to a withdrawal destination you already saved and verified in Kraken; " +
        "enter its name exactly as Kraken shows it.";

    public async Task<decimal> GetAvailableBalanceAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken) =>
        (await kraken.GetAvailableBalancesAsync(credentials, cancellationToken)).GetValueOrDefault(asset.ToUpperInvariant());

    public async Task<IReadOnlyList<WithdrawalDestination>> GetDestinationsAsync(ExchangeCredentials credentials, string asset,
        CancellationToken cancellationToken) =>
        Destinations(await AddressesAsync(credentials, asset, null, cancellationToken), asset);

    public async Task<WithdrawalQuote> QuoteAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount,
        CancellationToken cancellationToken)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount to withdraw must be positive.");
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("A saved destination is needed.", nameof(destination));
        // Kraken may not list every kind of destination (e.g. bank accounts) here; one it lists must be verified.
        var listed = Destinations(await AddressesAsync(credentials, asset, destination, cancellationToken), asset)
            .Where(d => d.Name == destination).ToList();
        if (listed.Count > 0 && !listed.Any(d => d.Verified))
            throw new ExchangeException($"Kraken lists the {asset} withdrawal destination \"{destination}\" as not verified. Verify it in Kraken first.", rejected: true);

        var decimals = await AmountDecimalsAsync(asset, cancellationToken);
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
            factor *= 10m;
        var rounded = Math.Floor(amount * factor) / factor;
        if (rounded <= 0)
            throw new ExchangeException($"{amount} {asset} is less than Kraken's smallest {asset} amount.", rejected: true);

        var info = Expect<JObject>(await kraken.PrivateAsync(credentials, "WithdrawInfo",
            [new("asset", ToKraken(asset)), new("key", destination), new("amount", rounded.ToString(CultureInfo.InvariantCulture))],
            cancellationToken), "WithdrawInfo");
        var method = Text(info["method"]) is { Length: > 0 } m ? m : throw new ExchangeException($"Kraken sent no withdrawal method for \"{destination}\".");
        var net = RequiredDecimal(info["amount"], $"net {asset} withdrawal amount");
        var fee = RequiredDecimal(info["fee"], $"{asset} withdrawal fee");
        var limit = RequiredDecimal(info["limit"], $"{asset} withdrawal limit");
        // What arrives can't be more than what leaves; a response saying otherwise is not trusted.
        if (net > rounded || fee > rounded)
            throw new ExchangeException($"Kraken quoted an impossible {asset} withdrawal ({net} sent and {fee} fee for {rounded}).");

        var methods = Expect<JArray>(await kraken.PrivateAsync(credentials, "WithdrawMethods", [new("asset", ToKraken(asset))], cancellationToken),
            "WithdrawMethods").OfType<JObject>();
        var entry = methods.FirstOrDefault(e => string.Equals(Text(e["method"]), method, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ExchangeException($"Kraken lists no {asset} withdrawal method \"{method}\", so its minimum is unknown.");
        var minimum = RequiredDecimal(entry["minimum"], $"{asset} minimum withdrawal");
        return new WithdrawalQuote(destination, method, rounded, net, fee, minimum, limit);
    }

    public async Task<string> WithdrawAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount, decimal maxFee,
        CancellationToken cancellationToken)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount to withdraw must be positive.");
        if (maxFee < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFee), "The highest fee can't be negative.");
        var result = Expect<JObject>(await kraken.PrivateAsync(credentials, "Withdraw",
        [
            new("asset", ToKraken(asset)),
            new("key", destination),
            new("amount", amount.ToString(CultureInfo.InvariantCulture)),
            // Kraken fails the withdrawal instead of charging more than this.
            new("max_fee", maxFee.ToString(CultureInfo.InvariantCulture))
        ], cancellationToken), "Withdraw");
        return result["refid"] is JValue { Type: JTokenType.String } refid && !string.IsNullOrEmpty((string?)refid)
            ? (string)refid!
            : throw new ExchangeException($"Kraken accepted the {amount} {asset} withdrawal but returned no reference. Check your Kraken withdrawals before doing anything else.");
    }

    /// <summary>Most pages of withdrawals read (500 each); past that the history is refused rather than read in part.</summary>
    public const int MaxWithdrawalPages = 20;

    public async Task<IReadOnlyList<WithdrawalRecord>> GetRecentWithdrawalsAsync(ExchangeCredentials credentials, string asset, DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        // Kraken lists withdrawals newest first, a page at a time. Every page is read: an older withdrawal still in progress
        // that was left out wouldn't be held back from the balance, and could be paid out again.
        var start = since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var entries = new List<JToken>();
        var cursor = "true";
        for (var page = 0; ; page++)
        {
            if (page == MaxWithdrawalPages)
                throw new ExchangeException($"Kraken lists more than {MaxWithdrawalPages} pages of {asset} withdrawals, so not all of them can be accounted for. Nothing is withdrawn.");
            var result = await kraken.PrivateAsync(credentials, "WithdrawStatus",
                [new("asset", ToKraken(asset)), new("start", start), new("cursor", cursor)], cancellationToken);
            var (list, next) = result switch
            {
                JArray array => (array, null),
                // The paged shape: the entries' array beside next_cursor, whatever Kraken names it.
                JObject paged when paged.Properties().Select(p => p.Value).OfType<JArray>().ToList() is [var array] => (array, Text(paged["next_cursor"])),
                _ => throw new ExchangeException("Kraken sent an unexpected WithdrawStatus response.")
            };
            entries.AddRange(list);
            if (list.Count == 0 || string.IsNullOrEmpty(next) || next == cursor)
                break;
            cursor = next;
        }
        var records = new List<WithdrawalRecord>();
        foreach (var token in entries)
        {
            if (token is not JObject entry)
                throw new ExchangeException("Kraken sent an unexpected withdrawal entry.");
            // Kraken filtered by asset already; an entry of another asset is not counted.
            if (Text(entry["asset"]) is { } entryAsset && FromKrakenAsset(entryAsset) is { } ticker &&
                !string.Equals(ticker, asset, StringComparison.OrdinalIgnoreCase))
                continue;
            var refid = Text(entry["refid"]) is { Length: > 0 } r ? r : throw new ExchangeException("Kraken sent a withdrawal without a reference.");
            if (entry["time"] is not JValue { Type: JTokenType.Integer or JTokenType.Float } time ||
                !long.TryParse(Text(time), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                throw new ExchangeException($"Kraken sent no valid time for withdrawal {refid}.");
            var prop = Text(entry["status-prop"]);
            var status = Text(entry["status"]) switch
            {
                // Cancelled, or sent back by Kraken: the funds stay in (or return to) the account.
                _ when prop is "canceled" or "return" => WithdrawalStatus.Failed,
                "Failure" => WithdrawalStatus.Failed,
                "Success" when prop is not ("onhold" or "cancel-pending") => WithdrawalStatus.Succeeded,
                // Initial, Pending, Settled, on hold, or a status we don't know: not final.
                _ => WithdrawalStatus.InProgress
            };
            records.Add(new WithdrawalRecord(refid, Text(entry["key"]),
                RequiredDecimal(entry["amount"], $"amount of withdrawal {refid}"), OptionalDecimal(entry["fee"]),
                DateTimeOffset.FromUnixTimeSeconds(seconds), status));
        }
        return records;
    }

    public async Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
    {
        var checks = new List<PermissionCheck>
        {
            await CheckAsync("Query Funds", () => kraken.PrivateAsync(credentials, "BalanceEx", [], cancellationToken), orderCheck: false, cancellationToken),
            await CheckAsync("Withdraw Funds", () => kraken.PrivateAsync(credentials, "WithdrawMethods", [new("asset", ToKraken(asset))], cancellationToken),
                orderCheck: false, cancellationToken),
            // The same validate-only order the trading key is tested with: this key must be refused it.
            await LacksPermissionAsync("No Create & Modify Orders", () => kraken.ValidateTestOrderAsync(credentials, cancellationToken),
                "This key can place orders. Create a payout key with only Query Funds and Withdraw Funds.", cancellationToken)
        };
        try
        {
            var problems = await GetKeyProblemsAsync(credentials, cancellationToken);
            checks.Add(new PermissionCheck("No Add or Update Withdrawal Addresses", problems.Count == 0, problems.Count == 0 ? null : string.Join(" ", problems)));
        }
        catch (ExchangeException e)
        {
            checks.Add(new PermissionCheck("No Add or Update Withdrawal Addresses", false, $"Could not read the key's permissions: {e.Message}"));
        }
        return new CredentialsCheck(checks);
    }

    static readonly Dictionary<string, string> ForbiddenPermissions = new()
    {
        ["add-withdraw-address"] = "This key can add withdrawal addresses, so a stolen copy could send funds anywhere. Create a payout key without it.",
        ["update-withdraw-address"] = "This key can update withdrawal addresses, so a stolen copy could redirect funds. Create a payout key without it.",
        ["modify-trades"] = "This key can place orders. Create a payout key with only Query Funds and Withdraw Funds."
    };

    public async Task<IReadOnlyList<string>> GetKeyProblemsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var info = Expect<JObject>(await kraken.PrivateAsync(credentials, "GetApiKeyInfo", [], cancellationToken), "GetApiKeyInfo");
        if (info["permissions"] is not JArray permissions || permissions.Any(p => p.Type != JTokenType.String))
            throw new ExchangeException("Kraken sent no permission list for the key.");
        return permissions.Select(p => (string)p!).Where(ForbiddenPermissions.ContainsKey).Distinct().Select(p => ForbiddenPermissions[p]).ToList();
    }

    async Task<JArray> AddressesAsync(ExchangeCredentials credentials, string asset, string? destination, CancellationToken cancellationToken)
    {
        var parameters = new List<KeyValuePair<string, string>> { new("asset", ToKraken(asset)) };
        if (destination is not null)
            parameters.Add(new("key", destination));
        return Expect<JArray>(await kraken.PrivateAsync(credentials, "WithdrawAddresses", parameters, cancellationToken), "WithdrawAddresses");
    }

    static List<WithdrawalDestination> Destinations(JArray addresses, string asset) =>
        addresses.OfType<JObject>()
            .Where(a => Text(a["asset"]) is not { } listed || string.Equals(FromKrakenAsset(listed), asset, StringComparison.OrdinalIgnoreCase))
            .Where(a => !string.IsNullOrEmpty(Text(a["key"])))
            .Select(a => new WithdrawalDestination(Text(a["key"])!, Text(a["method"]) ?? "", IsTrue(a["verified"])))
            .ToList();

    /// <summary>The precision Kraken shows the asset with (display_decimals), never finer than it accepts.</summary>
    async Task<int> AmountDecimalsAsync(string asset, CancellationToken cancellationToken)
    {
        var result = Expect<JObject>(await kraken.PublicAsync("Assets", "asset", ToKraken(asset), cancellationToken), "Assets");
        var entry = result.Properties().Select(p => p.Value).OfType<JObject>().FirstOrDefault()
                    ?? throw new ExchangeException($"Kraken doesn't know the asset {asset}.");
        foreach (var field in new[] { "display_decimals", "decimals" })
        {
            if (entry[field] is JValue { Type: JTokenType.Integer, Value: long decimals } && decimals is >= 0 and <= MarketRules.MaxAmountDecimals)
                return (int)decimals;
        }
        throw new ExchangeException($"Kraken sent no valid precision for {asset}.");
    }
}
