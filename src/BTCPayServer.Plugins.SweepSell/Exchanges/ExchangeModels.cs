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
}

/// <summary>
/// A deposit address, plus the tag or memo some assets need (e.g. an XRP destination tag). A sender that
/// cannot attach <see cref="Tag"/>, such as an on-chain wallet sweep, must refuse a destination that has one.
/// </summary>
public sealed record DepositDestination(string Address, string? Tag)
{
    public bool RequiresTag => !string.IsNullOrEmpty(Tag);
}

public sealed record MarketRules(string BaseAsset, string QuoteAsset, decimal MinimumAmount, int AmountDecimals)
{
    /// <summary><paramref name="amount"/> rounded down to the market's precision, or 0 when that is below the minimum order.</summary>
    public decimal Sellable(decimal amount)
    {
        if (amount <= 0)
            return 0;
        var factor = (decimal)Math.Pow(10, AmountDecimals);
        var rounded = Math.Floor(amount * factor) / factor;
        return rounded >= MinimumAmount ? rounded : 0;
    }
}

public sealed record OrderResult(bool Placed, IReadOnlyList<string> OrderIds, string Description);

public sealed record PermissionCheck(string Permission, bool Ok, string? Error);

public sealed record CredentialsCheck(IReadOnlyList<PermissionCheck> Checks)
{
    public bool AllOk => Checks.All(c => c.Ok);
}

/// <summary>An error the exchange returned, with its own message (never containing credentials).</summary>
public class ExchangeException(string message) : Exception(message);
