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
}
