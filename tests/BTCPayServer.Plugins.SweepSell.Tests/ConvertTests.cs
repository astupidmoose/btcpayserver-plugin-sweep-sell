using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class ConvertTests
{
    const string StoreId = "test-store";
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());

    readonly MemoryRepository _repository = new();
    readonly FakeExchange _exchange = new();
    readonly ManualClock _clock = new(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    ConvertService Service() =>
        new(_repository, Protector, new ExchangeRegistry([_exchange]), _clock, NullLogger<ConvertService>.Instance);

    void Configure(bool dryRun = false, bool paused = false, bool enabled = true, params string[] assets) =>
        _repository.Settings[StoreId] = new SweepSellSettings
        {
            Paused = paused,
            ExchangeId = FakeExchange.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-api-key"),
            ProtectedApiSecret = Protector.Protect("test-api-secret"),
            Convert = new ConvertSettings
            {
                Enabled = enabled,
                DryRun = dryRun,
                Assets = assets.Length == 0 ? ["DOGE", "LTC"] : [.. assets],
                TargetAsset = "BTC",
                IntervalMinutes = 15
            }
        };

    ConvertState State => _repository.States[StoreId];

    [Fact]
    public async Task SellsEverythingAboveTheMinimumRoundedDown()
    {
        Configure();
        _exchange.Balances["DOGE"] = 250.987654321m;
        _exchange.Balances["LTC"] = 0.01m; // below the 0.05 minimum
        Assert.Equal(ConvertRunResult.Ran, await Service().RunAsync(StoreId, Ct));

        var sale = Assert.Single(_exchange.Orders);
        Assert.Equal(("DOGE", "BTC", 250.98765432m, false), (sale.Base, sale.Quote, sale.Amount, sale.ValidateOnly));
        Assert.Empty(State.Pending);
        Assert.Contains(State.Activity, a => a.Asset == "DOGE" && a.Outcome == ActivityOutcome.Sold);
        Assert.Contains(State.Activity, a => a.Asset == "LTC" && a.Outcome == ActivityOutcome.Waiting && a.Message.Contains("minimum"));
    }

    [Fact]
    public async Task DryRunOnlyValidates()
    {
        Configure(dryRun: true);
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        Assert.True(Assert.Single(_exchange.Orders).ValidateOnly);
        Assert.Equal(ActivityOutcome.DryRun, State.Activity[0].Outcome);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task PausedOrDisabledDoesNothing()
    {
        _exchange.Balances["DOGE"] = 300m;
        Configure(paused: true);
        Assert.Equal(ConvertRunResult.Paused, await Service().RunAsync(StoreId, Ct));
        Configure(enabled: false);
        Assert.Equal(ConvertRunResult.Disabled, await Service().RunAsync(StoreId, Ct));
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task PendingOrderIsSavedBeforeSending()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnSell = () => Assert.Single(_repository.States[StoreId].Pending); // already saved when the order goes out
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task LostResponseIsLookedUpInsteadOfSellingAgain()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnSell = () => throw new HttpRequestException("connection reset");
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);

        // The order did go through: the next run finds it and sells nothing more.
        _exchange.OnSell = null;
        _exchange.Found[pending.ClientOrderId] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: false, ExecutedAmount: 300m);
        _exchange.Balances.Remove("DOGE");
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
        Assert.Empty(State.Pending);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sold && a.Message.Contains("OAAAAA-BBBBB-CCCCCC"));
    }

    [Fact]
    public async Task UnknownOrderBlocksSellingUntilTheGracePeriodEnds()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnSell = () => throw new TaskCanceledException("timeout");
        await Service().RunAsync(StoreId, Ct);
        _exchange.OnSell = null;

        // Not found yet, inside the grace period: wait, sell nothing.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
        Assert.Single(State.Pending);

        // Still not found after the grace period: it was never placed, so it is sold again.
        _clock.Advance(ConvertService.PendingGrace);
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(2, _exchange.Orders.Count);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task OpenOrderKeepsWaiting()
    {
        Configure();
        var id = Guid.NewGuid();
        _repository.States[StoreId] = new ConvertState
        {
            Pending = [Pending(id)]
        };
        _exchange.Found[id] = new OrderLookup(["ODDDDD-EEEEE-FFFFFF"], Open: true, ExecutedAmount: 0m);
        _exchange.Balances["LTC"] = 1m;
        _clock.Advance(TimeSpan.FromHours(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Single(State.Pending);
    }

    PendingOrder Pending(Guid id, string apiKey = "test-api-key") => new()
    {
        ClientOrderId = id,
        ExchangeId = FakeExchange.ExchangeId,
        KeyFingerprint = new ExchangeCredentials(apiKey, "unused").Fingerprint(),
        Asset = "DOGE",
        TargetAsset = "BTC",
        Amount = 300m,
        SentAt = _clock.GetUtcNow()
    };

    [Fact]
    public async Task PendingOrdersRememberTheirExchangeAndKey()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnSell = () => throw new HttpRequestException("connection reset");
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(FakeExchange.ExchangeId, pending.ExchangeId);
        Assert.Equal(new ExchangeCredentials("test-api-key", "unused").Fingerprint(), pending.KeyFingerprint);
        Assert.DoesNotContain("test-api-key", Newtonsoft.Json.JsonConvert.SerializeObject(State));
    }

    [Fact]
    public async Task PendingOrderFromAnotherKeyIsNeverLookedUpOrDropped()
    {
        Configure();
        var id = Guid.NewGuid();
        _repository.States[StoreId] = new ConvertState { Pending = [Pending(id, apiKey: "earlier-api-key")] };
        _exchange.Balances["DOGE"] = 300m;
        _clock.Advance(TimeSpan.FromHours(1)); // well past the grace period
        await Service().RunAsync(StoreId, Ct);
        Assert.DoesNotContain("find", _exchange.Calls);
        Assert.Empty(_exchange.Orders);
        Assert.Single(State.Pending);
        Assert.Contains("different exchange key", State.Activity[0].Message);
    }

    [Fact]
    public async Task RemovingAPendingOrderByHandResumesSelling()
    {
        Configure();
        var id = Guid.NewGuid();
        _repository.States[StoreId] = new ConvertState { Pending = [Pending(id, apiKey: "earlier-api-key")] };
        var service = Service();
        Assert.False(await service.RemovePendingAsync(StoreId, Guid.NewGuid(), Ct));
        Assert.True(await service.RemovePendingAsync(StoreId, id, Ct));
        Assert.Empty(State.Pending);
        Assert.Contains("removed by hand", State.Activity[0].Message);

        _exchange.Balances["DOGE"] = 300m;
        await service.RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task ExchangeErrorsAreRecordedNotThrown()
    {
        Configure();
        _exchange.BalanceError = new ExchangeException("Kraken: EAPI:Invalid key");
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("Invalid key", State.Activity[0].Message);
    }

    [Fact]
    public async Task MissingKeyIsRecorded()
    {
        Configure();
        _repository.Settings[StoreId].ProtectedApiKey = null;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Calls);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
    }

    [Fact]
    public async Task RunDueRespectsTheInterval()
    {
        Configure(dryRun: true);
        _exchange.Balances["DOGE"] = 300m;
        var service = Service();
        await service.RunDueAsync(Ct);
        await service.RunDueAsync(Ct); // too soon
        Assert.Single(_exchange.Orders);
        _clock.Advance(TimeSpan.FromMinutes(15));
        await service.RunDueAsync(Ct);
        Assert.Equal(2, _exchange.Orders.Count);
    }

    [Fact]
    public void RepeatedActivityRefreshesInsteadOfPiling()
    {
        var state = new ConvertState();
        for (var i = 0; i < 3; i++)
            state.Add(new ActivityEntry { At = DateTimeOffset.UnixEpoch.AddMinutes(i), Asset = "LTC", Outcome = ActivityOutcome.Waiting, Message = "below minimum" });
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(2), Assert.Single(state.Activity).At);
        for (var i = 0; i < ConvertState.MaxActivity + 10; i++)
            state.Add(new ActivityEntry { Asset = "DOGE", Outcome = ActivityOutcome.Sold, Message = $"sale {i}" });
        Assert.Equal(ConvertState.MaxActivity, state.Activity.Count);
        Assert.Equal($"sale {ConvertState.MaxActivity + 9}", state.Activity[0].Message);
    }

    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    sealed class MemoryRepository : ISweepSellRepository
    {
        public Dictionary<string, SweepSellSettings> Settings { get; } = new();
        public Dictionary<string, ConvertState> States { get; } = new();

        public Task<SweepSellSettings> GetAsync(string storeId) => Task.FromResult(Settings.GetValueOrDefault(storeId) ?? new SweepSellSettings());
        public Task SaveAsync(string storeId, SweepSellSettings settings) { Settings[storeId] = settings; return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, SweepSellSettings>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, SweepSellSettings>>(Settings);

        // Stored as copies, like a database, so a test sees only what was saved.
        public Task<ConvertState> GetConvertStateAsync(string storeId) =>
            Task.FromResult(States.TryGetValue(storeId, out var s) ? Copy(s) : new ConvertState());

        public Task SaveConvertStateAsync(string storeId, ConvertState state) { States[storeId] = Copy(state); return Task.CompletedTask; }

        static ConvertState Copy(ConvertState s) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<ConvertState>(Newtonsoft.Json.JsonConvert.SerializeObject(s))!;
    }

    /// <summary>An exchange with invented markets: every X/BTC pair has a 0.05 minimum, 8 decimals and a price of 0.001.</summary>
    sealed class FakeExchange : IExchangeConnector
    {
        public const string ExchangeId = "fake";
        public string Id => ExchangeId;
        public string Name => "Fake Exchange";
        public string KeyPermissionsHelp => "";

        public Dictionary<string, decimal> Balances { get; } = new();
        public Dictionary<Guid, OrderLookup> Found { get; } = new();
        public List<(string Base, string Quote, decimal Amount, bool ValidateOnly, Guid Id)> Orders { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? BalanceError { get; set; }
        public Action? OnSell { get; set; }

        public Task<IReadOnlyDictionary<string, decimal>> GetAvailableBalancesAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
        {
            Calls.Add("balances");
            if (BalanceError is not null)
                throw BalanceError;
            return Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>(Balances));
        }

        public Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken) =>
            Task.FromResult(new MarketRules(baseAsset, quoteAsset, 0.05m, 0m, 8));

        public Task<decimal> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken) => Task.FromResult(0.001m);

        public Task<OrderResult> SellAtMarketAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount,
            Guid clientOrderId, bool validateOnly, CancellationToken cancellationToken)
        {
            Calls.Add("sell");
            Orders.Add((baseAsset, quoteAsset, amount, validateOnly, clientOrderId));
            OnSell?.Invoke();
            return Task.FromResult(new OrderResult(!validateOnly, validateOnly ? [] : ["OAAAAA-BBBBB-CCCCCC"], $"sell {amount} {baseAsset}{quoteAsset} @ market"));
        }

        public Task<OrderLookup?> FindOrderAsync(ExchangeCredentials credentials, Guid clientOrderId, CancellationToken cancellationToken)
        {
            Calls.Add("find");
            return Task.FromResult(Found.GetValueOrDefault(clientOrderId));
        }

        public Task<DepositDestination> GetDepositAddressAsync(ExchangeCredentials credentials, string asset, bool fresh, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<decimal> GetMinimumDepositAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
