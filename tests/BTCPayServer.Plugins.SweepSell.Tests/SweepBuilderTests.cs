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
    public void SameAddressTwiceIsRefused()
    {
        var address = Address();
        Assert.Throws<SweepException>(() => SweepBuilder.Build(Network, [CoinOf(1m, 0)], null, address, address, 10, Fee, null));
    }
}
