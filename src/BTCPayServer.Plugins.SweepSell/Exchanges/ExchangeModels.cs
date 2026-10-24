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

/// <summary>
/// Prices of one <c>BaseAsset</c> in the quote asset. <see cref="DailyAverage"/> (volume-weighted, last 24 hours) is the
/// reference for price floors: a single trade on a thin market moves <see cref="Last"/> but barely moves it.
/// </summary>
public sealed record MarketPrice(decimal Last, decimal Bid, decimal DailyAverage);

public sealed record MarketRules(string BaseAsset, string QuoteAsset, decimal MinimumAmount, decimal MinimumCost, int AmountDecimals,
    int PriceDecimals)
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

    /// <summary>
    /// The lowest price this market accepts that is not below <paramref name="floor"/>: rounded up to the price precision,
    /// so a sell limit never undercuts the floor.
    /// </summary>
    public decimal LimitPriceAtLeast(decimal floor)
    {
        if (floor <= 0)
            throw new ExchangeException($"No valid price floor for {BaseAsset}/{QuoteAsset}.");
        var factor = Factor(PriceDecimals, "price");
        return Math.Ceiling(floor * factor) / factor;
    }

    decimal Factor() => Factor(AmountDecimals, "amount");

    // Built in decimal, never double: amounts are money.
    decimal Factor(int decimals, string what)
    {
        if (decimals is < 0 or > MaxAmountDecimals)
            throw new ExchangeException($"Unsupported {what} precision {decimals} for {BaseAsset}/{QuoteAsset}.");
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
            factor *= 10m;
        return factor;
    }
}

public enum DepositState
{
    /// <summary>Seen by the exchange, not credited yet (e.g. waiting for confirmations or on hold).</summary>
    Pending,
    /// <summary>Credited: the amount can be traded.</summary>
    Credited,
    /// <summary>The exchange won't credit it; the owner has to sort it out with the exchange.</summary>
    Failed
}

public sealed record DepositStatus(DepositState State, decimal Amount);

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

/// <summary>
/// An error the exchange returned, with its own message (never containing credentials). <see cref="Rejected"/> means the
/// exchange answered and refused the request without acting on it, e.g. an order refused for insufficient funds. It is
/// false whenever the outcome is unknown: a timeout, a server error, or an answer that can't be read.
/// </summary>
public class ExchangeException(string message, bool rejected = false) : Exception(message)
{
    public bool Rejected { get; } = rejected;
}
