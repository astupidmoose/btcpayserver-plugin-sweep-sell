using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Payout;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class PayoutTests
{
    const string StoreId = "test-store";
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());
    static readonly DateTimeOffset Start = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    readonly MemoryRepository _main = new();
    readonly Payouts _payouts = new();
    readonly ManualClock _clock = new(Start);
    readonly FakeWithdrawals _exchange;
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PayoutTests() => _exchange = new FakeWithdrawals(_clock);

    PayoutService Service() =>
        new(_payouts, _main, Protector, new WithdrawalRegistry([_exchange]), _clock, NullLogger<PayoutService>.Instance);

    void Configure(bool dryRun = false, bool paused = false, bool enabled = true, decimal minimum = 50m, decimal keep = 0m, decimal cap = 1000m,
        decimal maxFee = 2m, string payoutKey = "test-payout-key")
    {
        _main.Settings[StoreId] = new SweepSellSettings
        {
            Paused = paused,
            ExchangeId = FakeWithdrawals.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-trading-key"),
            ProtectedApiSecret = Protector.Protect("test-trading-secret")
        };
        _payouts.Settings[StoreId] = new PayoutSettings
        {
            Enabled = enabled,
            DryRun = dryRun,
            ExchangeId = FakeWithdrawals.ExchangeId,
            ProtectedApiKey = Protector.Protect(payoutKey),
            ProtectedApiSecret = Protector.Protect("test-payout-secret"),
            Asset = "USD",
            Destination = "test-bank",
            Minimum = minimum,
            KeepBalance = keep,
            DailyCap = cap,
            MaxFee = maxFee,
            IntervalMinutes = 1440
        };
    }

    PayoutState State => _payouts.States[StoreId];

    [Fact]
    public async Task ATradingKeyThatCanChangeDestinationsStopsPayout()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.OtherKeyProblems.Add("This key can update withdrawal addresses, so a stolen copy could redirect funds.");
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Equal(["test-trading-key"], _exchange.OtherKeysChecked);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("trading key makes payouts unsafe", State.Activity[0].Message);
    }

    [Fact]
    public async Task AWithdrawalSentAfterMidnightCountsOnTheNewDay()
    {
        Configure(cap: 1000m);
        _exchange.Balance = 500m;
        _clock.Advance(TimeSpan.FromHours(12) - TimeSpan.FromSeconds(30)); // 23:59:30 UTC
        _payouts.States[StoreId] = new PayoutState();
        _payouts.States[StoreId].AddWithdrawn(_clock.GetUtcNow(), "USD", 900m); // 100 left today
        var reads = 0;
        _exchange.OnRecent = () =>
        {
            if (++reads == 2)
                _clock.Advance(TimeSpan.FromMinutes(1)); // planned before midnight, sent after it
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(100m, Assert.Single(_exchange.Withdrawals).Amount);
        Assert.Equal(100m, State.WithdrawnOn(_clock.GetUtcNow(), "USD")); // the next run sees the new day's cap partly used
    }

    [Fact]
    public async Task ABalanceThatDropsDuringTheRunStopsTheWithdrawal()
    {
        Configure(keep: 100m);
        _exchange.Balance = 500m;
        _exchange.OnQuote = () => _exchange.Balance = 400m; // a convert sell of the same asset meanwhile
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("balance changed during the run", State.Activity[0].Message);

        _exchange.OnQuote = null;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(300m, Assert.Single(_exchange.Withdrawals).Amount); // the amount to keep stays
    }

    [Fact]
    public async Task WithdrawsTheBalanceToTheSavedDestination()
    {
        Configure(keep: 20m);
        _exchange.Balance = 320.456m;
        Assert.Equal(PayoutRunResult.Ran, await Service().RunAsync(StoreId, Ct));

        var withdrawal = Assert.Single(_exchange.Withdrawals);
        Assert.Equal(("USD", "test-bank", 300.45m, 2m), withdrawal); // rounded down, the amount to keep left, fee capped
        Assert.Equal("REF-1", Assert.Single(State.Pending).Reference); // accepted, but not done until the exchange lists it
        Assert.Equal(300.45m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
        Assert.Equal(["REF-1"], State.Known);
        var sent = State.Activity[0];
        Assert.Equal(ActivityOutcome.Sent, sent.Outcome);
        Assert.Contains("REF-1", sent.Message);
        Assert.Contains("\"test-bank\"", sent.Message);

        // The next run finds it listed and counts it only once.
        _clock.Advance(TimeSpan.FromHours(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sent && a.Message.Contains("lists the earlier withdrawal") && a.Message.Contains("REF-1"));
        Assert.Equal(300.45m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
    }

    [Fact]
    public async Task AnAcceptedWithdrawalStaysPendingUntilTheExchangeListsIt()
    {
        // An account that needs withdrawals approved returns a reference but lists the request only once it is approved.
        Configure(minimum: 10m);
        _exchange.Balance = 300m;
        _exchange.AwaitApproval = true;
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal("REF-1", pending.Reference);
        Assert.Equal(300m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));

        _exchange.Balance = 200m;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals); // nothing more while it isn't listed
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("doesn't list it yet", State.Activity[0].Message);

        _clock.Advance(PayoutService.PendingGrace);
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals); // never treated as lost, and never matched by amount
        Assert.Single(State.Pending);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("approve or decline it there", State.Activity[0].Message);

        // Approved and paid out: listed, so payout resumes in the same run, still counting the first one once.
        _exchange.Unlisted.Clear();
        _exchange.AwaitApproval = false;
        _exchange.Settle();
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(2, _exchange.Withdrawals.Count);
        Assert.Equal(500m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
    }

    [Fact]
    public async Task AnAcceptedWithdrawalThatFailsStaysCounted()
    {
        Configure(minimum: 10m, cap: 400m);
        _exchange.Balance = 300m;
        await Service().RunAsync(StoreId, Ct);
        _exchange.Records[0] = _exchange.Records[0] with { Status = WithdrawalStatus.Failed };
        _exchange.Balance = 300m; // returned
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Failed && a.Message.Contains("still counts against that day's cap"));
        Assert.Equal(100m, _exchange.Withdrawals[1].Amount); // what the cap had left
        Assert.Equal(400m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
    }

    [Fact]
    public async Task RemovingAnAcceptedWithdrawalDoesntCountItTwice()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 300m;
        _exchange.AwaitApproval = true;
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.True(await Service().RemovePendingAsync(StoreId, pending.Id, Ct));
        Assert.Empty(State.Pending);
        Assert.Equal(300m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
        Assert.Contains("REF-1", State.Activity[0].Message);
    }

    [Fact]
    public async Task DryRunQuotesButNeverWithdraws()
    {
        Configure(dryRun: true);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Single(_exchange.Quotes);
        Assert.Equal(ActivityOutcome.DryRun, State.Activity[0].Outcome);
        Assert.Contains("would withdraw 500 USD", State.Activity[0].Message);
        Assert.Equal(0m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
    }

    [Fact]
    public async Task PausedOrDisabledDoesNothing()
    {
        Configure(paused: true);
        _exchange.Balance = 500m;
        Assert.Equal(PayoutRunResult.Paused, await Service().RunAsync(StoreId, Ct));
        Configure(enabled: false);
        Assert.Equal(PayoutRunResult.Disabled, await Service().RunAsync(StoreId, Ct));
        Assert.Empty(_exchange.Calls);
    }

    [Fact]
    public async Task RefusesToRunWithTheTradingKey()
    {
        Configure(payoutKey: "test-trading-key");
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Calls);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("trading key", State.Activity[0].Message);
    }

    [Fact]
    public async Task RefusesAKeyThatCanManageDestinations()
    {
        // Checked every run: a permission can be added to the key after it was tested.
        Configure();
        _exchange.Balance = 500m;
        _exchange.KeyProblems.Add("This key can add withdrawal addresses.");
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.DoesNotContain("balance", _exchange.Calls);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("can add withdrawal addresses", State.Activity[0].Message);
    }

    [Theory]
    [InlineData(40, 50, "Waiting until at least 50 USD")] // below the owner's minimum
    [InlineData(100, 0.01, "below Fake Exchange's minimum withdrawal of 10")] // 9 would arrive after the fee
    public async Task SmallAmountsWait(decimal balance, decimal minimum, string message)
    {
        Configure(minimum: minimum);
        _exchange.Balance = balance;
        _exchange.Minimum = balance == 100 ? 100m : 10m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains(balance == 100 ? "below Fake Exchange's minimum withdrawal of 100" : message, State.Activity[0].Message);
    }

    [Fact]
    public async Task FeeAboveTheHighestFeeWaits()
    {
        Configure(maxFee: 0m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("above your highest fee of 0", State.Activity[0].Message);
    }

    [Fact]
    public async Task DailyCapLimitsEachDay()
    {
        Configure(cap: 300m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(300m, _exchange.Withdrawals.Single().Amount);
        _exchange.Settle(); // the first one is done, so nothing is held back for it

        _clock.Advance(TimeSpan.FromHours(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.Contains("cap of 300 USD is reached", State.Activity[0].Message);

        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal([300m, 200m], _exchange.Withdrawals.Select(w => w.Amount));
    }

    [Fact]
    public async Task WithdrawalsInProgressAreLeftOnTheExchange()
    {
        // A withdrawal still in progress may not have left the balance yet, so its amount is never paid out again.
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        _exchange.Records.Add(new WithdrawalRecord("OTHER-1", "test-bank", 149m, 1m, Start.AddDays(-1), WithdrawalStatus.InProgress));
        _exchange.Records.Add(new WithdrawalRecord("OTHER-2", "test-bank", 999m, 1m, Start.AddDays(-2), WithdrawalStatus.Succeeded));
        _exchange.Records.Add(new WithdrawalRecord("OTHER-3", "test-bank", 999m, 1m, Start.AddDays(-2), WithdrawalStatus.Failed));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(350m, _exchange.Withdrawals.Single().Amount);
    }

    [Fact]
    public async Task ExchangeLimitIsRequotedAtTheLimit()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.Limit = 200m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal([500m, 200m], _exchange.Quotes);
        Assert.Equal(200m, _exchange.Withdrawals.Single().Amount);
    }

    [Fact]
    public async Task RefusedWithdrawalIsNotPendingOrCounted()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.WithdrawError = new ExchangeException("Kraken: EFunding:Unknown withdraw key", rejected: true);
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(0m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
        Assert.Contains("Nothing was withdrawn", State.Activity[0].Message);
    }

    [Fact]
    public async Task LostWithdrawalIsNeverSentAgainBlindly()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.WithdrawError = new HttpRequestException("timeout"); // nothing reached the exchange, but that isn't known
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Equal((500m, 499m, "test-bank"), (pending.Amount, pending.NetAmount, pending.Destination));
        Assert.Contains("never retried automatically", State.Activity[0].Message);

        _exchange.WithdrawError = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome); // not listed yet, within the grace period

        _clock.Advance(PayoutService.PendingGrace);
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals); // only the first attempt
        Assert.Single(State.Pending);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("remove it under Pending withdrawals", State.Activity[0].Message);

        // The owner checks the exchange and removes it: it counts in full today, and payout resumes.
        Assert.True(await Service().RemovePendingAsync(StoreId, pending.Id, Ct));
        Assert.Empty(State.Pending);
        Assert.Equal(500m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
        Assert.False(await Service().RemovePendingAsync(StoreId, pending.Id, Ct));
    }

    [Fact]
    public async Task LostWithdrawalFoundOnTheExchangeIsCountedOnce()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true; // it went through; only the answer was lost
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending);
        Assert.Equal(0m, _exchange.Balance);

        _exchange.LoseResponse = false;
        _exchange.Balance = 100m; // more arrived meanwhile
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending); // listed, but taken for it only once nothing else can match
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("may be the earlier withdrawal", State.Activity[0].Message);

        _clock.Advance(PayoutService.MatchAfter);
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Contains("REF-1", State.Known);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sent && a.Message.StartsWith("Found the earlier withdrawal"));
        // The found one is still in progress, so its amount stays out of what can be sent: nothing more goes out yet.
        Assert.Single(_exchange.Withdrawals);
        Assert.Equal(500m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
    }

    [Fact]
    public async Task LostWithdrawalThatFailedIsNotCounted()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true;
        await Service().RunAsync(StoreId, Ct);
        _exchange.LoseResponse = false;
        _exchange.Records[0] = _exchange.Records[0] with { Status = WithdrawalStatus.Failed };
        _exchange.Balance = 0m;
        _clock.Advance(PayoutService.MatchAfter);
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(0m, State.WithdrawnOn(_clock.GetUtcNow(), "USD"));
        Assert.Contains(State.Activity, a => a.Message.Contains("failed or was cancelled"));
    }

    [Fact]
    public async Task AnEarlierWithdrawalIsNeverMistakenForALostOne()
    {
        Configure();
        _exchange.Balance = 500m;
        // A withdrawal of the same amount to the same destination a minute earlier, made by hand, that failed.
        _exchange.Records.Add(new WithdrawalRecord("HAND-1", "test-bank", 499m, 1m, Start - TimeSpan.FromMinutes(1), WithdrawalStatus.Failed));
        _exchange.LoseResponse = true; // and this one's response is lost before Kraken lists it
        _exchange.OnWithdraw = () => _exchange.Records.RemoveAt(_exchange.Records.Count - 1);
        await Service().RunAsync(StoreId, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Contains("HAND-1", pending.SeenBefore);

        _exchange.LoseResponse = false;
        _exchange.OnWithdraw = null;
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending); // still unknown: never matched to the earlier one, so nothing is sent again
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task ALaterWithdrawalIsNeverTakenForALostOneThatIsListedLate()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true; // accepted, but the answer is lost and the exchange is slow to list it
        _exchange.OnWithdraw = () => _exchange.Unlisted.Add(_exchange.Records[^1].Reference);
        await Service().RunAsync(StoreId, Ct);
        _exchange.LoseResponse = false;
        _exchange.OnWithdraw = null;

        // The owner withdraws the same amount to the same destination by hand, and that one is listed first.
        _clock.Advance(TimeSpan.FromMinutes(10));
        _exchange.Records.Add(new WithdrawalRecord("HAND-1", "test-bank", 499m, 1m, _clock.GetUtcNow(), WithdrawalStatus.Succeeded));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending); // not taken for it while the real one may still be listed
        Assert.DoesNotContain("HAND-1", State.Known);

        _exchange.Unlisted.Clear();
        _clock.Advance(PayoutService.MatchAfter);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending); // both listed now: which is which needs the owner
        Assert.Contains("Several withdrawals", State.Activity[0].Message);
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task AWithdrawalOnHoldLongerThanTheHistoryStaysHeldBack()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(500m, Assert.Single(_exchange.Withdrawals).Amount);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct); // listed, still in progress (on hold)
        Assert.Empty(State.Pending);

        // Kraken hasn't taken it from the balance yet, and it is still on hold after the usual history.
        _exchange.Balance = 500m;
        _clock.Advance(PayoutService.History + TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals); // never paid out again
        Assert.Contains("500 in withdrawals still in progress", State.Activity[0].Message);

        // Once it is final, payout resumes.
        _exchange.Settle();
        _exchange.Balance = 100m;
        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Unsettled);
        Assert.Equal([500m, 100m], _exchange.Withdrawals.Select(w => w.Amount));
    }

    [Fact]
    public async Task AnotherAssetsUnsettledWithdrawalIsNotHeldBack()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct); // listed, still in progress
        Assert.Equal("USD", Assert.Single(State.Unsettled).Asset);

        // Payout switches to another asset, whose list never shows the USD withdrawal.
        _payouts.Settings[StoreId].Asset = "EUR";
        _exchange.Records.Clear();
        _exchange.Balance = 300m;
        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(("EUR", "test-bank", 300m, 2m), _exchange.Withdrawals[1]);
        Assert.Contains(State.Unsettled, u => u.Asset == "USD" && u.Reference == "REF-1"); // still held back if payout goes back to USD
    }

    [Fact]
    public async Task ANewKeyForTheSameAccountStillHoldsBackALongHeldWithdrawal()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct); // listed, still in progress
        Assert.Single(State.Unsettled);

        // The owner rotates the payout key (same exchange account), and the withdrawal stays on hold past the usual history.
        _payouts.Settings[StoreId].ProtectedApiKey = Protector.Protect("test-rotated-payout-key");
        _exchange.Balance = 500m;
        _clock.Advance(PayoutService.History + TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.Contains("500 in withdrawals still in progress", State.Activity[0].Message);
    }

    [Fact]
    public async Task AnotherAccountsUnsettledWithdrawalIsKeptButNotHeldBack()
    {
        Configure(minimum: 10m);
        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Unsettled);

        // A key for another account on the same exchange: its balance never held the withdrawal.
        _exchange.Accounts["test-other-account-key"] = "test-account-2";
        _payouts.Settings[StoreId].ProtectedApiKey = Protector.Protect("test-other-account-key");
        _exchange.Records.Clear();
        _exchange.Balance = 300m;
        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(300m, _exchange.Withdrawals[1].Amount);
        _exchange.Settle();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync(StoreId, Ct); // listed and done on that account
        Assert.Empty(State.Pending);

        // Back on the first account after the usual history: the withdrawal still on hold there is held back again.
        _exchange.Records.Clear();
        _exchange.Records.Add(new WithdrawalRecord("REF-1", "test-bank", 499m, 1m, Start, WithdrawalStatus.InProgress));
        _payouts.Settings[StoreId].ProtectedApiKey = Protector.Protect("test-payout-key");
        _exchange.Balance = 500m;
        _clock.Advance(PayoutService.History);
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(2, _exchange.Withdrawals.Count);
        Assert.Contains("500 in withdrawals still in progress", State.Activity[0].Message);
    }

    [Fact]
    public async Task TwoStoresNeverPayOutWithTheSameKey()
    {
        Configure();
        _exchange.Balance = 500m;
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("Another store on this server pays out USD live from the same Fake Exchange account", State.Activity[0].Message);
        Assert.DoesNotContain("test-store-2", State.Activity[0].Message);

        _payouts.Settings["test-store-2"].DryRun = true; // the other store only quotes, so this one may pay out
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task TwoStoresPayOutDifferentAssetsOfOneAccount()
    {
        // Each asset has its own balance and withdrawals, so a CAD payout from the account doesn't hold up a USD one.
        Configure();
        _exchange.Balance = 500m;
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings["test-store-2"].Asset = "CAD";
        _payouts.Settings["test-store-2"].Destination = "test-bank-cad";
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(("USD", "test-bank", 500m), (_exchange.Withdrawals.Single().Asset, _exchange.Withdrawals.Single().Destination, _exchange.Withdrawals.Single().Amount));

        _payouts.Settings["test-store-2"].Asset = "usd"; // the same asset, however it is written
        _exchange.Balance = 500m;
        _clock.Advance(TimeSpan.FromDays(1));
        _exchange.Settle();
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.Contains("pays out USD live from the same Fake Exchange account", State.Activity[0].Message);
    }

    [Fact]
    public async Task AnotherStoresUnresolvedWithdrawalOfAnotherAssetDoesntBlockPayout()
    {
        Configure();
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings["test-store-2"].Asset = "CAD";
        _payouts.Settings["test-store-2"].Destination = "test-bank-cad";
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true;
        _exchange.OnWithdraw = () => _exchange.Unlisted.Add(_exchange.Records[^1].Reference);
        await Service().RunAsync("test-store-2", Ct);
        Assert.Equal("CAD", _payouts.States["test-store-2"].Pending.Single().Asset);
        _exchange.LoseResponse = false;
        _exchange.OnWithdraw = null;

        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(2, _exchange.Withdrawals.Count);
        Assert.Equal("USD", _exchange.Withdrawals[1].Asset);
    }

    [Fact]
    public async Task TwoStoresNeverPayOutOneAccountWithDifferentKeys()
    {
        Configure();
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings["test-store-2"].ProtectedApiKey = Protector.Protect("test-payout-key-2"); // another key, same account
        await Service().RunAsync("test-store-2", Ct); // nothing to withdraw yet, but its account is saved

        _exchange.Balance = 500m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("pays out USD live from the same Fake Exchange account", State.Activity[0].Message);
        Assert.DoesNotContain("test-account", _payouts.States[StoreId].Account!.Hash); // only a hash is kept

        // The other store's key is replaced with one for another account: what it saved for the old key no longer counts.
        _exchange.Accounts["test-payout-key-3"] = "test-account-3";
        _payouts.Settings["test-store-2"].ProtectedApiKey = Protector.Protect("test-payout-key-3");
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task AnExchangeThatDoesntReportAccountsAllowsOneLiveStore()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.Accounts["test-payout-key"] = null;
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings["test-store-2"].ProtectedApiKey = Protector.Protect("test-payout-key-2");
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("doesn't say which account", State.Activity[0].Message);
    }

    [Fact]
    public async Task ADryRunMakesTheSameFinalChecks()
    {
        Configure(dryRun: true, keep: 100m);
        _exchange.Balance = 500m;
        _exchange.OnQuote = () => _exchange.Balance = 400m;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("balance changed during the run", State.Activity[0].Message);

        _exchange.OnQuote = null;
        await Service().RunAsync(StoreId, Ct);
        Assert.Equal(ActivityOutcome.DryRun, State.Activity[0].Outcome);
        Assert.Contains("would withdraw 300 USD", State.Activity[0].Message);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task AnotherStoresTradingKeyIsNeverUsed()
    {
        // Each store's credentials serve only its own runs: another store trading on the exchange isn't looked up.
        Configure();
        _exchange.Balance = 500m;
        _main.Settings["test-store-2"] = new SweepSellSettings
        {
            ExchangeId = FakeWithdrawals.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-trading-key-2"),
            ProtectedApiSecret = Protector.Protect("test-trading-secret-2"),
            Convert = { Enabled = true, DryRun = false, Assets = ["USD"] }
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.DoesNotContain("test-trading-key-2", _exchange.KeysUsed);
    }

    [Fact]
    public async Task AnotherStoresUnresolvedWithdrawalKeepsTheAccountBlockedAfterItsPayoutIsTurnedOff()
    {
        Configure();
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings["test-store-2"].ProtectedApiKey = Protector.Protect("test-payout-key-2"); // same account
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true;
        _exchange.OnWithdraw = () => _exchange.Unlisted.Add(_exchange.Records[^1].Reference); // and it isn't listed yet
        await Service().RunAsync("test-store-2", Ct);
        Assert.Single(_payouts.States["test-store-2"].Pending);
        _exchange.LoseResponse = false;
        _exchange.OnWithdraw = null;
        _payouts.Settings["test-store-2"].Enabled = false;

        _exchange.Balance = 500m; // not taken from the balance yet
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.Contains("still has USD withdrawals it hasn't seen settle from", State.Activity[0].Message);
    }

    [Fact]
    public async Task APendingWithdrawalListedAfterTheUsualHistoryIsStillFound()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true;
        _exchange.OnWithdraw = () => _exchange.Unlisted.Add(_exchange.Records[^1].Reference);
        await Service().RunAsync(StoreId, Ct);
        _exchange.LoseResponse = false;
        _exchange.OnWithdraw = null;

        _clock.Advance(PayoutService.History + TimeSpan.FromDays(1));
        _exchange.Unlisted.Clear(); // listed at last, with the time it was requested
        _exchange.Settle();
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(State.Pending);
        Assert.Contains(State.Activity, a => a.Message.StartsWith("Found the earlier withdrawal"));
    }

    [Fact]
    public async Task APendingWithdrawalThatCouldntBeSavedIsNeitherSentNorKept()
    {
        Configure();
        _exchange.Balance = 500m;
        _payouts.FailNextPendingSave = true;
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Empty(State.Pending); // the save after the run doesn't keep it
        Assert.Contains("unexpected error", State.Activity[0].Message);
    }

    [Fact]
    public async Task AnotherStoresUnsettledWithdrawalCountsForItsOwnAccount()
    {
        Configure(minimum: 10m);
        _main.Settings["test-store-2"] = Copy(_main.Settings[StoreId]);
        _payouts.Settings["test-store-2"] = Copy(_payouts.Settings[StoreId]);
        _payouts.Settings[StoreId].Enabled = false;
        _exchange.Balance = 500m;
        await Service().RunAsync("test-store-2", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync("test-store-2", Ct); // listed, still on hold on test-account
        Assert.Single(_payouts.States["test-store-2"].Unsettled);

        // The other store moves on to another account; its withdrawal on hold still holds up the first one.
        _exchange.Accounts["test-payout-key-2"] = "test-account-2";
        _payouts.Settings["test-store-2"].ProtectedApiKey = Protector.Protect("test-payout-key-2");
        _exchange.Balance = 0m;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Service().RunAsync("test-store-2", Ct);
        Assert.Equal(PayoutAccount.For(FakeWithdrawals.ExchangeId, "test-account-2",
            Protector.Credentials(_payouts.Settings["test-store-2"])!).Hash, _payouts.States["test-store-2"].Account!.Hash);

        _payouts.Settings[StoreId].Enabled = true;
        _payouts.Settings["test-store-2"].Enabled = false;
        _exchange.Balance = 500m;
        _clock.Advance(PayoutService.History + TimeSpan.FromDays(1)); // too old for this store's own read to see it
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
        Assert.Contains("still has USD withdrawals it hasn't seen settle from", State.Activity[0].Message);
    }

    [Fact]
    public async Task ThisStoresConvertSellingThePayoutAssetStopsPayout()
    {
        Configure();
        _exchange.Balance = 500m;
        _main.Settings[StoreId].Convert = new ConvertSettings { Enabled = true, DryRun = false, Assets = ["usd"], TargetAsset = "BTC" };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("This store's convert sells USD from the same Fake Exchange account", State.Activity[0].Message);

        _main.Settings[StoreId].Convert.Assets = ["LTC"]; // converting other assets is fine
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task ThisStoresHedgingStopsABitcoinPayout()
    {
        Configure();
        _payouts.Settings[StoreId].Asset = "BTC";
        _exchange.Balance = 500m;
        _main.Settings[StoreId].Hedge = new HedgeSettings { Enabled = true, DryRun = false };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("This store's hedging sells BTC from the same Fake Exchange account", State.Activity[0].Message);

        _main.Settings[StoreId].Hedge.DryRun = true; // a hedging dry run sells nothing
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(_exchange.Withdrawals);
    }

    [Fact]
    public async Task ATradingKeySavedDuringTheRunIsCheckedBeforeWithdrawing()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.OnQuote = () =>
        {
            _main.Settings[StoreId].ProtectedApiKey = Protector.Protect("test-new-trading-key");
            _exchange.OtherKeyProblems.Add("This key can add withdrawal addresses.");
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Empty(State.Pending);
        Assert.Equal(["test-trading-key", "test-new-trading-key"], _exchange.OtherKeysChecked);
        Assert.Contains("trading key makes payouts unsafe", State.Activity[0].Message);
    }

    [Fact]
    public async Task SeveralMatchingWithdrawalsNeedTheOwner()
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.LoseResponse = true;
        await Service().RunAsync(StoreId, Ct);
        _exchange.LoseResponse = false;
        // Someone also withdrew the same amount by hand at the same time.
        _exchange.Records.Add(_exchange.Records[0] with { Reference = "HAND-1" });
        await Service().RunAsync(StoreId, Ct);
        Assert.Single(State.Pending);
        Assert.Contains("Several withdrawals", State.Activity[0].Message);
    }

    [Fact]
    public void MatchingUsesAmountTimeDestinationAndSkipsKnownReferences()
    {
        var pending = new PendingWithdrawal { Destination = "test-bank", Amount = 100m, NetAmount = 99m, Fee = 1m, SentAt = Start };
        var state = new PayoutState();
        var record = new WithdrawalRecord("R1", "test-bank", 99m, 1m, Start.AddSeconds(5), WithdrawalStatus.InProgress);
        Assert.True(PayoutService.Matches(record, pending, state)); // net amount
        Assert.True(PayoutService.Matches(record with { Amount = 100m }, pending, state)); // gross amount
        Assert.True(PayoutService.Matches(record with { Destination = null }, pending, state)); // destination not reported
        Assert.False(PayoutService.Matches(record with { Amount = 98m }, pending, state));
        Assert.False(PayoutService.Matches(record with { Destination = "test-other" }, pending, state));
        Assert.False(PayoutService.Matches(record with { RequestedAt = Start - TimeSpan.FromMinutes(3) }, pending, state));
        Assert.False(PayoutService.Matches(record with { RequestedAt = Start + TimeSpan.FromHours(2) }, pending, state));
        state.Remember("R1");
        Assert.False(PayoutService.Matches(record, pending, state));
        Assert.False(PayoutService.Matches(record with { Reference = "R0" }, new PendingWithdrawal
        {
            Destination = "test-bank", Amount = 100m, NetAmount = 99m, Fee = 1m, SentAt = Start, SeenBefore = ["R0"]
        }, new PayoutState())); // listed before it was sent
    }

    [Fact]
    public async Task PendingFromAnotherKeyBlocksPayout()
    {
        Configure();
        _exchange.Balance = 500m;
        _payouts.States[StoreId] = new PayoutState
        {
            Pending = [new PendingWithdrawal { Id = Guid.NewGuid(), ExchangeId = FakeWithdrawals.ExchangeId, KeyFingerprint = "0000000000000000", Asset = "USD", Amount = 1m, SentAt = Start }]
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Contains("different payout key", State.Activity[0].Message);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("destination")]
    [InlineData("dry run")]
    [InlineData("key")]
    public async Task SettingsSavedDuringTheRunApplyBeforeWithdrawing(string change)
    {
        Configure();
        _exchange.Balance = 500m;
        _exchange.OnQuote = () =>
        {
            switch (change)
            {
                case "pause": _main.Settings[StoreId].Paused = true; break;
                case "destination": _payouts.Settings[StoreId].Destination = "test-other"; break;
                case "dry run": _payouts.Settings[StoreId].DryRun = true; break;
                case "key": _payouts.Settings[StoreId].ProtectedApiSecret = Protector.Protect("test-new-secret"); break;
            }
        };
        await Service().RunAsync(StoreId, Ct);
        Assert.Empty(_exchange.Withdrawals);
        Assert.Empty(State.Pending);
        Assert.Contains("settings changed during the run", State.Activity[0].Message);
    }

    [Fact]
    public async Task ChangesOfWhereFundsGoAreLogged()
    {
        Configure();
        var saved = _payouts.Settings[StoreId];
        var updated = new PayoutSettings
        {
            ExchangeId = saved.ExchangeId, ProtectedApiKey = Protector.Protect("test-other-key"), ProtectedApiSecret = saved.ProtectedApiSecret,
            Asset = "EUR", Destination = "test-other"
        };
        var (_, _, error) = await Service().SaveSettingsAsync(StoreId, _ => Task.FromResult((updated, (string?)null)), Ct);
        Assert.Null(error);
        Assert.Same(updated, _payouts.Settings[StoreId]);
        var entry = Assert.Single(State.Activity);
        Assert.Equal(ActivityOutcome.Changed, entry.Outcome);
        Assert.Contains("a new payout key was saved", entry.Message);
        Assert.Contains("asset from USD to EUR", entry.Message);
        Assert.Contains("destination from \"test-bank\" to \"test-other\"", entry.Message);
        Assert.DoesNotContain("test-other-key", entry.Message);
    }

    [Fact]
    public async Task RunsOnlyWhenTheIntervalHasPassed()
    {
        Configure(dryRun: true);
        _exchange.Balance = 500m;
        await Service().RunDueAsync(Ct);
        await Service().RunDueAsync(Ct);
        Assert.Single(_exchange.Quotes);
        _clock.Advance(TimeSpan.FromDays(1));
        await Service().RunDueAsync(Ct);
        Assert.Equal(2, _exchange.Quotes.Count);
    }

    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static T Copy<T>(T s) => Newtonsoft.Json.JsonConvert.DeserializeObject<T>(Newtonsoft.Json.JsonConvert.SerializeObject(s))!;

    sealed class Payouts : IPayoutRepository
    {
        public Dictionary<string, PayoutSettings> Settings { get; } = new();
        public Dictionary<string, PayoutState> States { get; } = new();
        public Task<PayoutSettings> GetAsync(string storeId) => Task.FromResult(Settings.TryGetValue(storeId, out var s) ? Copy(s) : new PayoutSettings());
        public Task SaveAsync(string storeId, PayoutSettings settings) { Settings[storeId] = settings; return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, PayoutSettings>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, PayoutSettings>>(Settings);
        // Stored as copies, like a database, so a test sees only what was saved.
        public Task<PayoutState> GetStateAsync(string storeId) => Task.FromResult(States.TryGetValue(storeId, out var s) ? Copy(s) : new PayoutState());
        public Task SaveStateAsync(string storeId, PayoutState state)
        {
            if (FailNextPendingSave && state.Pending.Count > 0)
            {
                FailNextPendingSave = false;
                throw new InvalidOperationException("The database is unavailable");
            }
            States[storeId] = Copy(state);
            return Task.CompletedTask;
        }

        /// <summary>The next save of a state with a pending withdrawal fails.</summary>
        public bool FailNextPendingSave { get; set; }
    }

    /// <summary>
    /// An invented exchange: USD amounts have 2 decimals, every withdrawal costs a 1 USD fee, and the smallest withdrawal
    /// that may arrive is 10 USD unless a test changes it. A sent withdrawal leaves the balance at once and is listed as in progress.
    /// </summary>
    sealed class FakeWithdrawals(TimeProvider clock) : IWithdrawalConnector
    {
        public const string ExchangeId = "fake";
        public string Id => ExchangeId;
        public string Name => "Fake Exchange";
        public string KeyPermissionsHelp => "";

        public decimal Balance { get; set; }
        public decimal Fee { get; set; } = 1m;
        public decimal Minimum { get; set; } = 10m;
        public decimal Limit { get; set; } = 1_000_000m;
        public List<WithdrawalRecord> Records { get; } = [];
        public List<(string Asset, string Destination, decimal Amount, decimal MaxFee)> Withdrawals { get; } = [];
        public List<decimal> Quotes { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? WithdrawError { get; set; }
        public bool LoseResponse { get; set; }

        /// <summary>New withdrawals are accepted but not listed, as on an account where they wait for approval.</summary>
        public bool AwaitApproval { get; set; }

        public HashSet<string> Unlisted { get; } = [];
        public Action? OnQuote { get; set; }
        public Action? OnWithdraw { get; set; }
        public Action? OnRecent { get; set; }

        public void Settle()
        {
            for (var i = 0; i < Records.Count; i++)
                Records[i] = Records[i] with { Status = WithdrawalStatus.Succeeded };
        }

        public Task<decimal> GetAvailableBalanceAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken)
        {
            Calls.Add("balance");
            return Task.FromResult(Balance);
        }

        public Task<IReadOnlyList<WithdrawalDestination>> GetDestinationsAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WithdrawalDestination>>([new WithdrawalDestination("test-bank", "Bank transfer", true)]);

        public Task<WithdrawalQuote> QuoteAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount, CancellationToken cancellationToken)
        {
            Calls.Add("quote");
            Quotes.Add(amount);
            OnQuote?.Invoke();
            var rounded = Math.Floor(amount * 100m) / 100m;
            return Task.FromResult(new WithdrawalQuote(destination, "Bank transfer", rounded, rounded - Fee, Fee, Minimum, Limit));
        }

        public Task<string> WithdrawAsync(ExchangeCredentials credentials, string asset, string destination, decimal amount, decimal maxFee,
            CancellationToken cancellationToken)
        {
            Calls.Add("withdraw");
            Withdrawals.Add((asset, destination, amount, maxFee));
            if (WithdrawError is not null)
                throw WithdrawError;
            var reference = $"REF-{Withdrawals.Count}";
            Balance -= amount;
            Records.Add(new WithdrawalRecord(reference, destination, amount - Fee, Fee, clock.GetUtcNow(), WithdrawalStatus.InProgress));
            if (AwaitApproval)
                Unlisted.Add(reference);
            OnWithdraw?.Invoke();
            if (LoseResponse)
                throw new HttpRequestException("The response was lost");
            return Task.FromResult(reference);
        }

        public Task<IReadOnlyList<WithdrawalRecord>> GetRecentWithdrawalsAsync(ExchangeCredentials credentials, string asset, DateTimeOffset since,
            CancellationToken cancellationToken)
        {
            Calls.Add("recent");
            OnRecent?.Invoke();
            return Task.FromResult<IReadOnlyList<WithdrawalRecord>>(Records.Where(r => r.RequestedAt >= since && !Unlisted.Contains(r.Reference)).ToList());
        }

        public Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public List<string> KeyProblems { get; } = [];

        public Task<IReadOnlyList<string>> GetKeyProblemsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(KeyProblems.ToList());

        /// <summary>The account each key belongs to; one not listed belongs to "test-account". Null means the exchange doesn't say.</summary>
        public Dictionary<string, string?> Accounts { get; } = [];

        /// <summary>Every key the account was asked for with.</summary>
        public List<string> KeysUsed { get; } = [];

        public Task<string?> GetAccountIdAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
        {
            KeysUsed.Add(credentials.ApiKey);
            return Task.FromResult(Accounts.TryGetValue(credentials.ApiKey, out var account) ? account : "test-account");
        }

        /// <summary>What the trading key's permissions allow that endangers payouts.</summary>
        public List<string> OtherKeyProblems { get; } = [];
        public List<string> OtherKeysChecked { get; } = [];

        public Task<IReadOnlyList<string>> GetOtherKeyProblemsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
        {
            OtherKeysChecked.Add(credentials.ApiKey);
            return Task.FromResult<IReadOnlyList<string>>(OtherKeyProblems.ToList());
        }
    }
}
