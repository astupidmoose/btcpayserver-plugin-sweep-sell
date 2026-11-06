using BTCPayServer.Plugins.SweepSell.Sweeping;
using NBitcoin;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class SweepBuilderTests
{
    static readonly Network Network = Network.RegTest;
    static readonly FeeRate Fee = new(Money.Satoshis(10), 1); // 10 sat/vB

    // Invented keys and coins on regtest.
    static (ICoin, Key) CoinOf(decimal btc, int n)
    {
        var key = new Key();
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), n);
        return (new Coin(outpoint, new TxOut(Money.Coins(btc), key.GetScriptPubKey(ScriptPubKeyType.Segwit))), key);
    }

    static BitcoinAddress Address() => new Key().GetAddress(ScriptPubKeyType.Segwit, Network);

    [Fact]
    public void SweepsEverythingToTheExchangeWhichPaysTheFee()
    {
        var exchange = Address();
        var sweep = SweepBuilder.Build(Network, [CoinOf(0.5m, 0), CoinOf(0.25m, 1)], null, exchange, null, 0, Fee, null);
        var tx = Transaction.Parse(sweep.Hex, Network);
        Assert.Equal(2, tx.Inputs.Count);
        var output = Assert.Single(tx.Outputs);
        Assert.Equal(exchange.ScriptPubKey, output.ScriptPubKey);
        Assert.Equal(0.75m, sweep.Amounts.ToExchange + sweep.Amounts.Fee);
        Assert.True(sweep.Amounts.Fee > 0);
        Assert.Equal(0m, sweep.Amounts.Change);
        Assert.Equal(tx.GetHash().ToString(), sweep.Txid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.3)]
    public void EverySweepSignalsReplaceByFee(double? amount)
    {
        var sweep = SweepBuilder.Build(Network, [CoinOf(0.5m, 0), CoinOf(0.25m, 1)], (decimal?)amount, Address(), Address(), 10, Fee, Address());
        var tx = Transaction.Parse(sweep.Hex, Network);
        Assert.All(tx.Inputs, input => Assert.True(input.Sequence.IsRBF));
        Assert.True(tx.RBF);
    }

    [Fact]
    public void SplitsTheColdShareBeforeTheFee()
    {
        var exchange = Address();
        var cold = Address();
        var sweep = SweepBuilder.Build(Network, [CoinOf(1m, 0)], null, exchange, cold, 30m, Fee, null);
        Assert.Equal(0.3m, sweep.Amounts.ToCold);
        Assert.Equal(1m, sweep.Amounts.ToExchange + sweep.Amounts.ToCold + sweep.Amounts.Fee);
    }

    [Fact]
    public void PartialSweepReturnsChange()
    {
        var exchange = Address();
        var change = Address();
        var sweep = SweepBuilder.Build(Network, [CoinOf(1m, 0)], 0.4m, exchange, null, 0, Fee, change);
        Assert.Equal(0.4m, sweep.Amounts.ToExchange + sweep.Amounts.Fee);
        Assert.Equal(0.6m, sweep.Amounts.Change);
        Assert.Equal(0.4m, sweep.Amounts.Spent);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-1)]
    public void ColdShareMustLeaveSomethingForTheExchange(decimal percent)
    {
        Assert.Throws<SweepException>(() => SweepBuilder.Build(Network, [CoinOf(1m, 0)], null, Address(), Address(), percent, Fee, null));
    }

    [Fact]
    public void DustAfterTheFeeIsRefused()
    {
        Assert.Throws<SweepException>(() => SweepBuilder.Build(Network, [CoinOf(0.00000500m, 0)], null, Address(), null, 0, Fee, null));
    }

    [Fact]
    public void AFeeBelowOneSatPerVbyteIsBuiltWhenTheNodeRelaysIt()
    {
        var low = new FeeRate(Money.Satoshis(130), 1000); // 0.13 sat/vB
        var nodeMinimum = new FeeRate(Money.Satoshis(100), 1000); // 0.1 sat/vB
        var sweep = SweepBuilder.Build(Network, [CoinOf(0.01m, 0)], null, Address(), null, 0, low, null, nodeMinimum);
        var tx = Transaction.Parse(sweep.Hex, Network);
        Assert.InRange(sweep.Amounts.Fee, 0.00000001m, low.GetFee(tx.GetVirtualSize() + 1).ToDecimal(MoneyUnit.BTC));
    }

    [Fact]
    public void TheFeeRatePaidIsReported()
    {
        var rate = new FeeRate(Money.Satoshis(12_000), 1000); // 12 sat/vB
        var sweep = SweepBuilder.Build(Network, [CoinOf(0.01m, 0)], null, Address(), null, 0, rate, null);
        var tx = Transaction.Parse(sweep.Hex, Network);
        Assert.Equal(Math.Round(sweep.Amounts.Fee * 100_000_000m / tx.GetVirtualSize(), 2), sweep.FeeRate);
        Assert.InRange(sweep.FeeRate!.Value, 11.9m, 12.5m);
    }

    [Fact]
    public void WithoutTheNodesMinimumAFeeBelowOneSatPerVbyteIsRefused()
    {
        var low = new FeeRate(Money.Satoshis(130), 1000);
        var e = Assert.Throws<SweepException>(() => SweepBuilder.Build(Network, [CoinOf(0.01m, 0)], null, Address(), null, 0, low, null));
        Assert.Contains("didn't verify", e.Message);
    }

    [Fact]
    public void AFeeBelowTheNodesMinimumIsRaisedToIt()
    {
        var nodeMinimum = new FeeRate(Money.Satoshis(1000), 1000); // 1 sat/vB
        var sweep = SweepBuilder.Build(Network, [CoinOf(0.01m, 0)], null, Address(), null, 0, new FeeRate(Money.Satoshis(100), 1000), null, nodeMinimum);
        var tx = Transaction.Parse(sweep.Hex, Network);
        Assert.True(sweep.Amounts.Fee >= nodeMinimum.GetFee(tx.GetVirtualSize()).ToDecimal(MoneyUnit.BTC));
    }

    [Fact]
    public void SameAddressTwiceIsRefused()
    {
        var address = Address();
        Assert.Throws<SweepException>(() => SweepBuilder.Build(Network, [CoinOf(1m, 0)], null, address, address, 10, Fee, null));
    }

    [Fact]
    public void ReplacementPaysTheHigherFeeFromTheExchangeOutputOnly()
    {
        var coins = new[] { CoinOf(0.5m, 0), CoinOf(0.25m, 1) };
        var (exchange, cold) = (Address(), Address());
        // Sent at 0.11 sat/vB, which a node relaying down to 0.1 accepts.
        var nodeMinimum = new FeeRate(Money.Satoshis(100), 1000);
        var sweep = SweepBuilder.Build(Network, coins, null, exchange, cold, 20, new FeeRate(Money.Satoshis(110), 1000), null, nodeMinimum);
        var original = Transaction.Parse(sweep.Hex, Network);

        var bumped = SweepBuilder.Replace(Network, original, coins, sweep.Amounts.ToExchange, new FeeRate(3m), nodeMinimum);
        var tx = Transaction.Parse(bumped.Hex, Network);
        Assert.NotEqual(sweep.Txid, bumped.Txid);
        Assert.Equal(original.Inputs.Select(i => i.PrevOut), tx.Inputs.Select(i => i.PrevOut));
        Assert.True(tx.RBF);
        Assert.Equal(cold.ScriptPubKey, Assert.Single(tx.Outputs, o => o.Value == Money.Coins(sweep.Amounts.ToCold)).ScriptPubKey);
        var toExchange = Assert.Single(tx.Outputs, o => o.ScriptPubKey == exchange.ScriptPubKey).Value.ToDecimal(MoneyUnit.BTC);
        Assert.Equal(toExchange, bumped.Amounts.ToExchange);
        Assert.True(bumped.Amounts.Fee > sweep.Amounts.Fee);
        Assert.Equal(sweep.Amounts.ToExchange + sweep.Amounts.Fee, toExchange + bumped.Amounts.Fee); // the extra fee came from the exchange's share
        Assert.InRange(bumped.FeeRate!.Value, 3m, 3.2m);
    }

    [Fact]
    public void ReplacementCanBeBumpedAgain()
    {
        var coins = new[] { CoinOf(0.5m, 0) };
        var sweep = SweepBuilder.Build(Network, coins, null, Address(), null, 0, Fee, null);
        var once = SweepBuilder.Replace(Network, Transaction.Parse(sweep.Hex, Network), coins, sweep.Amounts.ToExchange, new FeeRate(20m));
        var twice = SweepBuilder.Replace(Network, Transaction.Parse(once.Hex, Network), coins, once.Amounts.ToExchange, new FeeRate(30m));
        Assert.True(twice.Amounts.Fee > once.Amounts.Fee);
    }

    [Fact]
    public void ReplacementMustPayAtLeastOneSatPerVbyteMore()
    {
        var coins = new[] { CoinOf(0.5m, 0) };
        var sweep = SweepBuilder.Build(Network, coins, null, Address(), null, 0, Fee, null); // 10 sat/vB
        var e = Assert.Throws<SweepException>(() =>
            SweepBuilder.Replace(Network, Transaction.Parse(sweep.Hex, Network), coins, sweep.Amounts.ToExchange, new FeeRate(10.5m)));
        Assert.Contains("must be at least", e.Message);
    }

    [Fact]
    public void ReplacementNeedsEveryCoinTheSweepSpent()
    {
        var coins = new[] { CoinOf(0.5m, 0), CoinOf(0.25m, 1) };
        var sweep = SweepBuilder.Build(Network, coins, null, Address(), null, 0, Fee, null);
        Assert.Throws<SweepException>(() =>
            SweepBuilder.Replace(Network, Transaction.Parse(sweep.Hex, Network), [coins[0]], sweep.Amounts.ToExchange, new FeeRate(30m)));
    }

    [Fact]
    public void ReplacementNeedsToKnowTheExchangeOutput()
    {
        var coins = new[] { CoinOf(0.5m, 0) };
        var sweep = SweepBuilder.Build(Network, coins, null, Address(), Address(), 10, Fee, null);
        var e = Assert.Throws<SweepException>(() =>
            SweepBuilder.Replace(Network, Transaction.Parse(sweep.Hex, Network), coins, 0.123m, new FeeRate(30m)));
        Assert.Contains("which of the sweep's outputs", e.Message);
    }

    [Fact]
    public void ReplacementThatLeavesDustForTheExchangeIsRefused()
    {
        var coins = new[] { CoinOf(0.00002m, 0) };
        var sweep = SweepBuilder.Build(Network, coins, null, Address(), null, 0, new FeeRate(Money.Satoshis(1000), 1000), null);
        Assert.Throws<SweepException>(() =>
            SweepBuilder.Replace(Network, Transaction.Parse(sweep.Hex, Network), coins, sweep.Amounts.ToExchange, new FeeRate(17m)));
    }
}
