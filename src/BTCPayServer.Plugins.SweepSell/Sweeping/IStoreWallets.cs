#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// A store's on-chain wallet for one coin. <see cref="Key"/> identifies the wallet itself (its derivation scheme), so a
/// wallet shared by several stores is recognised as one.
/// </summary>
public sealed record StoreWallet(string StoreId, string CryptoCode, string Key, bool IsHot);

/// <summary>
/// What the node said about a broadcast. <see cref="Accepted"/> includes a transaction already in the mempool or a block.
/// </summary>
public sealed record BroadcastOutcome(bool Accepted, string? Error);

public enum TransactionStatus
{
    /// <summary>Neither in the node's mempool nor in a block, as far as the wallet knows.</summary>
    Unknown,
    Unconfirmed,
    Confirmed
}

/// <summary>What to sweep. <see cref="Amount"/> null sweeps everything; less leaves change in the wallet.</summary>
public sealed record SweepRequest(bool IncludeUnconfirmed, decimal? Amount, string ExchangeAddress, string? ColdAddress, decimal ColdPercent,
    int FeeTargetBlocks);

/// <summary>The store wallets, as the sweep job needs them. BTCPay's implementation is <see cref="BtcPayStoreWallets"/>.</summary>
public interface IStoreWallets
{
    /// <summary>The coins the store has an on-chain wallet for, e.g. BTC, LTC.</summary>
    Task<IReadOnlyList<string>> GetCryptoCodesAsync(string storeId, CancellationToken cancellationToken);

    /// <summary>The store's on-chain wallet for <paramref name="cryptoCode"/>, or null when it has none.</summary>
    Task<StoreWallet?> GetWalletAsync(string storeId, string cryptoCode, CancellationToken cancellationToken);

    /// <summary>The total of the wallet's spendable coins, confirmed only unless <paramref name="includeUnconfirmed"/>.</summary>
    Task<decimal> GetBalanceAsync(StoreWallet wallet, bool includeUnconfirmed, CancellationToken cancellationToken);

    /// <summary>Why <paramref name="address"/> can't receive <paramref name="cryptoCode"/>, or null when it can.</summary>
    string? CheckAddress(string cryptoCode, string address);

    /// <summary>Why <paramref name="destination"/> isn't a usable cold wallet (address or extended public key), or null.</summary>
    string? CheckColdDestination(string cryptoCode, string destination);

    /// <summary>
    /// The cold wallet address to use: <paramref name="destination"/> itself when it is an address, else its receive
    /// address at <paramref name="index"/> (<c>Derived</c> true).
    /// </summary>
    (string Address, bool Derived) GetColdAddress(string cryptoCode, string destination, int index);

    /// <summary>Builds and signs the sweep with the hot wallet's key. Nothing is broadcast. Throws <see cref="SweepException"/>.</summary>
    Task<SignedSweep> BuildAsync(StoreWallet wallet, SweepRequest request, CancellationToken cancellationToken);

    /// <summary>Broadcasts a signed transaction. Throws when the node can't be reached, so the outcome is unknown.</summary>
    Task<BroadcastOutcome> BroadcastAsync(string cryptoCode, string hex, CancellationToken cancellationToken);

    Task<TransactionStatus> GetStatusAsync(string cryptoCode, string txid, CancellationToken cancellationToken);
}
