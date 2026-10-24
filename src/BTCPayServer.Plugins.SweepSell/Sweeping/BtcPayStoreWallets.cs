#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// The store wallets through BTCPay and NBXplorer: the same calls BTCPay's own automated payout processor uses to
/// spend from a hot wallet (unspent coins, the account key kept by NBXplorer, the fee provider, broadcast).
/// </summary>
public class BtcPayStoreWallets(StoreRepository stores, PaymentMethodHandlerDictionary handlers, BTCPayNetworkProvider networks,
    ExplorerClientProvider explorers, BTCPayWalletProvider walletProvider, IFeeProviderFactory fees) : IStoreWallets
{
    public async Task<IReadOnlyList<string>> GetCryptoCodesAsync(string storeId, CancellationToken cancellationToken)
    {
        var store = await stores.FindStore(storeId);
        if (store is null)
            return [];
        return networks.GetAll().OfType<BTCPayNetwork>()
            .Where(n => store.GetDerivationSchemeSettings(handlers, n.CryptoCode) is not null)
            .Select(n => n.CryptoCode)
            .ToList();
    }

    public async Task<StoreWallet?> GetWalletAsync(string storeId, string cryptoCode, CancellationToken cancellationToken)
    {
        if (networks.GetNetwork<BTCPayNetwork>(cryptoCode) is null)
            return null;
        var store = await stores.FindStore(storeId);
        var scheme = store?.GetDerivationSchemeSettings(handlers, cryptoCode);
        return scheme?.AccountDerivation is null ? null : new StoreWallet(storeId, cryptoCode, scheme.AccountDerivation.ToString(), scheme.IsHotWallet);
    }

    public async Task<decimal> GetBalanceAsync(StoreWallet wallet, bool includeUnconfirmed, CancellationToken cancellationToken)
    {
        var (network, derivation) = Wallet(wallet);
        // Coins already spent by an unconfirmed transaction (e.g. an earlier sweep) are left out by NBXplorer.
        var coins = await walletProvider.GetWallet(network).GetUnspentCoins(derivation, excludeUnconfirmed: !includeUnconfirmed, cancellationToken);
        return coins.Select(c => c.Coin.Amount).Sum().ToDecimal(MoneyUnit.BTC);
    }

    public string? CheckAddress(string cryptoCode, string address)
    {
        var network = Network(cryptoCode);
        try
        {
            BitcoinAddress.Create(address, network.NBitcoinNetwork);
            return null;
        }
        catch (FormatException)
        {
            return $"\"{address}\" isn't a {cryptoCode} address on this server's network.";
        }
    }

    public string? CheckColdDestination(string cryptoCode, string destination)
    {
        try
        {
            GetColdAddress(cryptoCode, destination, 0);
            return null;
        }
        catch (SweepException e)
        {
            return e.Message;
        }
    }

    public (string Address, bool Derived) GetColdAddress(string cryptoCode, string destination, int index)
    {
        var network = Network(cryptoCode);
        destination = destination.Trim();
        if (CheckAddress(cryptoCode, destination) is null)
            return (destination, false);
        if (index < 0)
            throw new SweepException("The cold wallet address index can't be negative.");
        DerivationStrategyBase strategy;
        try
        {
            // The same formats BTCPay accepts when setting up a wallet (xpub, ypub, zpub, BTCPay derivation schemes).
            strategy = network.GetDerivationSchemeParser().Parse(destination);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or NotSupportedException)
        {
            throw new SweepException($"The cold wallet isn't a {cryptoCode} address or extended public key this server understands.");
        }
        var script = strategy.GetLineFor(DerivationFeature.Deposit).Derive((uint)index).ScriptPubKey;
        return (script.GetDestinationAddress(network.NBitcoinNetwork)?.ToString()
                ?? throw new SweepException("The cold wallet key doesn't give a standard address."), true);
    }

    public async Task<SignedSweep> BuildAsync(StoreWallet wallet, SweepRequest request, CancellationToken cancellationToken)
    {
        if (!wallet.IsHot)
            throw new SweepException($"The {wallet.CryptoCode} wallet isn't a hot wallet, so this server can't sign a sweep.");
        var (network, derivation) = Wallet(wallet);
        var explorer = explorers.GetExplorerClient(network);
        var accountKey = await explorer.GetMetadataAsync<string>(derivation, WellknownMetadataKeys.AccountHDKey, cancellationToken)
                         ?? throw new SweepException($"The {wallet.CryptoCode} hot wallet's private key isn't on this server.");
        var key = ExtKey.Parse(accountKey, network.NBitcoinNetwork);
        var coins = await walletProvider.GetWallet(network).GetUnspentCoins(derivation, excludeUnconfirmed: !request.IncludeUnconfirmed, cancellationToken);
        var signing = coins.Select(c => ((ICoin)c.Coin, key.Derive(c.KeyPath).PrivateKey)).ToList();
        var exchange = Address(network, request.ExchangeAddress);
        var cold = request.ColdAddress is null ? null : Address(network, request.ColdAddress);
        // A partial sweep (daily cap) keeps its change in the wallet.
        var change = request.Amount is null
            ? null
            : (await explorer.GetUnusedAsync(derivation, DerivationFeature.Change, 0, false, cancellationToken))?.Address
              ?? throw new SweepException($"The {wallet.CryptoCode} wallet has no change address.");
        var feeRate = await fees.CreateFeeProvider(network).GetFeeRateAsync(request.FeeTargetBlocks);
        return SweepBuilder.Build(network.NBitcoinNetwork, signing, request.Amount, exchange, cold, request.ColdPercent, feeRate, change);
    }

    public async Task<BroadcastOutcome> BroadcastAsync(string cryptoCode, string hex, CancellationToken cancellationToken)
    {
        var network = Network(cryptoCode);
        var result = await explorers.GetExplorerClient(network).BroadcastAsync(Transaction.Parse(hex, network.NBitcoinNetwork), cancellationToken);
        return result.Success || result.RPCCode == RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN
            ? new BroadcastOutcome(true, null)
            : new BroadcastOutcome(false, $"{result.RPCCode} {result.RPCCodeMessage} {result.RPCMessage}".Trim());
    }

    public async Task<TransactionStatus> GetStatusAsync(string cryptoCode, string txid, CancellationToken cancellationToken)
    {
        var network = Network(cryptoCode);
        var tx = await explorers.GetExplorerClient(network).GetTransactionAsync(uint256.Parse(txid), cancellationToken);
        return tx switch
        {
            null => TransactionStatus.Unknown,
            { Confirmations: > 0 } => TransactionStatus.Confirmed,
            { ReplacedBy: not null } => TransactionStatus.Replaced,
            _ => TransactionStatus.Unconfirmed
        };
    }

    BTCPayNetwork Network(string cryptoCode) =>
        networks.GetNetwork<BTCPayNetwork>(cryptoCode) ?? throw new SweepException($"This server doesn't support {cryptoCode}.");

    (BTCPayNetwork, DerivationStrategyBase) Wallet(StoreWallet wallet)
    {
        var network = Network(wallet.CryptoCode);
        return (network, network.NBXplorerNetwork.DerivationStrategyFactory.Parse(wallet.Key));
    }

    static BitcoinAddress Address(BTCPayNetwork network, string address)
    {
        try
        {
            return BitcoinAddress.Create(address, network.NBitcoinNetwork);
        }
        catch (FormatException)
        {
            throw new SweepException($"\"{address}\" isn't a {network.CryptoCode} address on this server's network.");
        }
    }
}
