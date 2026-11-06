#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>What a sweep sends, in whole coins.</summary>
public sealed record SweepAmounts(decimal ToExchange, decimal ToCold, decimal Fee, decimal Change)
{
    /// <summary>What leaves the wallet: both outputs and the fee, not the change.</summary>
    public decimal Spent => ToExchange + ToCold + Fee;
}

/// <summary>
/// A signed sweep, ready to broadcast. Broadcasting the same transaction again can never pay twice. <paramref name="Balance"/>
/// is what the wallet's coins added up to when it was built; <paramref name="FeeRate"/> is the fee it pays, in satoshis per
/// virtual byte, when known. <paramref name="EstimatedFeeRate"/> is the estimate, set only when the rule's lowest fee rate
/// was used instead.
/// </summary>
public sealed record SignedSweep(string Txid, string Hex, SweepAmounts Amounts, decimal Balance, decimal? FeeRate = null,
    decimal? EstimatedFeeRate = null);

/// <summary>A sweep that can't be built, with a message for the store owner.</summary>
public class SweepException(string message) : Exception(message);

/// <summary>
/// Builds and signs the sweep transaction. Pure NBitcoin, so it is tested without a node: the wallet adapter
/// supplies the coins, keys and fee rate.
/// </summary>
public static class SweepBuilder
{
    /// <summary>
    /// Sends <paramref name="amount"/> (everything when null or not less than the coins' total) from
    /// <paramref name="coins"/>: <paramref name="coldPercent"/>% of it to <paramref name="cold"/>, the rest to
    /// <paramref name="exchange"/>, which also pays the fee. A partial sweep returns its change to <paramref name="change"/>.
    /// <paramref name="minRelayFee"/> is the node's minimum relay fee; without it, NBitcoin's default (1 sat/vB) applies.
    /// </summary>
    public static SignedSweep Build(Network network, IReadOnlyList<(ICoin Coin, Key Key)> coins, decimal? amount, IDestination exchange,
        IDestination? cold, decimal coldPercent, FeeRate feeRate, IDestination? change, FeeRate? minRelayFee = null)
    {
        if (coins.Count == 0)
            throw new SweepException("There are no coins to sweep.");
        if (coldPercent is < 0 or >= 100)
            throw new SweepException("The cold wallet share must be at least 0% and under 100%.");
        if (coldPercent > 0 && cold is null)
            throw new SweepException("A cold wallet share needs a cold wallet address.");
        var scripts = new[] { exchange.ScriptPubKey, cold?.ScriptPubKey, change?.ScriptPubKey }.Where(s => s is not null).ToList();
        if (scripts.Distinct().Count() != scripts.Count)
            throw new SweepException("The exchange, cold wallet and change addresses must all be different.");
        var total = coins.Select(c => (Money)c.Coin.Amount).Sum();
        var sweepAll = amount is null || Money.Coins(amount.Value) >= total;
        var swept = sweepAll ? total : Money.Coins(amount!.Value);
        if (swept <= Money.Zero)
            throw new SweepException("The amount to sweep must be positive.");
        // Rounded down to the satoshi, so the cold share never exceeds the percentage.
        var toCold = cold is null || coldPercent == 0 ? Money.Zero : Money.Satoshis((long)Math.Floor(swept.Satoshi * coldPercent / 100m));

        var builder = network.CreateTransactionBuilder();
        // Signals replace-by-fee (BIP 125), so a sweep stuck below the going fee can be bumped from the wallet.
        builder.OptInRBF = true;
        if (minRelayFee is not null)
        {
            // Nodes now relay below 1 sat/vB, and BTCPay's fee estimate follows the node's own minimum; the final check
            // must use that minimum too, or a sweep the node would accept is refused here.
            var policy = builder.StandardTransactionPolicy.Clone();
            policy.MinRelayTxFee = minRelayFee;
            builder.StandardTransactionPolicy = policy;
            feeRate = FeeRate.Max(feeRate, minRelayFee);
        }
        builder.AddCoins(coins.Select(c => c.Coin));
        builder.AddKeys(coins.Select(c => c.Key).ToArray());
        if (toCold > Money.Zero)
            builder.Send(cold!, toCold);
        if (sweepAll)
        {
            builder.SendAllRemaining(exchange);
        }
        else
        {
            if (change is null)
                throw new SweepException("A partial sweep needs a change address.");
            builder.Send(exchange, swept - toCold).SubtractFees();
            builder.SetChange(change);
        }
        builder.SendEstimatedFees(feeRate);

        Transaction tx;
        try
        {
            tx = builder.BuildTransaction(sign: true);
        }
        catch (OutputTooSmallException)
        {
            throw new SweepException("An output would be too small to send (dust) after the network fee.");
        }
        catch (NotEnoughFundsException)
        {
            throw new SweepException("The coins don't cover the outputs and the network fee.");
        }
        if (!builder.Verify(tx, out var errors))
            throw new SweepException("The signed sweep didn't verify: " + string.Join("; ", errors.Select(e => e.ToString())));

        decimal Sum(IDestination? destination) => destination is null
            ? 0m
            : tx.Outputs.Where(o => o.ScriptPubKey == destination.ScriptPubKey).Select(o => o.Value).Sum().ToDecimal(MoneyUnit.BTC);
        var fee = tx.GetFee(coins.Select(c => c.Coin).ToArray());
        return new SignedSweep(tx.GetHash().ToString(), tx.ToHex(),
            new SweepAmounts(Sum(exchange), Sum(cold), fee.ToDecimal(MoneyUnit.BTC), Sum(change)),
            total.ToDecimal(MoneyUnit.BTC), Math.Round((decimal)fee.Satoshi / tx.GetVirtualSize(), 2));
    }

