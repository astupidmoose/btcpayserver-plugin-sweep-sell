#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.SweepSell.Exchanges;

/// <summary>An exchange API key in plain text. Only ever held in memory; see <see cref="Settings.SecretProtector"/>.</summary>
public sealed record ExchangeCredentials(string ApiKey, string ApiSecret)
{
    // Keep the secret out of logs and exception messages.
    public override string ToString() => "ExchangeCredentials { ApiKey = ***, ApiSecret = *** }";

    /// <summary>Tells keys apart without storing them: the start of the SHA-256 of the API key (the public half).</summary>
    public string Fingerprint() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApiKey)))[..16];
}

/// <summary>
/// A deposit address, plus the tag or memo some assets need (e.g. an XRP destination tag). A sender that
/// cannot attach <see cref="Tag"/>, such as an on-chain wallet sweep, must refuse a destination that has one.
/// </summary>
public sealed record DepositDestination(string Address, string? Tag)
{
    public bool RequiresTag => !string.IsNullOrEmpty(Tag);
}

public sealed record MarketRules(string BaseAsset, string QuoteAsset, decimal MinimumAmount, decimal MinimumCost, int AmountDecimals)
{
    /// <summary>Largest precision a decimal can scale by without overflow for realistic amounts.</summary>
    public const int MaxAmountDecimals = 18;

    /// <summary>
    /// <paramref name="amount"/> rounded down to the market's precision, or 0 when that is below the minimum order
    /// or its value at <paramref name="price"/> (in the quote asset) is below the minimum order cost.
    /// </summary>
    public decimal Sellable(decimal amount, decimal price)
    {
        if (amount <= 0 || price <= 0)
            return 0;
        var factor = Factor();
        var rounded = Math.Floor(amount * factor) / factor;
        return rounded >= MinimumAmount && rounded * price >= MinimumCost ? rounded : 0;
    }

    /// <summary>The smallest amount this market accepts at <paramref name="price"/>, rounded up to its precision.</summary>
    public decimal MinimumSellAmount(decimal price)
    {
        if (price <= 0)
            throw new ExchangeException($"No valid price for {BaseAsset}/{QuoteAsset}.");
        var factor = Factor();
        var minimum = Math.Max(MinimumAmount, MinimumCost / price);
        return Math.Ceiling(minimum * factor) / factor;
    }

    // Built in decimal, never double: amounts are money.
    decimal Factor()
    {
        if (AmountDecimals is < 0 or > MaxAmountDecimals)
            throw new ExchangeException($"Unsupported amount precision {AmountDecimals} for {BaseAsset}/{QuoteAsset}.");
        var factor = 1m;
        for (var i = 0; i < AmountDecimals; i++)
            factor *= 10m;
        return factor;
    }
}

public sealed record OrderResult(bool Placed, IReadOnlyList<string> OrderIds, string Description);

/// <summary>
/// What the exchange knows about the orders sent with one client order ID. <see cref="Open"/> means some of it can
/// still fill; <see cref="ExecutedAmount"/> is what has filled so far, in the base asset.
/// </summary>
public sealed record OrderLookup(IReadOnlyList<string> OrderIds, bool Open, decimal ExecutedAmount);

public sealed record PermissionCheck(string Permission, bool Ok, string? Error);

public sealed record CredentialsCheck(IReadOnlyList<PermissionCheck> Checks)
{
    public bool AllOk => Checks.All(c => c.Ok);
}

/// <summary>An error the exchange returned, with its own message (never containing credentials).</summary>
public class ExchangeException(string message) : Exception(message);
