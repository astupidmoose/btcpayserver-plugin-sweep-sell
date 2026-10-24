using BTCPayServer.Events;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Hedging;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class HedgeServiceTests
{
    // Each test uses its own store ID: run locks are static, like the plugin's single instance.
    readonly string _storeId = "hedge-store-" + Guid.NewGuid().ToString("N");
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());
    static readonly DateTimeOffset Start = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    readonly MemoryRepository _repository = new();
    readonly FakeExchange _exchange = new() { Price = new MarketPrice(100_000m, 100_000m, 100_000m) };
    readonly FakeInvoices _invoices = new();
    readonly ManualClock _clock = new(Start);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    HedgeService Service() => new(_repository, Protector, new ExchangeRegistry([_exchange]), _invoices, _clock, NullLogger<HedgeService>.Instance);

    HedgeSettings Configure(bool dryRun = false, Action<HedgeSettings>? hedge = null)
    {
        var settings = new HedgeSettings { Enabled = true, DryRun = dryRun, EnabledSince = Start.AddDays(-1) };
        hedge?.Invoke(settings);
        _repository.Settings[_storeId] = new SweepSellSettings
        {
            ExchangeId = FakeExchange.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-api-key"),
            ProtectedApiSecret = Protector.Protect("test-api-secret"),
            Hedge = settings
        };
        _exchange.Balances["BTC"] = 10m;
        return settings;
    }

    HedgeState State => _repository.HedgeStates[_storeId];

    HedgeInvoice Invoice(string id, decimal btcPaid, HedgeInvoiceStatus status = HedgeInvoiceStatus.Settled, string currency = "CAD",
        DateTimeOffset? created = null, decimal? btcValue = null)
    {
        var invoice = new HedgeInvoice(id, _storeId, status, currency, created ?? Start.AddHours(-1), btcPaid, btcValue);
        _invoices.Invoices[id] = invoice;
        return invoice;
    }

    [Fact]
    public async Task PaidInvoiceSellsItsBtcStraightAway()
    {
        Configure();
        Invoice("inv-1", 0.123456789m);
        await Service().OnInvoiceAsync("inv-1", Ct);

        var sale = Assert.Single(_exchange.Orders);
        Assert.Equal(("BTC", "CAD", 0.12345678m, false, 98_000m), (sale.Base, sale.Quote, sale.Amount, sale.ValidateOnly, sale.Limit));
        Assert.Equal(0m, State.Invoices["inv-1"].Remaining);
        Assert.Equal(0.12345678m, State.Sold.On(_clock.GetUtcNow(), "BTC"));
        Assert.Equal(ActivityOutcome.Sold, State.Activity[0].Outcome);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task EachInvoiceIsHedgedOnce()
    {
        Configure();
        Invoice("inv-1", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        await service.OnInvoiceAsync("inv-1", Ct);
        _clock.Advance(HedgeService.ScanInterval);
        await service.RunDueAsync(Ct); // the rescan finds it again
        Assert.Single(_exchange.Orders);
    }

    [Theory]
    [InlineData(HedgeTrigger.Processing, HedgeInvoiceStatus.Processing, true)]
    [InlineData(HedgeTrigger.Processing, HedgeInvoiceStatus.Settled, true)]
    [InlineData(HedgeTrigger.Settled, HedgeInvoiceStatus.Processing, false)]
    [InlineData(HedgeTrigger.Settled, HedgeInvoiceStatus.Settled, true)]
    [InlineData(HedgeTrigger.Processing, HedgeInvoiceStatus.Other, false)]
    public async Task TheTriggerStatusDecides(HedgeTrigger trigger, HedgeInvoiceStatus status, bool hedged)
    {
        Configure(hedge: h => h.Trigger = trigger);
        Invoice("inv-1", 0.1m, status);
        await Service().OnInvoiceAsync("inv-1", Ct);
        Assert.Equal(hedged, _exchange.Orders.Count == 1);
    }

    [Fact]
    public async Task ProcessingInvoiceIsHedgedOnceItSettles()
    {
        Configure();
        Invoice("inv-1", 0.1m, HedgeInvoiceStatus.Processing);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.Empty(_exchange.Orders);
        Invoice("inv-1", 0.1m);
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task OldInvoicesAreNeverHedged()
    {
        Configure(hedge: h => h.EnabledSince = Start.AddDays(-60));
        Invoice("before-enabling", 0.1m, created: Start.AddDays(-61));
        Invoice("too-old", 0.1m, created: Start - HedgeService.MaxInvoiceAge - TimeSpan.FromMinutes(1));
        var service = Service();
        await service.OnInvoiceAsync("before-enabling", Ct);
        await service.OnInvoiceAsync("too-old", Ct);
        Assert.Empty(_exchange.Orders);

        Configure(hedge: h => h.EnabledSince = Start);
        Invoice("created-before-turning-on", 0.1m, created: Start.AddMinutes(-1));
        await service.OnInvoiceAsync("created-before-turning-on", Ct);
        Assert.Empty(_exchange.Orders);
    }

    [Fact]
    public async Task SmallInvoicesAddUpToTheMinimumOrder()
    {
        Configure();
        Invoice("inv-1", 0.03m);
        Invoice("inv-2", 0.03m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Contains("below Fake Exchange's minimum order of 0.05 BTC", State.Activity[0].Message);

        await service.OnInvoiceAsync("inv-2", Ct);
        Assert.Equal(0.06m, Assert.Single(_exchange.Orders).Amount);
        Assert.All(State.Invoices.Values, i => Assert.Equal(0m, i.Remaining));
    }

    [Fact]
    public async Task HedgesNeverSellMoreBtcThanTheExchangeHolds()
    {
        Configure();
        _exchange.Balances["BTC"] = 0.07m;
        Invoice("inv-1", 0.06m);
        Invoice("inv-2", 0.06m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        _exchange.Balances["BTC"] = 0.01m; // what the first sell left
        await service.OnInvoiceAsync("inv-2", Ct);
        Assert.Equal([0.06m], _exchange.Orders.Select(o => o.Amount));
        Assert.Equal(0.06m, State.Invoices["inv-2"].Remaining);
        Assert.Contains("only has 0.01 BTC available", State.Activity[0].Message);
        Assert.Contains("never borrow", State.Activity[0].Message);

        _exchange.Balances["BTC"] = 0.08m; // e.g. a sweep arrived; still less than everything queued is fine
        await service.RunDueAsync(Ct);
        Assert.Equal([0.06m, 0.06m], _exchange.Orders.Select(o => o.Amount));
    }

    [Fact]
    public async Task PartOfTheQueueSellsWhenTheExchangeHoldsLess()
    {
        Configure();
        _exchange.Balances["BTC"] = 0.07m;
        Invoice("inv-1", 0.06m, created: Start.AddHours(-2));
        Invoice("inv-2", 0.06m, created: Start.AddHours(-1));
        await Service().RunAsync(_storeId, Ct);
        Assert.Equal(0.07m, Assert.Single(_exchange.Orders).Amount);
        Assert.Equal(0.05m, State.Invoices.Values.Sum(i => i.Remaining));
    }

    [Fact]
    public async Task InvoiceCurrenciesSellIntoTheirMarkets()
    {
        Configure(hedge: h =>
        {
            h.Markets["GBP"] = "CAD";
            h.DefaultMarket = "USD";
        });
        Invoice("gbp", 0.1m, currency: "GBP");
        Invoice("eur", 0.1m, currency: "EUR");
        var service = Service();
        await service.OnInvoiceAsync("gbp", Ct);
        await service.OnInvoiceAsync("eur", Ct);
        Assert.Equal(["CAD", "USD"], _exchange.Orders.Select(o => o.Quote));
    }

    [Fact]
    public async Task QueuedInvoicesFollowAChangedMarket()
    {
        var hedge = Configure(hedge: h => h.Markets["GBP"] = "EUR");
        _exchange.Balances["BTC"] = 0m;
        Invoice("gbp", 0.1m, currency: "GBP");
        Invoice("usd", 0.1m, currency: "USD");
        var service = Service();
        await service.OnInvoiceAsync("gbp", Ct); // waits for BTC on the exchange
        await service.OnInvoiceAsync("usd", Ct);
        Assert.Equal("EUR", State.Invoices["gbp"].Market);

        hedge.Markets["GBP"] = "CAD"; // the owner corrects the mapping before BTC arrives
        hedge.Markets["USD"] = "BTC"; // and leaves USD invoices without a market
        _exchange.Balances["BTC"] = 1m;
        await service.RunAsync(_storeId, Ct);
        Assert.Equal("CAD", Assert.Single(_exchange.Orders).Quote);
        Assert.Equal(0m, State.Invoices["gbp"].Remaining);
        Assert.Equal(0m, State.Invoices["usd"].Remaining);
        Assert.Contains(State.Activity, a => a.Message.Contains("now sells for CAD instead of EUR"));
        Assert.Contains(State.Activity, a => a.Message.Contains("USD invoices have no market now"));
    }

    [Fact]
    public async Task ConfirmedOrderIdsAreSavedBeforeTheFillIsLookedUp()
    {
        Configure();
        Invoice("inv-1", 0.1m);
        IReadOnlyList<string>? saved = null;
        _exchange.OnFind = _ => saved = _repository.HedgeStates[_storeId].Pending.Single().OrderIds;
        await Service().OnInvoiceAsync("inv-1", Ct);
        Assert.Equal(["OAAAAA-BBBBB-CCCCCC"], saved); // a restart here would still know the order exists
    }

    [Fact]
    public async Task BtcInvoicesNeedAMarket()
    {
        Configure();
        Invoice("btc", 0.1m, currency: "BTC");
        var service = Service();
        await service.OnInvoiceAsync("btc", Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Contains("priced in BTC and no market is set", State.Activity[0].Message);
        Assert.Equal(0m, State.Invoices["btc"].Remaining); // decided once: never sold later
    }

    [Fact]
    public async Task InvoiceValueModeUsesTheInvoiceRate()
    {
        Configure(hedge: h => h.Amount = HedgeAmount.InvoiceValue);
        Invoice("paid-in-ltc", 0m, btcValue: 0.08m);
        Invoice("no-rate", 0.1m, btcValue: null);
        var service = Service();
        await service.OnInvoiceAsync("paid-in-ltc", Ct);
        await service.OnInvoiceAsync("no-rate", Ct);
        Assert.Equal(0.08m, Assert.Single(_exchange.Orders).Amount);
        Assert.Contains("has no BTC rate", State.Activity[0].Message);
    }

    [Fact]
    public async Task InvoicesPaidInOtherCoinsAreNotHedgedByDefault()
    {
        Configure();
        Invoice("paid-in-ltc", 0m, btcValue: 0.08m);
        await Service().OnInvoiceAsync("paid-in-ltc", Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Equal(0m, State.Invoices["paid-in-ltc"].Amount);
    }

    [Fact]
    public async Task PartialFillsLeaveTheRestQueuedOldestFirst()
    {
        Configure();
        _exchange.FillRatio = 0.5m;
        Invoice("inv-1", 0.06m, created: Start.AddHours(-2));
        Invoice("inv-2", 0.06m, created: Start.AddHours(-1));
        _clock.Advance(TimeSpan.Zero);
        await Service().RunAsync(_storeId, Ct); // the scan takes both
        Assert.Equal(0.12m, Assert.Single(_exchange.Orders).Amount);
        Assert.Equal(0m, State.Invoices["inv-1"].Remaining); // the older invoice is hedged first
        Assert.Equal(0.06m, State.Invoices["inv-2"].Remaining);
        Assert.Equal(0.06m, State.Sold.On(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task LostResponseIsLookedUpBeforeSellingAgain()
    {
        Configure();
        _exchange.OnSell = () => throw new HttpRequestException("connection reset");
        Invoice("inv-1", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(0.1m, State.Invoices["inv-1"].Remaining);

        _exchange.OnSell = null;
        _exchange.Found[pending.ClientOrderId] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: false, ExecutedAmount: 0.1m);
        Invoice("inv-2", 0.1m);
        await service.OnInvoiceAsync("inv-2", Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(0m, State.Invoices["inv-1"].Remaining);
        Assert.Equal(2, _exchange.Orders.Count); // the lost one, then inv-2 only
        Assert.Equal(0.1m, _exchange.Orders[1].Amount);
    }

    [Fact]
    public async Task DryRunChecksButNeverSellsLater()
    {
        Configure(dryRun: true);
        Invoice("inv-1", 0.1m);
        Invoice("small", 0.01m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.True(Assert.Single(_exchange.Orders).ValidateOnly);
        Assert.Equal(ActivityOutcome.DryRun, State.Activity[0].Outcome);
        await service.OnInvoiceAsync("small", Ct); // below the minimum: waits, still in dry run
        Assert.Equal(0.01m, State.Invoices["small"].Remaining);

        Configure(dryRun: false);
        await service.RunAsync(_storeId, Ct);
        Assert.DoesNotContain(_exchange.Orders, o => !o.ValidateOnly);
        Assert.Equal(0m, State.Invoices["small"].Remaining);
        Assert.Contains("taken while hedging was in dry run", State.Activity[0].Message);
        Assert.Equal(0m, State.Sold.On(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task OlderInvoicesAreHedgedFirstWhateverOrderTheyArriveIn()
    {
        Configure();
        _exchange.FillRatio = 0.5m;
        Invoice("newer", 0.06m, created: Start.AddMinutes(-10));
        Invoice("older", 0.06m, created: Start.AddMinutes(-20));
        var service = Service();
        _exchange.Balances["BTC"] = 0.01m; // both wait for BTC
        await service.OnInvoiceAsync("newer", Ct);
        await service.OnInvoiceAsync("older", Ct);
        _exchange.Balances["BTC"] = 1m;
        await service.RunAsync(_storeId, Ct);
        Assert.Equal(0m, State.Invoices["older"].Remaining);
        Assert.Equal(0.06m, State.Invoices["newer"].Remaining);
    }

    [Fact]
    public async Task TurningHedgingOffAndOnDropsWhatWasLeft()
    {
        var hedge = Configure();
        _exchange.Balances["BTC"] = 0m;
        Invoice("inv-1", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.Equal(0.1m, State.Invoices["inv-1"].Remaining);

        hedge.EnabledSince = Start; // turned off and on again
        _exchange.Balances["BTC"] = 1m;
        await service.RunAsync(_storeId, Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Equal(0m, State.Invoices["inv-1"].Remaining);
        Assert.Contains("before hedging was last turned on", State.Activity[0].Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderSentBeforeTurningHedgingOffAndOnNeverHedgesNewInvoices(bool removedByHand)
    {
        var hedge = Configure();
        _exchange.OnSell = () => throw new HttpRequestException("connection reset");
        Invoice("inv-1", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(hedge.EnabledSince, pending.HedgeEnabledSince);

        hedge.EnabledSince = Start; // turned off and on again while the order was unresolved
        _exchange.OnSell = null;
        _clock.Advance(TimeSpan.FromHours(1));
        Invoice("inv-2", 0.1m, created: Start.AddMinutes(30));
        if (removedByHand)
            Assert.True(await service.RemovePendingAsync(_storeId, pending.ClientOrderId, Ct));
        else
            _exchange.Found[pending.ClientOrderId] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: false, ExecutedAmount: 0.1m);
        await service.OnInvoiceAsync("inv-2", Ct);

        Assert.Empty(State.Pending);
        Assert.Equal(0.1m, _exchange.Orders[^1].Amount); // inv-2 is sold for itself
        Assert.Equal(0m, State.Invoices["inv-2"].Remaining);
        Assert.Equal(0.2m, State.Sold.On(_clock.GetUtcNow(), "BTC")); // the old fill still counts against the cap
    }

    [Fact]
    public async Task ImpossibleFillsAreNeverCounted()
    {
        Configure();
        _exchange.FillRatio = 2m; // reports more sold than ordered
        Invoice("inv-1", 0.1m);
        Invoice("inv-2", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal(0.1m, State.Invoices["inv-1"].Remaining);
        Assert.Contains("can't be right", State.Activity[0].Message);

        // Looked up again on the next run: still impossible, still pending, and nothing more is sold.
        await service.OnInvoiceAsync("inv-2", Ct);
        Assert.Single(_exchange.Orders);
        Assert.Equal(pending.ClientOrderId, Assert.Single(State.Pending).ClientOrderId);
        Assert.Equal(0m, State.Sold.On(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task DryRunAppliesTheCapAcrossMarkets()
    {
        Configure(dryRun: true, hedge: h =>
        {
            h.DailyCap = 0.1m;
            h.Markets["USD"] = "USD";
        });
        Invoice("cad", 0.1m);
        Invoice("usd", 0.1m, currency: "USD");
        await Service().RunAsync(_storeId, Ct);
        Assert.Equal([("CAD", 0.1m)], _exchange.Orders.Select(o => (o.Quote, o.Amount)));
        Assert.Contains("today's cap", State.Activity[0].Message);
    }

    [Fact]
    public async Task DailyCapHoldsTheRestForTomorrow()
    {
        Configure(hedge: h => h.DailyCap = 0.1m);
        Invoice("inv-1", 0.08m);
        Invoice("inv-2", 0.08m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        await service.OnInvoiceAsync("inv-2", Ct);
        Assert.Equal([0.08m], _exchange.Orders.Select(o => o.Amount)); // 0.02 left today, below the minimum
        Assert.Contains("today's cap of 0.1 BTC", State.Activity[0].Message);

        _clock.Advance(TimeSpan.FromDays(1));
        await service.RunAsync(_storeId, Ct);
        Assert.Equal([0.08m, 0.08m], _exchange.Orders.Select(o => o.Amount));
    }

    [Fact]
    public async Task BidBelowTheFloorWaits()
    {
        Configure();
        _exchange.Price = new MarketPrice(Last: 90_000m, Bid: 97_000m, DailyAverage: 100_000m);
        Invoice("inv-1", 0.1m);
        await Service().OnInvoiceAsync("inv-1", Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Contains("best bid", State.Activity[0].Message);
        Assert.Equal(0.1m, State.Invoices["inv-1"].Remaining);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("dry run")]
    [InlineData("market")]
    [InlineData("key")]
    [InlineData("turned off and on")]
    public async Task SettingsChangedDuringTheRunStopTheSell(string change)
    {
        Configure();
        _exchange.OnPrice = () =>
        {
            var settings = _repository.Settings[_storeId];
            switch (change)
            {
                case "pause": settings.Paused = true; break;
                case "dry run": settings.Hedge.DryRun = true; break;
                case "market": settings.Hedge.Markets["CAD"] = "USD"; break;
                case "turned off and on": settings.Hedge.EnabledSince = Start; break;
                default: settings.ProtectedApiSecret = Protector.Protect("another-secret"); break;
            }
        };
        Invoice("inv-1", 0.1m);
        await Service().OnInvoiceAsync("inv-1", Ct);
        Assert.Empty(_exchange.Orders);
        Assert.Empty(State.Pending);
        Assert.Contains("settings changed during the run", State.Activity[0].Message);
    }

    [Fact]
    public async Task RemovedPendingOrderCountsAsSold()
    {
        Configure();
        _exchange.OnSell = () => throw new HttpRequestException("timeout");
        Invoice("inv-1", 0.1m);
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        var pending = Assert.Single(State.Pending);
        _clock.Advance(TimeSpan.FromDays(1)); // removed the next day: counts today

        Assert.True(await service.RemovePendingAsync(_storeId, pending.ClientOrderId, Ct));
        Assert.False(await service.RemovePendingAsync(_storeId, pending.ClientOrderId, Ct));
        Assert.Equal(0m, State.Invoices["inv-1"].Remaining);
        Assert.Equal(0.1m, State.Sold.On(_clock.GetUtcNow(), "BTC"));
    }

    [Fact]
    public async Task MissedInvoicesAreFoundByTheScan()
    {
        Configure();
        Invoice("missed", 0.1m);
        var service = Service();
        await service.RunDueAsync(Ct);
        Assert.Single(_exchange.Orders);
        Assert.Equal(1, _invoices.Scans);

        await service.RunDueAsync(Ct); // nothing queued and the scan isn't due: the store isn't run
        Assert.Equal(1, _invoices.Scans);
        _clock.Advance(HedgeService.ScanInterval);
        await service.RunDueAsync(Ct);
        Assert.Equal(2, _invoices.Scans);
    }

    [Fact]
    public async Task HedgeNowAlwaysLooksForPaidInvoices()
    {
        Configure();
        var service = Service();
        await service.RunDueAsync(Ct); // a scan, finding nothing
        Assert.Equal(1, _invoices.Scans);
        Invoice("missed", 0.1m); // its event was missed, and the next scheduled scan isn't due yet
        await service.RunAsync(_storeId, Ct);
        Assert.Equal(2, _invoices.Scans);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task ScanFailuresAreReported()
    {
        Configure();
        _invoices.ScanError = new InvalidOperationException("database unavailable");
        await Service().RunAsync(_storeId, Ct);
        Assert.Contains("Could not list the store's paid invoices", State.Activity[0].Message);
        Assert.Null(State.LastScan);
    }

    [Fact]
    public async Task PausedOrDisabledStoresDoNothing()
    {
        Configure();
        Invoice("inv-1", 0.1m);
        _repository.Settings[_storeId].Paused = true;
        var service = Service();
        await service.OnInvoiceAsync("inv-1", Ct);
        Assert.Equal(HedgeRunResult.Paused, await service.RunAsync(_storeId, Ct));
        _repository.Settings[_storeId].Paused = false;
        _repository.Settings[_storeId].Hedge.Enabled = false;
        Assert.Equal(HedgeRunResult.Disabled, await service.RunAsync(_storeId, Ct));
        Assert.Empty(_exchange.Orders);
    }

    [Fact]
    public async Task NoKeyIsReported()
    {
        Configure();
        _repository.Settings[_storeId].ProtectedApiSecret = null;
        Invoice("inv-1", 0.1m);
        await Service().OnInvoiceAsync("inv-1", Ct);
        Assert.Contains("No exchange key", State.Activity[0].Message);
        Assert.Equal(0.1m, State.Invoices["inv-1"].Remaining); // sold once a key is back
    }

    [Fact]
    public void HedgedInvoicesAreForgottenAfterAWhile()
    {
        var state = new HedgeState();
        state.Invoices["done"] = new HedgedInvoice { Amount = 1, Remaining = 0, AddedAt = Start };
        state.Invoices["queued"] = new HedgedInvoice { Amount = 1, Remaining = 1, AddedAt = Start };
        state.Prune(Start.AddDays(1));
        Assert.Equal(["queued"], state.Invoices.Keys);
        Assert.True(HedgeState.KeepHedged > HedgeService.MaxInvoiceAge); // forgotten only once too old to hedge again
    }

    [Theory]
    [InlineData(InvoiceEventCode.PaidInFull, true)]
    [InlineData(InvoiceEventCode.Confirmed, true)]
    [InlineData(InvoiceEventCode.Completed, true)]
    [InlineData(InvoiceEventCode.MarkedCompleted, true)]
    [InlineData(InvoiceEventCode.PaidAfterExpiration, true)]
    [InlineData(InvoiceEventCode.Created, false)]
    [InlineData(InvoiceEventCode.Expired, false)]
    [InlineData(InvoiceEventCode.MarkedInvalid, false)]
    public void InvoiceEventsThatCanTriggerAHedge(InvoiceEventCode code, bool triggers) =>
        Assert.Equal(triggers, HedgeInvoiceListener.Triggers(code));
}