    /// <summary>
    /// Replaces <paramref name="original"/>, a sent sweep, with the same transaction at a higher fee rate (BIP 125): same
    /// coins, same outputs, except that the exchange's output (the one of <paramref name="toExchange"/>) pays the extra fee.
    /// <paramref name="coins"/> are the coins it spends, all from the hot wallet.
    /// </summary>
    public static SignedSweep Replace(Network network, Transaction original, IReadOnlyList<(ICoin Coin, Key Key)> coins, decimal toExchange,
        FeeRate feeRate, FeeRate? minRelayFee = null)
    {
        var spent = original.Inputs.Select(i => i.PrevOut).ToList();
        var byOutpoint = coins.ToDictionary(c => c.Coin.Outpoint);
        if (spent.Count == 0 || spent.Any(o => !byOutpoint.ContainsKey(o)) || byOutpoint.Count != spent.Count)
            throw new SweepException("The sweep spends coins this wallet doesn't hold, so it can't be replaced here.");
        var exchangeValue = Money.Coins(toExchange);
        var exchangeOutputs = original.Outputs.AsIndexedOutputs().Where(o => o.TxOut.Value == exchangeValue).ToList();
        if (exchangeOutputs.Count != 1)
            throw new SweepException("Couldn't tell which of the sweep's outputs goes to the exchange, so it isn't replaced.");
        var exchangeIndex = (int)exchangeOutputs[0].N;

        var inputs = spent.Select(o => byOutpoint[o].Coin).ToArray();
        var total = inputs.Select(c => (Money)c.Amount).Sum();
        var oldFee = total - original.Outputs.Select(o => o.Value).Sum();
        // The signatures are made again, and may come out a byte longer.
        var vsize = original.GetVirtualSize() + original.Inputs.Count;
        var newFee = feeRate.GetFee(vsize);
        // Nodes accept a replacement only if it pays at least the incremental relay fee (1 sat/vB by default) more.
        var incremental = new FeeRate(Money.Satoshis(1000), 1000);
        var lowest = oldFee + incremental.GetFee(vsize);
        if (newFee < lowest)
            throw new SweepException($"The new fee rate must be at least {Math.Ceiling(lowest.Satoshi * 100m / vsize) / 100m} per vB: " +
                                     $"the sweep already pays about {Math.Round(oldFee.Satoshi / (decimal)original.GetVirtualSize(), 2)}, and nodes want at least 1 more.");
        var newExchange = original.Outputs[exchangeIndex].Value - (newFee - oldFee);
        var dustRate = minRelayFee ?? new FeeRate(Money.Satoshis(1000), 1000);
        if (newExchange <= Money.Zero || new TxOut(newExchange, original.Outputs[exchangeIndex].ScriptPubKey).IsDust(dustRate))
            throw new SweepException("At that fee rate, too little would be left for the exchange.");

        var tx = original.Clone();
        tx.Outputs[exchangeIndex].Value = newExchange;
        foreach (var input in tx.Inputs)
        {
            // Still replaceable, so it can be bumped again.
            input.Sequence = new Sequence(0xfffffffd);
            input.ScriptSig = Script.Empty;
            input.WitScript = WitScript.Empty;
        }
        var builder = network.CreateTransactionBuilder();
        if (minRelayFee is not null)
        {
            var policy = builder.StandardTransactionPolicy.Clone();
            policy.MinRelayTxFee = minRelayFee;
            builder.StandardTransactionPolicy = policy;
        }
        builder.AddCoins(inputs);
        builder.AddKeys(coins.Select(c => c.Key).ToArray());
        builder.SignTransactionInPlace(tx);
        if (!builder.Verify(tx, out var errors))
            throw new SweepException("The replacement didn't verify: " + string.Join("; ", errors.Select(e => e.ToString())));

        var fee = tx.GetFee(inputs);
        var exchangeScript = original.Outputs[exchangeIndex].ScriptPubKey;
        var others = tx.Outputs.Where((_, i) => i != exchangeIndex).Select(o => o.Value).Sum();
        return new SignedSweep(tx.GetHash().ToString(), tx.ToHex(),
            new SweepAmounts(newExchange.ToDecimal(MoneyUnit.BTC), others.ToDecimal(MoneyUnit.BTC), fee.ToDecimal(MoneyUnit.BTC), 0m),
            total.ToDecimal(MoneyUnit.BTC), Math.Round((decimal)fee.Satoshi / tx.GetVirtualSize(), 2));
    }
}
