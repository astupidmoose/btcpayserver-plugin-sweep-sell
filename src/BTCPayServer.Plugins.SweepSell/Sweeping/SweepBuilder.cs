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
/// is what the wallet's coins added up to when it was built.
/// </summary>
public sealed record SignedSweep(string Txid, string Hex, SweepAmounts Amounts, decimal Balance);

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
    /// </summary>
    public static SignedSweep Build(Network network, IReadOnlyList<(ICoin Coin, Key Key)> coins, decimal? amount, IDestination exchange,
        IDestination? cold, decimal coldPercent, FeeRate feeRate, IDestination? change)
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
        return new SignedSweep(tx.GetHash().ToString(), tx.ToHex(),
            new SweepAmounts(Sum(exchange), Sum(cold), tx.GetFee(coins.Select(c => c.Coin).ToArray()).ToDecimal(MoneyUnit.BTC), Sum(change)),
            total.ToDecimal(MoneyUnit.BTC));
    }
}
