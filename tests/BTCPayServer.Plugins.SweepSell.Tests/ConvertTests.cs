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
    public async Task ConfirmedOrderIdsAreSavedBeforeTheFillIsLookedUp()
    {
        Configure(assets: "DOGE");
        _exchange.Balances["DOGE"] = 100m;
        IReadOnlyList<string>? saved = null;
        _exchange.OnFind = _ => saved = _repository.States[StoreId].Pending.Single().OrderIds;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(["OAAAAA-BBBBB-CCCCCC"], saved); // a restart here would still know the order exists
    }

    [Fact]
    public async Task OrdersAreImmediateLimitsAtTheFloorBelowTheDailyAverage()
    {
        Configure(); // 2% default
        _exchange.Price = new MarketPrice(Last: 0.0005m, Bid: 0.00099m, DailyAverage: 0.001m); // last trade dragged down
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        var sale = Assert.Single(_exchange.Orders);
        Assert.Equal(0.00098m, sale.Limit); // 2% below the 24-hour average, not the last trade
        Assert.Contains("0.00098 or better", State.Activity[0].Message);
    }

    [Fact]
    public async Task BidBelowTheFloorWaits()
    {
        Configure();
        _exchange.Price = new MarketPrice(Last: 0.0009m, Bid: 0.00097m, DailyAverage: 0.001m);
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("best bid", State.Activity[0].Message);
    }

    [Fact]
    public async Task UnfilledOrderSellsNothingAndDoesNotCountAgainstTheCap()
    {
        Configure();
        _repository.Settings[StoreId].Convert.DailyCap = 1m;
        _exchange.FillRatio = 0m;
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("No buyer", State.Activity[0].Message);
        Assert.Equal(0m, State.SoldOn(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task PartialFillIsRecordedAndCounted()
    {
        Configure();
        _exchange.FillRatio = 0.5m;
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Contains("Sold 150 of 300 DOGE", State.Activity[0].Message);
        Assert.Equal(0.15m, State.SoldOn(_clock.GetUtcNow(), "BTC")); // 150 x 0.001
    }

    [Fact]
    public async Task DryRunAppliesTheCapAcrossAssets()
    {
        Configure(dryRun: true);
        _repository.Settings[StoreId].Convert.DailyCap = 0.1m; // 100 DOGE or 100 LTC at 0.001
        _exchange.Balances["DOGE"] = 100m;
        _exchange.Balances["LTC"] = 100m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal([("DOGE", 100m)], _exchange.Orders.Select(o => (o.Base, o.Amount)));
        Assert.Contains("Today's cap", State.Activity[0].Message);
    }

    [Fact]
    public async Task ADryRunThatCrossesMidnightGetsTheNewDaysCap()
    {
        Configure(dryRun: true);
        _repository.Settings[StoreId].Convert.DailyCap = 0.1m;
        _exchange.Balances["DOGE"] = 100m;
        _exchange.Balances["LTC"] = 100m;
        _clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59)); // 23:59 UTC
        var prices = 0;
        _exchange.OnPrice = () =>
        {
            if (++prices == 2)
                _clock.Advance(TimeSpan.FromMinutes(2)); // the second asset is checked after midnight
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal([("DOGE", 100m), ("LTC", 100m)], _exchange.Orders.Select(o => (o.Base, o.Amount)));
    }

    [Fact]
    public async Task ImpossibleFillsAreNeverCounted()
    {
        Configure();
        _exchange.FillRatio = -1m;
        _exchange.Balances["DOGE"] = 100m;
        var service = Service();
        await service.RunAsync(StoreId, Ct);
        Assert.Single(State.Pending);
        Assert.Contains("can't be right", State.Activity[0].Message);
        await service.RunAsync(StoreId, Ct); // the lookup on the next run gets the same answer
        Assert.Single(State.Pending);
        Assert.Single(_exchange.Orders);
        Assert.Equal(0m, State.SoldOn(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task DailyCapLimitsSalesUntilTheNextDay()
    {
        Configure();
        _repository.Settings[StoreId].Convert.DailyCap = 0.1m; // 100 DOGE at 0.001
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(100m, Assert.Single(_exchange.Orders).Amount);

        _exchange.Balances["DOGE"] = 200m;
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
        Assert.Contains("cap", State.Activity[0].Message);

        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(2, _exchange.Orders.Count);
        Assert.Equal(100m, _exchange.Orders[1].Amount);
    }

    [Theory]
    [InlineData(false)] // not listed yet
    [InlineData(true)] // listed, still open
    public async Task PlacedOrderStaysPendingUntilItCloses(bool listedOpen)
    {
        Configure();
        _repository.Settings[StoreId].Convert.DailyCap = 1m;
        _exchange.FillUnknown = true;
        _exchange.OnSell = () =>
        {
            if (listedOpen)
                _exchange.Found[_exchange.Orders[^1].Id] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: true, ExecutedAmount: 0m);
        };
        _exchange.Balances["DOGE"] = 300m;
        _exchange.Balances["LTC"] = 2m;
        await Service().RunAsync(StoreId, Ct);
        // LTC waits: the DOGE order's final fill must be counted against the cap first.
        Assert.Single(_exchange.Orders);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(["OAAAAA-BBBBB-CCCCCC"], pending.OrderIds);
        Assert.Contains("Waiting for Fake Exchange to report it closed", State.Activity[0].Message);

        // It closes; the next run counts the fill and moves on.
        _exchange.OnSell = null;
        _exchange.FillUnknown = false;
        _exchange.Found[pending.ClientOrderId] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: false, ExecutedAmount: 300m);
        _exchange.Balances.Remove("DOGE");
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(2, _exchange.Orders.Count);
        Assert.Equal("LTC", _exchange.Orders[1].Base);
    }

    [Fact]
    public async Task ConfirmedOrderIsNeverTreatedAsUnplaced()
    {
        Configure();
        var id = Guid.NewGuid();
        var pending = Pending(id);
        pending.OrderIds = ["OAAAAA-BBBBB-CCCCCC"];
        _repository.States[StoreId] = new ConvertState { Pending = [pending] };
        _exchange.Balances["DOGE"] = 300m;
        _clock.Advance(TimeSpan.FromHours(1)); // well past the grace period, and the exchange doesn't list it
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Single(State.Pending);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("was placed but", State.Activity[0].Message);
    }

    [Fact]
    public async Task CapIsCountedInTheAssetItWasSoldFor()
    {
        Configure(assets: "DOGE");
        _repository.Settings[StoreId].Convert.DailyCap = 0.1m;
        _exchange.Balances["DOGE"] = 100m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(0.1m, State.SoldOn(_clock.GetUtcNow(), "BTC"));

        // Switched to another target the same day: BTC sold isn't mistaken for the new asset, and BTC's total is kept.
        _repository.Settings[StoreId].Convert.TargetAsset = "ETH";
        _repository.Settings[StoreId].Convert.DailyCap = 2m;
        _exchange.Balances["DOGE"] = 100m;
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(0.1m, State.SoldOn(_clock.GetUtcNow(), "ETH"));
        Assert.Equal(0.1m, State.SoldOn(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task RemovingAPendingOrderByHandCountsItAgainstTheCap()
    {
        Configure(assets: "DOGE");
        _repository.Settings[StoreId].Convert.DailyCap = 0.4m;
        var id = Guid.NewGuid();
        var pending = Pending(id, apiKey: "earlier-api-key");
        pending.ReferencePrice = 0.001m;
        _repository.States[StoreId] = new ConvertState { Pending = [pending] };
        var service = Service();
        Assert.True(await service.RemovePendingAsync(StoreId, id, Ct));
        Assert.Equal(0.3m, State.SoldOn(_clock.GetUtcNow(), "BTC")); // 300 DOGE at 0.001, whatever it sold

        _exchange.Balances["DOGE"] = 300m;
        await service.RunAsync(StoreId, Ct);
        Assert.Equal(100m, Assert.Single(_exchange.Orders).Amount); // only what is left of the cap
    }

    [Fact]
    public async Task OneSlowStoreDoesNotHoldUpTheOthers()
    {
        Configure(assets: "DOGE");
        _repository.Settings["other-store"] = new SweepSellSettings
        {
            ExchangeId = FakeExchange.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-api-key"),
            ProtectedApiSecret = Protector.Protect("test-api-secret"),
            Convert = new ConvertSettings { Enabled = true, DryRun = false, Assets = ["LTC"], TargetAsset = "BTC", IntervalMinutes = 15 }
        };
        _exchange.Balances["DOGE"] = 300m;
        _exchange.Balances["LTC"] = 2m;
        var gate = new TaskCompletionSource();
        _exchange.SellGates["DOGE"] = gate.Task;
        var run = Service().RunDueAsync(Ct);
        try
        {
            while (!_repository.States.TryGetValue("other-store", out var other) || other.LastRun is null || other.Activity.Count == 0)
                await Task.Delay(10, Ct);
            Assert.False(run.IsCompleted);
            Assert.Contains(_repository.States["other-store"].Activity, a => a.Outcome == ActivityOutcome.Sold && a.Asset == "LTC");
        }
        finally
        {
            gate.TrySetResult(); // never leave the store's job holding its lock
        }
        await run;
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sold && a.Asset == "DOGE");
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
        Assert.Contains("300 DOGE", State.Activity[0].Message);
        Assert.Contains(id.ToString("D"), State.Activity[0].Message);
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
    public async Task CanceledOrExpiredPendingOrderIsNotASale()
    {
        Configure(assets: "DOGE");
        _repository.Settings[StoreId].Convert.DailyCap = 0.3m;
        var id = Guid.NewGuid();
        _repository.States[StoreId] = new ConvertState { Pending = [Pending(id)] };
        _exchange.Found[id] = new OrderLookup(["OCANCE-LLED00-ORDER1"], Open: false, ExecutedAmount: 0m);
        _exchange.Balances["DOGE"] = 300m;
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Service().RunAsync(StoreId, Ct);
        Assert.DoesNotContain(State.Activity, a => a.Message.Contains("OCANCE-LLED00-ORDER1") && a.Outcome == ActivityOutcome.Sold);
        Assert.Contains(State.Activity, a => a.Message.Contains("without selling anything"));
        // Nothing counted against the cap, and the coins are offered again straight away.
        var order = Assert.Single(_exchange.Orders);
        Assert.Equal(300m, order.Amount);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task PartlyFilledPendingOrderCountsWhatSold()
    {
        Configure(assets: "DOGE");
        var id = Guid.NewGuid();
        var pending = Pending(id);
        pending.ReferencePrice = 0.001m;
        _repository.States[StoreId] = new ConvertState { Pending = [pending] };
        _exchange.Found[id] = new OrderLookup(["OPARTI-ALFIL-LORDER"], Open: false, ExecutedAmount: 120m);
        await Service().RunAsync(StoreId, Ct);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sold && a.Message.Contains("120 of 300 DOGE"));
        Assert.Equal(0.12m, State.SoldOn(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task RefusedOrderIsClearedAndOtherCoinsStillSell()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.Balances["LTC"] = 2m;
        _exchange.OnSell = () =>
        {
            if (_exchange.Orders.Count == 1)
                throw new ExchangeException("Kraken: EOrder:Insufficient funds", rejected: true);
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Failed && a.Message.Contains("refused") && a.Message.Contains("Nothing was sold"));
        Assert.Equal(2, _exchange.Orders.Count);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sold && a.Asset == "LTC");
    }

    [Fact]
    public async Task ErrorWithAnUnknownOutcomeKeepsTheOrderPending()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.Balances["LTC"] = 2m;
        _exchange.OnSell = () => throw new ExchangeException("Kraken: EService:Unavailable");
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending);
        Assert.Single(_exchange.Orders); // stops selling until the order is looked up
    }

    [Fact]
    public async Task LookupFailureNamesTheAmount()
    {
        Configure();
        _repository.States[StoreId] = new ConvertState { Pending = [Pending(Guid.NewGuid())] };
        _exchange.FindError = new HttpRequestException("connection reset");
        await Service().RunAsync(StoreId, Ct);
        Assert.Contains("300 DOGE", State.Activity[0].Message);
        Assert.Single(State.Pending);
    }

    [Fact]
    public async Task StoreLockWaitsForARunningConvert()
    {
        Configure(assets: "DOGE");
        _exchange.Balances["DOGE"] = 300m;
        var gate = new TaskCompletionSource();
        _exchange.SellGates["DOGE"] = gate.Task;
        var service = Service();
        var run = service.RunAsync(StoreId, Ct);
        var pendingSeen = -1;
        Task<bool> locked;
        try
        {
            while (_exchange.Orders.Count == 0)
                await Task.Delay(10, Ct);
            locked = service.WithStoreLockAsync(StoreId, async () =>
            {
                pendingSeen = (await _repository.GetConvertStateAsync(StoreId)).Pending.Count;
                return true;
            }, Ct);
            await Task.Delay(50, Ct);
            Assert.False(locked.IsCompleted);
        }
        finally
        {
            gate.TrySetResult(); // never leave the store's job holding its lock
        }
        Assert.Equal(ConvertRunResult.Ran, await run);
        await locked;
        Assert.Equal(0, pendingSeen); // ran after the order was settled, never in between
    }

    [Fact]
    public async Task PauseSavedDuringARunStopsTheNextOrder()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnPrice = () => _repository.Settings[StoreId].Paused = true; // saved while the run reads the market
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Empty(State.Pending);
        Assert.Contains("settings changed during the run", State.Activity[0].Message);
    }

    [Fact]
    public async Task NewKeySavedDuringARunStopsTheNextOrder()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnPrice = () => _repository.Settings[StoreId].ProtectedApiKey = Protector.Protect("another-api-key");
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
    }

    [Fact]
    public async Task NewSecretForTheSameKeySavedDuringARunStopsTheNextOrder()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnPrice = () => _repository.Settings[StoreId].ProtectedApiSecret = Protector.Protect("replaced-api-secret");
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Orders);
    }

    [Fact]
    public async Task FillFoundAfterMidnightCountsOnTheDayItWasSent()
    {
        Configure(assets: "DOGE");
        _repository.Settings[StoreId].Convert.DailyCap = 0.3m;
        var id = Guid.NewGuid();
        var pending = Pending(id);
        pending.ReferencePrice = 0.001m;
        _repository.States[StoreId] = new ConvertState { Pending = [pending] };
        _exchange.Found[id] = new OrderLookup(["OLATE0-NIGHT-ORDER1"], Open: false, ExecutedAmount: 300m);
        _clock.Advance(TimeSpan.FromHours(24.5)); // found the next UTC day
        _exchange.Balances["DOGE"] = 300m;
        await Service().RunAsync(StoreId, Ct);
        // Yesterday's sale doesn't use today's cap: today's 300 DOGE still sells.
        Assert.Equal(300m, Assert.Single(_exchange.Orders).Amount);
        Assert.Equal(0.3m, State.SoldOn(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public void EarlierDaysSalesNeverCountAgainstALaterDay()
    {
        var state = new ConvertState();
        var day2 = new DateTimeOffset(2030, 1, 2, 1, 0, 0, TimeSpan.Zero);
        state.AddSold(day2, "BTC", 0.1m);
        state.AddSold(day2.AddHours(-2), "BTC", 5m); // sent the day before
        Assert.Equal(0.1m, state.SoldOn(day2, "BTC"));
    }

    [Fact]
    public async Task FailedFillLookupAfterPlacingIsRecorded()
    {
        Configure();
        _exchange.Balances["DOGE"] = 300m;
        _exchange.Balances["LTC"] = 2m;
        _exchange.OnSell = () => _exchange.FindError = new HttpRequestException("connection reset");
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Orders);
        Assert.Single(State.Pending);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("300 DOGE", State.Activity[0].Message);
        Assert.Contains("connection reset", State.Activity[0].Message);
    }

    [Fact]
    public async Task SettingsSaveWaitsOnlyForAnOrderBeingSent()
    {
        Configure(assets: "DOGE");
        _exchange.Balances["DOGE"] = 300m;
        var gate = new TaskCompletionSource();
        _exchange.SellGates["DOGE"] = gate.Task;
        var service = Service();
        var run = service.RunAsync(StoreId, Ct);
        Task<bool> save;
        try
        {
            while (_exchange.Orders.Count == 0)
                await Task.Delay(10, Ct);
            save = service.WithSettingsLockAsync(StoreId, () => Task.FromResult(true), Ct);
            await Task.Delay(50, Ct);
            Assert.False(save.IsCompleted);
        }
        finally
        {
            gate.TrySetResult(); // never leave the store's job holding its locks
        }
        Assert.True(await save);
        await run;
    }

    [Fact]
    public async Task ArithmeticOverflowIsRecordedForTheAsset()
    {
        Configure();
        _exchange.Balances["DOGE"] = decimal.MaxValue; // rounding to 8 decimals overflows
        _exchange.Balances["LTC"] = 2m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Contains(State.Activity, a => a.Asset == "DOGE" && a.Outcome == ActivityOutcome.Failed);
        Assert.Contains(State.Activity, a => a.Asset == "LTC" && a.Outcome == ActivityOutcome.Sold);
    }

    [Fact]
    public async Task MarketReadFailureNamesTheAmount()
    {
        Configure(assets: "DOGE");
        _exchange.Balances["DOGE"] = 300m;
        _exchange.OnPrice = () => throw new ExchangeException("Kraken: EService:Unavailable");
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("300 DOGE", State.Activity[0].Message);
    }

    [Fact]
    public async Task UnexpectedErrorsReachTheActivity()
    {
        Configure();
        _exchange.BalanceError = new InvalidOperationException("something odd");
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("something odd", State.Activity[0].Message);
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

    [Fact]
    public void SeveralCoinsWaitingDoNotPushOutHistory()
    {
        var state = new ConvertState();
        state.Add(new ActivityEntry { Asset = "DOGE", Outcome = ActivityOutcome.Sold, Message = "sold" });
        for (var i = 0; i < ConvertState.MaxActivity * 2; i++)
        {
            state.Add(new ActivityEntry { At = DateTimeOffset.UnixEpoch.AddMinutes(i), Asset = "LTC", Outcome = ActivityOutcome.Waiting, Message = "below minimum" });
            state.Add(new ActivityEntry { At = DateTimeOffset.UnixEpoch.AddMinutes(i), Asset = "XRP", Outcome = ActivityOutcome.Waiting, Message = "bid too low" });
        }
        Assert.Equal(3, state.Activity.Count);
        Assert.Equal(["XRP", "LTC", "DOGE"], state.Activity.Select(a => a.Asset));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(ConvertState.MaxActivity * 2 - 1), state.Activity[0].At);
    }
}
