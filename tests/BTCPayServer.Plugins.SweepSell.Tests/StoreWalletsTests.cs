using BTCPayServer.Plugins.SweepSell.Sweeping;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.SweepSell.Tests;

/// <summary>The address checks and cold wallet derivation of <see cref="BtcPayStoreWallets"/>, on regtest with invented keys.</summary>
public class StoreWalletsTests
{
    static readonly NBXplorerNetworkProvider Explorers = new(ChainName.Regtest);
    static readonly BTCPayNetwork Btc = new BTCPayNetwork
    {
        CryptoCode = "BTC",
        NBXplorerNetwork = Explorers.GetFromCryptoCode("BTC"),
        CoinType = new KeyPath("1'")
    }.SetDefaultElectrumMapping(ChainName.Regtest);

    static readonly BtcPayStoreWallets Wallets = new(null!, null!,
        new BTCPayNetworkProvider([Btc], Explorers, new BTCPayServer.Logging.Logs()), null!, null!, null!, null!);

    // An invented account key; nothing here holds funds.
    static readonly ExtPubKey Account = ExtKey.CreateFromSeed(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()).Derive(new KeyPath("84'/1'/0'")).Neuter();
    static readonly Network Regtest = Network.RegTest;

    static string Receive(int index) => Account.Derive(0).Derive((uint)index).PubKey.GetAddress(ScriptPubKeyType.Segwit, Regtest).ToString();

    [Fact]
    public void AddressesMustBelongToTheCoinsNetwork()
    {
        Assert.Null(Wallets.CheckAddress("BTC", Receive(0)));
        var mainnet = Account.Derive(0).Derive(0).PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
        Assert.Contains("isn't a BTC address", Wallets.CheckAddress("BTC", mainnet));
        Assert.NotNull(Wallets.CheckAddress("BTC", "not an address"));
    }

    [Fact]
    public void FixedColdAddressIsUsedAsIs()
    {
        var address = Receive(3);
        Assert.Equal((address, false), Wallets.GetColdAddress("BTC", " " + address + " ", 7));
    }

    [Fact]
    public void ExtendedKeyGivesItsReceiveAddresses()
    {
        var key = Account.ToString(Regtest);
        Assert.Equal((Receive(0), true), Wallets.GetColdAddress("BTC", key, 0));
        Assert.Equal((Receive(5), true), Wallets.GetColdAddress("BTC", key, 5));
        Assert.Null(Wallets.CheckColdDestination("BTC", key));
    }

    [Fact]
    public void UnusableColdDestinationsAreRefused()
    {
        Assert.NotNull(Wallets.CheckColdDestination("BTC", "not a key"));
        Assert.Throws<SweepException>(() => Wallets.GetColdAddress("BTC", Account.ToString(Regtest), -1));
        Assert.Throws<SweepException>(() => Wallets.GetColdAddress("DOGE", Receive(0), 0)); // not on this server
    }

    [Theory]
    [InlineData(0.11, 0, 0.11, false)]
    [InlineData(0.11, 3, 3, true)]
    [InlineData(5, 3, 5, false)]
    [InlineData(3, 3, 3, false)]
    public void TheLowestFeeRateRaisesALowerEstimate(double estimate, double minimum, double paid, bool raised)
    {
        var (rate, wasRaised) = BtcPayStoreWallets.ChooseFeeRate(new FeeRate((decimal)estimate), (decimal)minimum);
        Assert.Equal((decimal)paid, rate.SatoshiPerByte);
        Assert.Equal(raised, wasRaised);
    }
}
