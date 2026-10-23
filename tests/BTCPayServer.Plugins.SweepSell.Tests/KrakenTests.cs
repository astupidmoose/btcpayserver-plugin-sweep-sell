using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class KrakenTests
{
    // Made-up credentials; the secret only has to be valid base64.
    static readonly ExchangeCredentials Credentials = new("test-api-key", Convert.ToBase64String(new byte[64]));

    [Fact]
    public void SignatureMatchesKrakensDocumentedExample()
    {
        // The example from Kraken's REST authentication guide.
        var signature = KrakenSigner.Sign(
            "kQH5HW/8p1uGOVjbgWA7FunAmGO8lsSUXNsu3eow76sz84Q18fWxnyRzBHCd3pd5nE9qa99HAZtuZuj6F1huXg==",
            "/0/private/AddOrder",
            "1616492376594",
            "nonce=1616492376594&ordertype=limit&pair=XBTUSD&price=37500&type=buy&volume=1.25");
        Assert.Equal("4/dpxb3iT4tp/ZCVEwSnEsLxx0bqyhLpdfOpc6fn7OR8+UClSV5n9E6aSS8MPtnRfp32bAb0nmbRn6H8ndwLUQ==", signature);
    }

    [Fact]
    public void BadSecretGivesAReadableError()
    {
        var e = Assert.Throws<ExchangeException>(() => KrakenSigner.Sign("not base64!", "/0/private/Balance", "1", "nonce=1"));
        Assert.Contains("base64", e.Message);
    }

    [Theory]
    [InlineData("BTC", "CAD", "XBTCAD")]
    [InlineData("DOGE", "BTC", "XDGXBT")]
    [InlineData("ltc", "btc", "LTCXBT")]
    [InlineData("XMR", "BTC", "XMRXBT")]
    public void PairsUseKrakensTickers(string baseAsset, string quoteAsset, string pair) =>
        Assert.Equal(pair, KrakenConnector.Pair(baseAsset, quoteAsset));

    [Fact]
    public async Task BalancesAreAvailableAmountsInCommonTickers()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/BalanceEx"] = """
            {"error":[],"result":{
              "XXBT":{"balance":"0.5","hold_trade":"0.1"},
              "XXDG":{"balance":"250.0","hold_trade":"0"},
              "XLTC":{"balance":"0.0","hold_trade":"0"},
              "ZCAD":{"balance":"10.25","hold_trade":"0"},
              "XBT.F":{"balance":"1.0","hold_trade":"0"}}}
            """;
        var balances = await new KrakenConnector(http.Client()).GetAvailableBalancesAsync(Credentials, TestContext.Current.CancellationToken);
        Assert.Equal(0.4m, balances["BTC"]);
        Assert.Equal(250m, balances["DOGE"]);
        Assert.Equal(10.25m, balances["CAD"]);
        Assert.False(balances.ContainsKey("LTC")); // zero
        Assert.Equal(3, balances.Count); // the Earn balance (XBT.F) is not spendable
    }

    [Fact]
    public async Task PrivateCallsAreSignedAndNeverCarryTheSecret()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/BalanceEx"] = """{"error":[],"result":{}}""";
        await new KrakenConnector(http.Client()).GetAvailableBalancesAsync(Credentials, TestContext.Current.CancellationToken);
        var (request, body) = Assert.Single(http.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("test-api-key", request.Headers.GetValues("API-Key").Single());
        Assert.False(string.IsNullOrEmpty(request.Headers.GetValues("API-Sign").Single()));
        Assert.StartsWith("nonce=", body);
        Assert.DoesNotContain(Credentials.ApiSecret, body);
    }

    [Fact]
    public async Task FreshDepositAddressWhenTheMethodCanMakeOne()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/DepositMethods"] = """{"error":[],"result":[{"method":"Bitcoin","limit":false,"fee":"0","gen-address":true,"minimum":"0.0001"}]}""";
        http.Responses["/0/private/DepositAddresses"] = """{"error":[],"result":[{"address":"bc1qexampleaddress000000000000000000000000","expiretm":"0","new":true}]}""";
        var kraken = new KrakenConnector(http.Client());
        var address = await kraken.GetDepositAddressAsync(Credentials, "BTC", fresh: true, TestContext.Current.CancellationToken);
        Assert.Equal("bc1qexampleaddress000000000000000000000000", address);
        var body = http.Sent.Last().Body;
        Assert.Contains("asset=XBT", body);
        Assert.Contains("method=Bitcoin", body);
        Assert.Contains("new=true", body);
    }

    [Fact]
    public async Task ReusesTheAddressWhenTheMethodCannotMakeNewOnes()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/DepositMethods"] = """{"error":[],"result":[{"method":"Dogecoin","gen-address":false,"minimum":"50"}]}""";
        http.Responses["/0/private/DepositAddresses"] = """{"error":[],"result":[{"address":"DExampleAddress","expiretm":"0"}]}""";
        await new KrakenConnector(http.Client()).GetDepositAddressAsync(Credentials, "DOGE", fresh: true, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("new=true", http.Sent.Last().Body);
    }

    [Fact]
    public async Task MinimumDepositComesFromTheDepositMethod()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/DepositMethods"] = """{"error":[],"result":[{"method":"Litecoin","gen-address":true,"minimum":"0.02"}]}""";
        Assert.Equal(0.02m, await new KrakenConnector(http.Client()).GetMinimumDepositAsync(Credentials, "LTC", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PriceAndMarketRules()
    {
        var http = new FakeHttp();
        http.Responses["/0/public/Ticker"] = """{"error":[],"result":{"XXBTZCAD":{"c":["123456.7","0.01"]}}}""";
        http.Responses["/0/public/AssetPairs"] = """{"error":[],"result":{"XXBTZCAD":{"altname":"XBTCAD","ordermin":"0.00005","lot_decimals":8}}}""";
        var kraken = new KrakenConnector(http.Client());
        Assert.Equal(123456.7m, await kraken.GetPriceAsync("BTC", "CAD", TestContext.Current.CancellationToken));
        var rules = await kraken.GetMarketRulesAsync("BTC", "CAD", TestContext.Current.CancellationToken);
        Assert.Equal(0.00005m, rules.MinimumAmount);
        Assert.Equal(8, rules.AmountDecimals);
        Assert.Contains("pair=XBTCAD", http.Sent[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ValidateOnlySellIsNotPlaced()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/AddOrder"] = """{"error":[],"result":{"descr":{"order":"sell 0.01000000 XBTCAD @ market"}}}""";
        var result = await new KrakenConnector(http.Client()).SellAtMarketAsync(Credentials, "BTC", "CAD", 0.01m, validateOnly: true, TestContext.Current.CancellationToken);
        Assert.False(result.Placed);
        Assert.Empty(result.OrderIds);
        var body = http.Sent.Single().Body;
        Assert.Contains("validate=true", body);
        Assert.Contains("type=sell", body);
        Assert.Contains("ordertype=market", body);
        Assert.Contains("volume=0.01", body);
    }

    [Fact]
    public async Task PlacedSellReturnsTheOrderIds()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/AddOrder"] = """{"error":[],"result":{"descr":{"order":"sell 300 XDGXBT @ market"},"txid":["OEXAMP-LE000-ORDER1"]}}""";
        var result = await new KrakenConnector(http.Client()).SellAtMarketAsync(Credentials, "DOGE", "BTC", 300m, validateOnly: false, TestContext.Current.CancellationToken);
        Assert.True(result.Placed);
        Assert.Equal(["OEXAMP-LE000-ORDER1"], result.OrderIds);
        Assert.DoesNotContain("validate", http.Sent.Single().Body);
    }

    [Fact]
    public async Task KrakenErrorsBecomeExchangeExceptions()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/BalanceEx"] = """{"error":["EAPI:Invalid key"]}""";
        var e = await Assert.ThrowsAsync<ExchangeException>(() => new KrakenConnector(http.Client()).GetAvailableBalancesAsync(Credentials, TestContext.Current.CancellationToken));
        Assert.Equal("Kraken: EAPI:Invalid key", e.Message);
    }

    [Fact]
    public async Task TestReportsEachPermission()
    {
        var http = new FakeHttp();
        http.Responses["/0/private/BalanceEx"] = """{"error":[],"result":{}}""";
        http.Responses["/0/private/DepositMethods"] = """{"error":["EGeneral:Permission denied"]}""";
        http.Responses["/0/public/AssetPairs"] = """{"error":[],"result":{"XXBTZUSD":{"ordermin":"0.00005","lot_decimals":8}}}""";
        http.Responses["/0/private/AddOrder"] = """{"error":["EOrder:Insufficient funds"]}""";
        var check = await new KrakenConnector(http.Client()).TestCredentialsAsync(Credentials, TestContext.Current.CancellationToken);
        Assert.False(check.AllOk);
        Assert.True(check.Checks.Single(c => c.Permission == "Query Funds").Ok);
        Assert.False(check.Checks.Single(c => c.Permission == "Deposit Funds").Ok);
        Assert.True(check.Checks.Single(c => c.Permission == "Create & Modify Orders").Ok);
        Assert.All(http.Sent.Where(s => s.Request.RequestUri!.AbsolutePath == "/0/private/AddOrder"), s => Assert.Contains("validate=true", s.Body));
    }

    [Theory]
    [InlineData("0.123456789", 8, "0.0001", "0.12345678")]
    [InlineData("0.00009", 8, "0.0001", "0")]
    [InlineData("0", 8, "0.0001", "0")]
    [InlineData("150.9", 0, "100", "150")]
    public void SellableRoundsDownAndRespectsTheMinimum(string amount, int decimals, string minimum, string expected)
    {
        var rules = new MarketRules("X", "Y", decimal.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture), decimals);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            rules.Sellable(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void CredentialsNeverPrintTheirValues() =>
        Assert.DoesNotContain("test-api-key", Credentials.ToString());
}
