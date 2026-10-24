#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SweepSell.Exchanges;

/// <summary>
/// Withdrawals for fiat payout, kept apart from <see cref="IExchangeConnector"/> so the trading key never needs
/// withdrawal permission. It is used with its own key, which needs only to query funds and withdraw.
/// A destination is the name of a withdrawal destination the owner already saved (and the exchange verified) in their
/// exchange account. There is deliberately no call that adds, changes or looks up a destination by address: funds can
/// only go where the account owner already set up to receive them.
/// </summary>
public interface IWithdrawalConnector
{
    /// <summary>The same id as the exchange's <see cref="IExchangeConnector.Id"/>, e.g. "kraken".</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>What the withdraw-only key needs, shown on the payout page.</summary>
    string KeyPermissionsHelp { get; }

    /// <summary>Spendable balance of <paramref name="asset"/>: the total minus anything held by open orders.</summary>
    Task<decimal> GetAvailableBalanceAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken);

    /// <summary>The withdrawal destinations the exchange lists for <paramref name="asset"/>, shown when the key is tested.</summary>
    Task<IReadOnlyList<WithdrawalDestination>> GetDestinationsAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken);

    /// <summary>
    /// What withdrawing <paramref name="amount"/> (rounded down to the asset's precision) to <paramref name="destination"/>
    /// would cost and send. Fails for a destination that isn't saved in the account, or that the exchange lists as unverified.
    /// </summary>
    Task<WithdrawalQuote> QuoteAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws <paramref name="amount"/> to the saved <paramref name="destination"/>, failing at the exchange if its fee
    /// would be above <paramref name="maxFee"/>. Returns the exchange's reference. Exchanges take no client ID for
    /// withdrawals, so after a lost response a caller finds it with <see cref="GetRecentWithdrawalsAsync"/> and never
    /// retries blindly. Accepted without a reference is an error, never a success.
    /// </summary>
    Task<string> WithdrawAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount, decimal maxFee,
        CancellationToken cancellationToken);

    /// <summary>Withdrawals of <paramref name="asset"/> requested since <paramref name="since"/>, from any source.</summary>
    Task<IReadOnlyList<WithdrawalRecord>> GetRecentWithdrawalsAsync(ExchangeCredentials credentials, string asset, DateTimeOffset since,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks the key can query funds and withdraw <paramref name="asset"/>, and that it can NOT trade, without moving funds.
    /// </summary>
    Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken);

    /// <summary>
    /// Why the key must not be used for payouts, read from the exchange's own record of the key's permissions: it can
    /// add or change withdrawal destinations (which would let a stolen key send funds anywhere), or trade. Empty when fine.
    /// </summary>
    Task<IReadOnlyList<string>> GetKeyProblemsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken);
}

/// <summary>A withdrawal destination saved in the exchange account, by the name the owner gave it there.</summary>
public sealed record WithdrawalDestination(string Name, string Method, bool Verified);

/// <summary>
/// A withdrawal's cost before sending it. <see cref="Amount"/> is what leaves the account (rounded down to the asset's
/// precision); <see cref="NetAmount"/> is what arrives after <see cref="Fee"/>. <see cref="Minimum"/> and
/// <see cref="Limit"/> are the exchange's smallest and largest net amounts right now.
/// </summary>
public sealed record WithdrawalQuote(string Destination, string Method, decimal Amount, decimal NetAmount, decimal Fee, decimal Minimum,
    decimal Limit);

public enum WithdrawalStatus
{
    /// <summary>Requested and not final yet (including on hold or with a cancellation pending).</summary>
    InProgress,
    Succeeded,
    /// <summary>Failed, cancelled or returned: nothing left the account for good.</summary>
    Failed
}

/// <summary>
/// A withdrawal the exchange lists. <see cref="Destination"/> is the saved destination's name when the exchange reports it.
/// </summary>
public sealed record WithdrawalRecord(string Reference, string? Destination, decimal Amount, decimal Fee, DateTimeOffset RequestedAt,
    WithdrawalStatus Status);

/// <summary>The exchanges a store can pay out from. Adding one = implementing <see cref="IWithdrawalConnector"/> and registering it.</summary>
public class WithdrawalRegistry(IEnumerable<IWithdrawalConnector> connectors)
{
    readonly Dictionary<string, IWithdrawalConnector> _byId = System.Linq.Enumerable.ToDictionary(connectors, c => c.Id);

    public IReadOnlyCollection<IWithdrawalConnector> All => _byId.Values;

    public IWithdrawalConnector? Find(string? id) => id is not null && _byId.TryGetValue(id, out var c) ? c : null;
}
