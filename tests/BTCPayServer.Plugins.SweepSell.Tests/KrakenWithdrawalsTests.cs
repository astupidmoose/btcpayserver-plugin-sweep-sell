using System.Reflection;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class KrakenWithdrawalsTests
{
    // Made-up credentials and destination names; the secret only has to be valid base64.
    static readonly ExchangeCredentials Credentials = new("test-payout-key", Convert.ToBase64String(new byte[64]));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static (FakeHttp Http, KrakenWithdrawals Kraken) Setup()
    {
        var http = new FakeHttp();
        http.Responses["/0/public/Assets"] = """{"error":[],"result":{"ZUSD":{"aclass":"currency","altname":"USD","decimals":4,"display_decimals":2}}}""";
        http.Responses["/0/private/WithdrawAddresses"] = """{"error":[],"result":[{"address":"example","asset":"ZUSD","method":"Bank Transfer","key":"test-bank","verified":true}]}""";
        http.Responses["/0/private/WithdrawInfo"] = """{"error":[],"result":{"method":"Bank Transfer","limit":"5000.00","amount":"122.45","fee":"1.00"}}""";
        http.Responses["/0/private/WithdrawMethods"] = """{"error":[],"result":[{"asset":"ZUSD","method":"Other Method","minimum":"500"},{"asset":"ZUSD","method":"Bank Transfer","minimum":"10.00"}]}""";
        return (http, new KrakenWithdrawals(new KrakenConnector(http.Client())));
    }

    static string Body(FakeHttp http, string path) => http.Sent.Single(s => s.Request.RequestUri!.AbsolutePath == path).Body;

    [Fact]
    public async Task QuoteRoundsDownAndReadsFeeLimitAndTheMethodsMinimum()
    {
        var (http, kraken) = Setup();
        var quote = await kraken.QuoteAsync(Credentials, "USD", "test-bank", 123.4567m, Ct);
        Assert.Equal(new WithdrawalQuote("test-bank", "Bank Transfer", 123.45m, 122.45m, 1.00m, 10.00m, 5000.00m), quote);
        var info = Body(http, "/0/private/WithdrawInfo");
        Assert.Contains("asset=USD", info);
        Assert.Contains("key=test-bank", info);
        Assert.Contains("amount=123.45", info);
        Assert.DoesNotContain("123.4567", info);
    }

    [Fact]
    public async Task UnverifiedDestinationIsRefusedBeforeAnyQuote()
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawAddresses"] = """{"error":[],"result":[{"address":"example","asset":"ZUSD","method":"Bank Transfer","key":"test-bank","verified":false}]}""";
        var e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.QuoteAsync(Credentials, "USD", "test-bank", 100m, Ct));
        Assert.True(e.Rejected);
        Assert.Contains("not verified", e.Message);
        Assert.DoesNotContain(http.Sent, s => s.Request.RequestUri!.AbsolutePath == "/0/private/WithdrawInfo");
    }

    [Fact]
    public async Task UnlistedDestinationIsLeftToKrakenToJudge()
    {
        // Kraken may not list bank accounts as withdrawal addresses; WithdrawInfo fails for a name the account doesn't have.
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawAddresses"] = """{"error":[],"result":[]}""";
        Assert.Equal(123.45m, (await kraken.QuoteAsync(Credentials, "USD", "test-bank", 123.45m, Ct)).Amount);
        http.Responses["/0/private/WithdrawInfo"] = """{"error":["EFunding:Unknown withdraw key"]}""";
        var e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.QuoteAsync(Credentials, "USD", "someone-else", 123.45m, Ct));
        Assert.Contains("Unknown withdraw key", e.Message);
    }

    [Theory]
    [InlineData("""{"error":[],"result":{"method":"Bank Transfer","limit":"5000","amount":"200","fee":"1"}}""")] // more arrives than leaves
    [InlineData("""{"error":[],"result":{"method":"Bank Transfer","limit":"5000","amount":"99"}}""")] // no fee
    [InlineData("""{"error":[],"result":{"limit":"5000","amount":"99","fee":"1"}}""")] // no method
    [InlineData("""{"error":[],"result":{"method":"Unknown Method","limit":"5000","amount":"99","fee":"1"}}""")] // minimum unknown
    public async Task ImpossibleOrIncompleteQuotesFail(string info)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawInfo"] = info;
        await Assert.ThrowsAsync<ExchangeException>(() => kraken.QuoteAsync(Credentials, "USD", "test-bank", 100m, Ct));
    }

    [Fact]
    public async Task AmountBelowTheAssetsPrecisionIsRefused()
    {
        var (_, kraken) = Setup();
        var e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.QuoteAsync(Credentials, "USD", "test-bank", 0.001m, Ct));
        Assert.True(e.Rejected);
    }

    [Fact]
    public async Task WithdrawNamesTheSavedDestinationAndCapsTheFee()
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/Withdraw"] = """{"error":[],"result":{"refid":"TEST-REF-1"}}""";
        Assert.Equal("TEST-REF-1", await kraken.WithdrawAsync(Credentials, "USD", "test-bank", 123.45m, 2.5m, Ct));
        var body = Body(http, "/0/private/Withdraw");
        Assert.Contains("asset=USD", body);
        Assert.Contains("key=test-bank", body);
        Assert.Contains("amount=123.45", body);
        Assert.Contains("max_fee=2.5", body);
        // A withdrawal only ever names a saved destination; it never carries an address of its own.
        Assert.DoesNotContain("address=", body);
    }

    [Theory]
    [InlineData("""{"error":[],"result":{}}""")]
    [InlineData("""{"error":[],"result":{"refid":""}}""")]
    [InlineData("""{"error":[],"result":{"refid":12}}""")]
    public async Task AcceptedWithoutAReferenceIsAnUnknownOutcome(string response)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/Withdraw"] = response;
        var e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.WithdrawAsync(Credentials, "USD", "test-bank", 100m, 1m, Ct));
        Assert.False(e.Rejected);
    }

    [Fact]
    public async Task RefusedWithdrawalIsRejected()
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/Withdraw"] = """{"error":["EFunding:Max fee exceeded"]}""";
        var e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.WithdrawAsync(Credentials, "USD", "test-bank", 100m, 1m, Ct));
        Assert.Contains("Max fee exceeded", e.Message);
        Assert.True(e.Rejected);
        http.Responses["/0/private/Withdraw"] = """{"error":["EService:Unavailable"]}""";
        e = await Assert.ThrowsAsync<ExchangeException>(() => kraken.WithdrawAsync(Credentials, "USD", "test-bank", 100m, 1m, Ct));
        Assert.False(e.Rejected); // may have gone through: looked up, never retried
    }

    [Fact]
    public async Task RecentWithdrawalsMapKrakensStatuses()
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawStatus"] = """
            {"error":[],"result":[
              {"asset":"ZUSD","refid":"R1","amount":"99.00","fee":"1.00","time":1893456000,"status":"Success","key":"test-bank"},
              {"asset":"ZUSD","refid":"R2","amount":"50","fee":"1","time":1893456100,"status":"Pending"},
              {"asset":"ZUSD","refid":"R3","amount":"50","fee":"1","time":1893456200,"status":"Failure"},
              {"asset":"ZUSD","refid":"R4","amount":"50","fee":"1","time":1893456300,"status":"Success","status-prop":"onhold"},
              {"asset":"ZUSD","refid":"R5","amount":"50","fee":"1","time":1893456400,"status":"Pending","status-prop":"canceled"},
              {"asset":"ZUSD","refid":"R6","amount":"50","fee":"1","time":1893456500,"status":"Success","status-prop":"return"},
              {"asset":"ZUSD","refid":"R7","amount":"50","time":1893456600,"status":"Settled"},
              {"asset":"XXBT","refid":"R8","amount":"1","fee":"0","time":1893456700,"status":"Pending"}]}
            """;
        var since = DateTimeOffset.FromUnixTimeSeconds(1893400000);
        var records = await kraken.GetRecentWithdrawalsAsync(Credentials, "USD", since, Ct);
        Assert.Equal(["R1", "R2", "R3", "R4", "R5", "R6", "R7"], records.Select(r => r.Reference));
        Assert.Equal(
            [WithdrawalStatus.Succeeded, WithdrawalStatus.InProgress, WithdrawalStatus.Failed, WithdrawalStatus.InProgress,
             WithdrawalStatus.Failed, WithdrawalStatus.Failed, WithdrawalStatus.InProgress],
            records.Select(r => r.Status));
        Assert.Equal(new WithdrawalRecord("R1", "test-bank", 99.00m, 1.00m, DateTimeOffset.FromUnixTimeSeconds(1893456000), WithdrawalStatus.Succeeded), records[0]);
        Assert.Equal(0m, records[6].Fee);
        var body = Body(http, "/0/private/WithdrawStatus");
        Assert.Contains("asset=USD", body);
        Assert.Contains("start=1893400000", body);
        Assert.Contains("cursor=true", body);
    }

    /// <summary>Kraken's paged WithdrawStatus: each page names the next, and an endless one never stops naming one.</summary>
    sealed class PagedWithdrawals(bool endless) : FakeHttp
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/0/private/WithdrawStatus")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var page = body.Contains("cursor=true") ? 1 : int.Parse(body.Split("cursor=page")[1].Split('&')[0]);
                var next = endless || page < 3 ? $"page{page + 1}" : "";
                Responses["/0/private/WithdrawStatus"] = $$$"""
                    {"error":[],"result":{"withdrawals":[{"asset":"ZUSD","refid":"R{{{page}}}","amount":"50","fee":"1","time":{{{1893456000 + page}}},"status":"Pending"}],"next_cursor":"{{{next}}}"}}
                    """;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task RecentWithdrawalsFollowEveryPage()
    {
        var http = new PagedWithdrawals(endless: false);
        var kraken = new KrakenWithdrawals(new KrakenConnector(http.Client()));
        var records = await kraken.GetRecentWithdrawalsAsync(Credentials, "USD", DateTimeOffset.UnixEpoch, Ct);
        Assert.Equal(["R1", "R2", "R3"], records.Select(r => r.Reference)); // an older withdrawal in progress is never missed

        var endless = new PagedWithdrawals(endless: true);
        var e = await Assert.ThrowsAsync<ExchangeException>(() =>
            new KrakenWithdrawals(new KrakenConnector(endless.Client())).GetRecentWithdrawalsAsync(Credentials, "USD", DateTimeOffset.UnixEpoch, Ct));
        Assert.Contains("can be accounted for", e.Message);
        Assert.Equal(KrakenWithdrawals.MaxWithdrawalPages, endless.Sent.Count(s => s.Request.RequestUri!.AbsolutePath == "/0/private/WithdrawStatus"));
    }

    [Theory]
    [InlineData("""{"error":[],"result":[{"asset":"ZUSD","amount":"1","time":1,"status":"Success"}]}""")] // no reference
    [InlineData("""{"error":[],"result":[{"asset":"ZUSD","refid":"R1","amount":"1","status":"Success"}]}""")] // no time
    [InlineData("""{"error":[],"result":[{"asset":"ZUSD","refid":"R1","time":1,"status":"Success"}]}""")] // no amount
    [InlineData("""{"error":[],"result":{}}""")]
    [InlineData("""{"error":[],"result":["R1"]}""")]
    public async Task MalformedWithdrawalListsFail(string response)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawStatus"] = response;
        await Assert.ThrowsAsync<ExchangeException>(() => kraken.GetRecentWithdrawalsAsync(Credentials, "USD", DateTimeOffset.UnixEpoch, Ct));
    }

    [Fact]
    public async Task DestinationsListedForTheAsset()
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/WithdrawAddresses"] = """
            {"error":[],"result":[
              {"address":"example","asset":"ZUSD","method":"Bank Transfer","key":"test-bank","verified":true},
              {"address":"example","asset":"ZUSD","method":"Bank Transfer","key":"test-new","verified":false},
              {"address":"example","asset":"XXBT","method":"Bitcoin","key":"test-btc","verified":true},
              {"address":"example","asset":"ZUSD","method":"Bank Transfer"}]}
            """;
        var destinations = await kraken.GetDestinationsAsync(Credentials, "USD", Ct);
        Assert.Equal([new WithdrawalDestination("test-bank", "Bank Transfer", true), new WithdrawalDestination("test-new", "Bank Transfer", false)], destinations);
    }

    [Theory]
    [InlineData("""{"error":["EGeneral:Permission denied"]}""", true)]
    [InlineData("""{"error":["EOrder:Insufficient funds"]}""", false)] // judged on its merits: the key may trade
    [InlineData("""{"error":[],"result":{"descr":{"order":"sell"}}}""", false)]
    public async Task PayoutKeyMustNotBeAbleToTrade(string addOrder, bool ok)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/BalanceEx"] = """{"error":[],"result":{}}""";
        http.Responses["/0/public/AssetPairs"] = """{"error":[],"result":{"XXBTZUSD":{"ordermin":"0.00005","costmin":"0.5","lot_decimals":8,"pair_decimals":1}}}""";
        http.Responses["/0/public/Ticker"] = """{"error":[],"result":{"XXBTZUSD":{"c":["100000.0","0.01"],"b":["99990.0","1","1.000"],"p":["100100.0","100050.0"]}}}""";
        http.Responses["/0/private/AddOrder"] = addOrder;
        http.Responses["/0/private/GetApiKeyInfo"] = """{"error":[],"result":{"api_key_name":"test","permissions":["query-funds","withdraw-funds"]}}""";
        var check = await kraken.TestCredentialsAsync(Credentials, "USD", Ct);
        Assert.Equal(["Query Funds", "Withdraw Funds", "No Create & Modify Orders", "No Other Permissions"], check.Checks.Select(c => c.Permission));
        Assert.Equal(ok, check.AllOk);
        Assert.Contains("validate=true", Body(http, "/0/private/AddOrder"));
        Assert.Contains("asset=USD", Body(http, "/0/private/WithdrawMethods"));
    }

    [Theory]
    [InlineData("""["query-funds","withdraw-funds"]""", 0)]
    [InlineData("""["withdraw-funds"]""", 0)]
    [InlineData("""["query-funds","withdraw-funds","add-withdraw-address"]""", 2)]
    [InlineData("""["withdraw-funds","update-withdraw-address","add-withdraw-address","modify-trades"]""", 4)]
    [InlineData("""["query-funds","withdraw-funds","close-trades"]""", 2)]
    [InlineData("""["query-funds","withdraw-funds","earn-funds","query-ledger","some-new-permission"]""", 4)]
    public async Task KeysWithAnyOtherPermissionAreRefused(string permissions, int problems)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/GetApiKeyInfo"] = $$$"""{"error":[],"result":{"api_key_name":"test","permissions":{{{permissions}}}}}""";
        var found = await kraken.GetKeyProblemsAsync(Credentials, Ct);
        Assert.Equal(problems, found.Count);
        if (permissions.Contains("add-withdraw-address"))
            Assert.Contains(found, p => p.Contains("add withdrawal addresses"));
        if (permissions.Contains("close-trades"))
            Assert.Contains(found, p => p.Contains("cancel or close orders"));
        if (permissions.Contains("some-new-permission"))
            Assert.Contains(found, p => p.Contains("some-new-permission"));
    }

    [Theory]
    [InlineData("""{"error":[],"result":{"api_key_name":"test"}}""")]
    [InlineData("""{"error":[],"result":{"permissions":[1]}}""")]
    [InlineData("""{"error":["EGeneral:Unknown method"]}""")]
    public async Task UnreadableKeyPermissionsFail(string response)
    {
        var (http, kraken) = Setup();
        http.Responses["/0/private/GetApiKeyInfo"] = response;
        await Assert.ThrowsAsync<ExchangeException>(() => kraken.GetKeyProblemsAsync(Credentials, Ct));
    }

    [Fact]
    public void NoConnectorCanAddOrChooseAnAddress()
    {
        // Payout only names destinations saved in the exchange account, and trading never withdraws at all.
        var withdrawalCalls = typeof(IWithdrawalConnector).GetMethods().Select(m => m.Name).Where(n => !n.StartsWith("get_")).Order();
        Assert.Equal(
            ["GetAvailableBalanceAsync", "GetDestinationsAsync", "GetKeyProblemsAsync", "GetRecentWithdrawalsAsync", "QuoteAsync", "TestCredentialsAsync", "WithdrawAsync"],
            withdrawalCalls);
        Assert.DoesNotContain(typeof(IWithdrawalConnector).GetMethods().SelectMany(m => m.GetParameters()), p => p.Name!.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IExchangeConnector).GetMethods(), m => m.Name.Contains("Withdraw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(KrakenConnector).GetMethods(BindingFlags.Public | BindingFlags.Instance), m => m.Name.Contains("Withdraw", StringComparison.OrdinalIgnoreCase));
    }
}
