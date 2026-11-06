using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Plugins.SweepSell.Sweeping;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class SweepServiceTests
{
    // Each test uses its own store ID: run locks are static, like the plugin's single instance.
    readonly string _storeId = "sweep-store-" + Guid.NewGuid().ToString("N");
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());

    readonly MemoryRepository _repository = new();
    readonly FakeExchange _exchange = new();
    readonly FakeWallets _wallets = new();
    readonly ManualClock _clock = new(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    SweepService Service()
    {
        var registry = new ExchangeRegistry([_exchange]);
        var convert = new ConvertService(_repository, Protector, registry, _clock, NullLogger<ConvertService>.Instance);
        return new SweepService(_repository, Protector, registry, _wallets, convert, _clock, NullLogger<SweepService>.Instance);
    }

    SweepRule Configure(string? storeId = null, bool dryRun = false, decimal balance = 1m, Action<SweepRule>? rule = null, bool convert = false)
    {
        storeId ??= _storeId;
        var btc = new SweepRule { CryptoCode = "BTC", Enabled = true, MinimumBalance = 0.01m };
        rule?.Invoke(btc);
        _repository.Settings[storeId] = new SweepSellSettings
        {
            ExchangeId = FakeExchange.ExchangeId,
            ProtectedApiKey = Protector.Protect("test-api-key"),
            ProtectedApiSecret = Protector.Protect("test-api-secret"),
            Convert = new ConvertSettings { Enabled = convert, DryRun = false, Assets = ["BTC"], TargetAsset = "CAD" },
            Sweep = new SweepSettings { Enabled = true, DryRun = dryRun, Rules = [btc] }
        };
        _wallets.Balances[$"{storeId}:BTC"] = balance;
        return btc;
    }

    SweepState State => _repository.SweepStates[_storeId];

    [Fact]
    public async Task DryRunSignsButNeverBroadcasts()
    {
        Configure(dryRun: true);
        Assert.Equal(SweepRunResult.Ran, await Service().RunAsync(_storeId, wait: false, Ct));
        Assert.Single(_wallets.Built);
        Assert.Empty(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Empty(State.Awaiting);
        Assert.Equal(ActivityOutcome.DryRun, State.Activity[0].Outcome);
        Assert.Contains("0.9999 BTC to Fake Exchange (exchange-deposit-address)", State.Activity[0].Message);
        Assert.Contains("fee 0.0001 BTC (2.5 sat/vB, aiming for confirmation within 3 blocks)", State.Activity[0].Message);
        Assert.Equal(3, Assert.Single(_wallets.Built).FeeTargetBlocks); // about half an hour unless the owner picks otherwise
    }

    [Fact]
    public async Task TheChosenFeeTargetIsAskedForAndShown()
    {
        Configure(dryRun: true);
        _repository.Settings[_storeId].Sweep.FeeTargetBlocks = 1;
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(1, Assert.Single(_wallets.Built).FeeTargetBlocks);
        Assert.Contains("(2.5 sat/vB, aiming for the next block)", State.Activity[0].Message);
    }

    [Fact]
    public async Task AnotherCoinsFeeRateIsNotCalledSatoshis()
    {
        Configure(dryRun: true, rule: r => r.CryptoCode = "LTC");
        _wallets.Balances[$"{_storeId}:LTC"] = 1m;
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Built);
        var message = State.Activity[0].Message;
        Assert.Contains("(2.5 of the smallest LTC unit per vB, aiming for confirmation within 3 blocks)", message);
        Assert.DoesNotContain("sat/vB", message);
    }

    [Fact]
    public async Task LowestFeeRateIsPaidWhenTheEstimateIsBelowIt()
    {
        Configure(dryRun: true, rule: r => r.MinimumFeeRate = 3);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(3m, Assert.Single(_wallets.Built).MinimumFeeRate);
        Assert.Contains("(3 sat/vB, the lowest fee rate for BTC: the estimate for confirmation within 3 blocks was only 2.5)", State.Activity[0].Message);
    }

    [Fact]
    public async Task EstimateAboveTheLowestFeeRateIsPaidAsUsual()
    {
        Configure(dryRun: true, rule: r => r.MinimumFeeRate = 1);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Contains("(2.5 sat/vB, aiming for confirmation within 3 blocks)", State.Activity[0].Message);
    }

    [Fact]
    public async Task SentSweepStillSaysTheLowestFeeRateWasUsed()
    {
        Configure(rule: r => r.MinimumFeeRate = 3);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sent &&
                                             a.Message.Contains("the lowest fee rate for BTC: the estimate for confirmation within 3 blocks was only 2.5"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    public async Task SweepsNeverAskForLessThanOneSatPerVbyte(double rate)
    {
        Configure(dryRun: true, rule: r => r.MinimumFeeRate = (decimal)rate);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(SweepSettings.LowestFeeRate, Assert.Single(_wallets.Built).MinimumFeeRate);
    }

    [Fact]
    public async Task LiveSweepBroadcastsAndWaitsForTheDeposit()
    {
        Configure();
        await Service().RunAsync(_storeId, wait: false, Ct);
        var request = Assert.Single(_wallets.Built);
        Assert.Equal(new SweepRequest(false, null, "exchange-deposit-address", null, 0, 3, SweepSettings.LowestFeeRate), request);
        Assert.Single(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        var awaiting = Assert.Single(State.Awaiting);
        Assert.Equal(("BTC", 0.9999m, FakeExchange.ExchangeId), (awaiting.Asset, awaiting.Amount, awaiting.ExchangeId));
        Assert.Equal(1m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Equal(ActivityOutcome.Sent, State.Activity[0].Outcome);
    }

    [Fact]
    public async Task BelowTheMinimumBalanceWaits()
    {
        Configure(balance: 0.005m);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Built);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("below the minimum of 0.01 BTC", State.Activity[0].Message);
    }

    [Fact]
    public async Task WalletSpentBelowTheMinimumWhileBuildingWaits()
    {
        // Something else on the server spends from the wallet after the balance check: the sweep built from what is left is dropped.
        Configure(balance: 1m);
        _wallets.OnBuild = () =>
        {
            _wallets.Balances[$"{_storeId}:BTC"] = 0.005m;
            return Task.CompletedTask;
        };
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Built);
        Assert.Empty(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("holds 0.005 BTC confirmed, below the minimum of 0.01 BTC", State.Activity[0].Message);
    }

    [Fact]
    public async Task BelowTheExchangeMinimumDepositWaits()
    {
        Configure(balance: 0.02m);
        _exchange.MinimumDeposit = 0.05m;
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Broadcasts);
        Assert.Equal(ActivityOutcome.Waiting, State.Activity[0].Outcome);
        Assert.Contains("minimum deposit of 0.05 BTC", State.Activity[0].Message);
    }

    [Fact]
    public async Task ColdShareGoesToANewAddressOfTheExtendedKeyEachSweep()
    {
        Configure(rule: r =>
        {
            r.ColdPercent = 25;
            r.ColdDestination = "xpub-cold";
            r.ColdStartIndex = 5;
        });
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(("xpub-cold/5", 25m), (_wallets.Built[0].ColdAddress, _wallets.Built[0].ColdPercent));
        Assert.Contains("0.25 BTC to the cold wallet (xpub-cold/5)", State.Activity[0].Message);

        _wallets.Balances[$"{_storeId}:BTC"] = 1m;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal("xpub-cold/6", _wallets.Built[1].ColdAddress);
        Assert.Equal(7, State.NextColdIndex["xpub-cold"]);
    }

    [Fact]
    public async Task DryRunsDoNotUseUpColdAddresses()
    {
        Configure(dryRun: true, rule: r =>
        {
            r.ColdPercent = 10;
            r.ColdDestination = "xpub-cold";
        });
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.All(_wallets.Built, b => Assert.Equal("xpub-cold/0", b.ColdAddress));
        Assert.Empty(State.NextColdIndex);
    }

    [Fact]
    public async Task TheLastAddressOfAnExtendedKeyIsNeverUsed()
    {
        Configure(rule: r =>
        {
            r.ColdPercent = 10;
            r.ColdDestination = "xpub-cold";
            r.ColdStartIndex = int.MaxValue;
        });
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Built);
        Assert.Contains("no unused address left", State.Activity[0].Message);
    }

    [Fact]
    public async Task FixedColdAddressIsReused()
    {
        Configure(rule: r =>
        {
            r.ColdPercent = 10;
            r.ColdDestination = "cold-address";
        });
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        _wallets.Balances[$"{_storeId}:BTC"] = 1m;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.All(_wallets.Built, b => Assert.Equal("cold-address", b.ColdAddress));
        Assert.Empty(State.NextColdIndex);
    }

    [Fact]
    public async Task DailyCapSweepsInPartsAndResumesTheNextDay()
    {
        Configure(balance: 1m, rule: r => r.DailyCap = 0.4m);
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(0.4m, _wallets.Built[0].Amount);
        Assert.Equal(0.6m, _wallets.Balances[$"{_storeId}:BTC"]); // the change stayed in the wallet

        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Built);
        Assert.Contains("Today's cap of 0.4 BTC is reached", State.Activity[0].Message);

        _clock.Advance(TimeSpan.FromDays(1));
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(0.4m, _wallets.Built[1].Amount);
    }

    [Fact]
    public async Task TheCapBoundsTheSweepEvenAboveTheBalance()
    {
        // A payment arriving between reading the balance and building the sweep must not take it past the cap.
        Configure(balance: 0.3m, rule: r => r.DailyCap = 0.5m);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(0.5m, Assert.Single(_wallets.Built).Amount);
    }

    [Fact]
    public async Task UnansweredBroadcastStaysPendingAndOnlyTheSameTransactionIsSentAgain()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("node unreachable"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Empty(State.Awaiting);
        Assert.Contains("rebroadcasts the same transaction", State.Activity[0].Message);

        // The wallet doesn't know the sweep (it never reached the node), so it still shows the coins.
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Built); // nothing new was built: once found, the sweep had spent the coins
        Assert.Equal([pending.Hex, pending.Hex], _wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Equal(pending.Txid, Assert.Single(State.Awaiting).Txid);
        // Sent by the rebroadcast, it still shows the rate and target it was signed with.
        Assert.Contains("(2.5 sat/vB, aiming for confirmation within 3 blocks)", Assert.Single(State.Activity, a => a.Outcome == ActivityOutcome.Sent).Message);
    }

    [Fact]
    public async Task PendingSweepSeenOnTheNetworkIsSettledWithoutRebroadcast()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        _wallets.Statuses[pending.Txid] = TransactionStatus.Unconfirmed;
        _wallets.Balances[$"{_storeId}:BTC"] = 0; // the node got it after all, so its coins are spent

        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Single(State.Awaiting);
        Assert.Equal(1m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Contains("(2.5 sat/vB, aiming for confirmation within 3 blocks)", Assert.Single(State.Activity, a => a.Outcome == ActivityOutcome.Sent).Message);
    }

    [Fact]
    public async Task PendingSweepSavedBeforeFeeDetailsIsShownWithoutThem()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        // Saved by an earlier version, which kept neither.
        (pending.FeeRate, pending.FeeTargetBlocks) = (null, null);
        _wallets.Statuses[pending.Txid] = TransactionStatus.Unconfirmed;
        _wallets.Balances[$"{_storeId}:BTC"] = 0;

        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        var sent = Assert.Single(State.Activity, a => a.Outcome == ActivityOutcome.Sent).Message;
        Assert.Contains("fee 0.0001 BTC", sent);
        Assert.DoesNotContain("sat/vB", sent);
    }

    [Fact]
    public async Task PendingSweepOfARuleTurnedOffIsStillSettledButNeverRebroadcast()
    {
        Configure();
        _repository.Settings[_storeId].Sweep.Rules.Add(new SweepRule { CryptoCode = "LTC", Enabled = true, MinimumBalance = 1m });
        _wallets.Balances[$"{_storeId}:LTC"] = 0m;
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);

        _repository.Settings[_storeId].Sweep.Rules[0].Enabled = false;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts); // not on the network, and its rule is off: not sent again
        Assert.Single(State.Pending);
        Assert.Contains("its BTC rule", State.Activity[0].Message);

        // It reached the network after all: settled and watched although the rule is off.
        _wallets.Statuses[pending.Txid] = TransactionStatus.Unconfirmed;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(pending.Txid, Assert.Single(State.Awaiting).Txid);
        Assert.Single(_wallets.Broadcasts);
    }

    [Fact]
    public async Task PendingSweepIsNotRebroadcastOnceItsCoinIsStopped()
    {
        Configure();
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var first = State.Awaiting[0].Txid;
        _wallets.Balances[$"{_storeId}:BTC"] = 1m;
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        await service.RunAsync(_storeId, wait: false, Ct);
        var second = Assert.Single(State.Pending);

        _exchange.Deposits[first] = new DepositStatus(DepositState.Failed, 0.9999m);
        await service.WatchDepositsAsync(Ct);
        Assert.True(State.Stopped.ContainsKey("BTC"));

        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count); // the second sweep isn't sent while the coin is stopped
        Assert.Single(State.Pending);
        Assert.Contains("sweeping BTC is stopped", State.Activity[0].Message);

        // Its status is still checked: once on the network it is settled and watched.
        _wallets.Statuses[second.Txid] = TransactionStatus.Unconfirmed;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        Assert.Contains(State.Awaiting, a => a.Txid == second.Txid);
        Assert.Equal(2, _wallets.Broadcasts.Count);
    }

    [Fact]
    public async Task PendingSweepIsNotRebroadcastToAnotherExchangeAccount()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        _repository.Settings[_storeId].ProtectedApiKey = Protector.Protect("another-api-key");
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Single(State.Pending);
        Assert.Contains("exchange key changed since it was signed", State.Activity[0].Message);

        _repository.Settings[_storeId].ProtectedApiKey = Protector.Protect("test-api-key"); // put back
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task PendingSweepIsRebroadcastAfterTheLowestFeeRateChanges()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        // The signed transaction's fee is fixed, so the new lowest rate can't change what a rebroadcast sends.
        _repository.Settings[_storeId].Sweep.Rules[0].MinimumFeeRate = 3;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task PendingSweepSignedBeforeTheLowestFeeRateExistedIsStillRebroadcast()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        // A rule saved by an earlier version has no lowest fee rate in it.
        var pending = Assert.Single(State.Pending);
        var old = Newtonsoft.Json.Linq.JObject.Parse(pending.Rule!);
        old.Remove("MinimumFeeRate");
        pending.Rule = Newtonsoft.Json.JsonConvert.SerializeObject(old);
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count);
        Assert.Empty(State.Pending);
    }

    [Theory]
    [InlineData("cold")]
    [InlineData("cap")]
    public async Task PendingSweepIsNotRebroadcastUnderAChangedRule(string change)
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        var rule = _repository.Settings[_storeId].Sweep.Rules[0];
        if (change == "cold")
            (rule.ColdPercent, rule.ColdDestination) = (10, "cold-address");
        else
            rule.DailyCap = 0.5m;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Single(State.Pending);
        Assert.Contains("rule changed since it was signed", State.Activity[0].Message);

        (rule.ColdPercent, rule.ColdDestination, rule.DailyCap) = (0, null, 0); // put back
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count);
        Assert.Empty(State.Pending);
    }

    [Fact]
    public async Task PendingSweepIsStillLookedForWithoutAnExchangeKey()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);

        _repository.Settings[_storeId].ProtectedApiKey = null;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts); // not rebroadcast without the key it was signed for
        Assert.Single(State.Pending);

        _wallets.Statuses[pending.Txid] = TransactionStatus.Unconfirmed;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(pending.Txid, Assert.Single(State.Awaiting).Txid);
    }

    [Fact]
    public async Task FirstBroadcastRefusedSendsNothing()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => new BroadcastOutcome(false, "RPC_VERIFY_REJECTED min relay fee not met"));
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        Assert.Empty(State.Awaiting);
        Assert.Equal(0m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains("Nothing was sent", State.Activity[0].Message);
    }

    [Fact]
    public async Task RefusedRebroadcastWaitsForTheOwnerThenCountsAgainstTheCap()
    {
        Configure(rule: r => r.DailyCap = 2m);
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        _wallets.BroadcastResults.Enqueue(() => new BroadcastOutcome(false, "RPC_VERIFY_ERROR bad-txns-inputs-missingorspent"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        Assert.Single(_wallets.Built);
        Assert.Contains("remove it under Pending sweeps", State.Activity[0].Message);

        Assert.True(await service.RemovePendingAsync(_storeId, pending.Txid, Ct));
        Assert.False(await service.RemovePendingAsync(_storeId, pending.Txid, Ct));
        Assert.Empty(State.Pending);
        Assert.Equal(1m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Built.Count);
    }

    [Fact]
    public async Task PendingSweepIsNotRebroadcastInDryRun()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        _repository.Settings[_storeId].Sweep.DryRun = true;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Single(State.Pending);
        Assert.Single(_wallets.Built); // and nothing new is built while it is unsettled
        Assert.Contains("isn't rebroadcast", State.Activity[0].Message);

        // Still found when the wallet sees it, in dry run too: finding it sends nothing.
        _wallets.Statuses[State.Pending[0].Txid] = TransactionStatus.Confirmed;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(State.Pending);
        Assert.Single(_wallets.Broadcasts);
    }

    [Fact]
    public async Task SweepSettledLateCountsAndIsWatchedFromWhenItIsFound()
    {
        Configure(rule: r => r.DailyCap = 5m);
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        var signedAt = _clock.GetUtcNow();
        await service.RunAsync(_storeId, wait: false, Ct);
        _clock.Advance(TimeSpan.FromDays(2));
        await service.RunAsync(_storeId, wait: false, Ct); // rebroadcast accepted two days later
        Assert.Equal(1m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Equal(_clock.GetUtcNow(), Assert.Single(State.Awaiting).SentAt);
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting); // not timed out straight away
        Assert.Equal(signedAt, _exchange.DepositSearchesSince.Single()); // but searched for from when it was signed: it may have arrived long ago
    }

    [Fact]
    public async Task RemovedSweepCountsTodayAndUsesUpItsColdAddress()
    {
        Configure(rule: r =>
        {
            r.DailyCap = 5m;
            r.ColdPercent = 10;
            r.ColdDestination = "xpub-cold";
        });
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await service.RemovePendingAsync(_storeId, pending.Txid, Ct));
        Assert.Equal(1m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Equal(1, State.NextColdIndex["xpub-cold"]);
        // It may have gone through, so its deposit is watched: a return would stop the coin.
        var watched = Assert.Single(State.Awaiting);
        Assert.Equal((pending.Txid, pending.ToExchange, pending.CreatedAt), (watched.Txid, watched.Amount, watched.SignedAt));
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal("xpub-cold/1", _wallets.Built[1].ColdAddress);

        _exchange.Deposits[pending.Txid] = new DepositStatus(DepositState.Failed, pending.ToExchange);
        await service.WatchDepositsAsync(Ct);
        Assert.Equal(pending.Txid, State.Stopped["BTC"].Txid);
    }

    [Fact]
    public async Task SharedWalletWaitsForAnotherStoresUnsettledSweep()
    {
        var second = _storeId + "-second";
        Configure();
        Configure(second);
        _wallets.Keys[$"{_storeId}:BTC"] = "shared-wallet";
        _wallets.Keys[$"{second}:BTC"] = "shared-wallet";
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        await service.RunAsync(second, wait: false, Ct);
        Assert.Single(_wallets.Built);
        var other = _repository.SweepStates[second];
        Assert.Contains("Another store using this BTC wallet", other.Activity[0].Message);
        Assert.DoesNotContain(_storeId, other.Activity[0].Message.Replace(second, "")); // the other store isn't named to this one's users
    }

    [Fact]
    public async Task ReplacedSweepIsNeitherCountedNorRebroadcast()
    {
        Configure(rule: r => r.DailyCap = 5m);
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);

        _wallets.Statuses[pending.Txid] = TransactionStatus.Replaced;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Single(_wallets.Built);
        Assert.Single(State.Pending);
        Assert.Empty(State.Awaiting);
        Assert.Equal(0m, State.Swept.On(_clock.GetUtcNow(), "BTC"));
        Assert.Contains("was replaced by another transaction", State.Activity[0].Message);
    }

    [Fact]
    public async Task WalletsThatCannotBeSweptAreReported()
    {
        Configure();
        _wallets.Cold.Add($"{_storeId}:BTC");
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Contains("isn't a hot wallet", State.Activity[0].Message);

        _wallets.Balances.TryRemove($"{_storeId}:BTC", out _);
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Contains("no BTC on-chain wallet", State.Activity[0].Message);
        Assert.Empty(_wallets.Built);
    }

    [Theory]
    [InlineData("bad-address", null, "unusable deposit address")]
    [InlineData("exchange-deposit-address", "12345", "needs a tag or memo")]
    public async Task UnusableDepositAddressesAreRefused(string address, string? tag, string message)
    {
        Configure();
        _exchange.DepositAddress = new DepositDestination(address, tag);
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Built);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Contains(message, State.Activity[0].Message);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("dry run")]
    [InlineData("cold wallet")]
    [InlineData("key")]
    public async Task SettingsChangedDuringTheRunStopTheBroadcast(string change)
    {
        Configure();
        _wallets.OnBuild = () =>
        {
            var settings = _repository.Settings[_storeId];
            switch (change)
            {
                case "pause": settings.Paused = true; break;
                case "dry run": settings.Sweep.DryRun = true; break;
                case "cold wallet":
                    settings.Sweep.Rules[0].ColdPercent = 50;
                    settings.Sweep.Rules[0].ColdDestination = "cold-address";
                    break;
                default: settings.ProtectedApiSecret = Protector.Protect("another-secret"); break;
            }
            return Task.CompletedTask;
        };
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Contains("settings changed during the run", State.Activity[0].Message);
    }

    [Fact]
    public async Task PausedAndDisabledStoresDoNothing()
    {
        Configure();
        _repository.Settings[_storeId].Paused = true;
        Assert.Equal(SweepRunResult.Paused, await Service().RunAsync(_storeId, wait: false, Ct));
        _repository.Settings[_storeId].Paused = false;
        _repository.Settings[_storeId].Sweep.Enabled = false;
        Assert.Equal(SweepRunResult.Disabled, await Service().RunAsync(_storeId, wait: false, Ct));
        Assert.Empty(_wallets.Built);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedOrOffStoreStillLooksForItsUnsettledSweep(bool pause)
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var pending = Assert.Single(State.Pending);
        if (pause)
            _repository.Settings[_storeId].Paused = true;
        else
            _repository.Settings[_storeId].Sweep.Enabled = false;

        Assert.Equal(pause ? SweepRunResult.Paused : SweepRunResult.Disabled, await service.RunAsync(_storeId, wait: false, Ct));
        Assert.Single(_wallets.Broadcasts); // never rebroadcast while paused or off
        Assert.Single(State.Pending);

        // The scheduled run still picks the store up, and finds the sweep once it is on the network.
        _wallets.Statuses[pending.Txid] = TransactionStatus.Unconfirmed;
        _clock.Advance(TimeSpan.FromDays(1));
        await service.RunDueAsync(Ct);
        Assert.Empty(State.Pending);
        Assert.Equal(pending.Txid, Assert.Single(State.Awaiting).Txid);
        Assert.Single(_wallets.Built);
        Assert.Single(_wallets.Broadcasts);
    }

    [Fact]
    public async Task WalletReplacedDuringTheRunStopsTheBroadcast()
    {
        Configure();
        _wallets.OnBuild = () =>
        {
            _wallets.Keys[$"{_storeId}:BTC"] = "another-wallet";
            return Task.CompletedTask;
        };
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Built);
        Assert.Empty(_wallets.Broadcasts);
        Assert.Empty(State.Pending);
        Assert.Contains("wallet was changed or removed during the run", State.Activity[0].Message);
    }

    [Fact]
    public async Task PendingSweepFromAReplacedWalletIsNotRebroadcast()
    {
        Configure();
        _wallets.BroadcastResults.Enqueue(() => throw new HttpRequestException("timeout"));
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(State.Pending);

        _wallets.Keys[$"{_storeId}:BTC"] = "another-wallet";
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Single(State.Pending);
        Assert.Contains("a wallet this store no longer uses", State.Activity[0].Message);
    }

    [Fact]
    public async Task NoKeyMeansNowhereToSweep()
    {
        Configure();
        _repository.Settings[_storeId].ProtectedApiSecret = null;
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Empty(_wallets.Built);
        Assert.Contains("No exchange key", State.Activity[0].Message);
    }

    [Fact]
    public async Task ExchangeErrorsAreRecordedPerCoin()
    {
        Configure();
        _exchange.DepositAddressError = new ExchangeException("EFunding:Unknown asset");
        await Service().RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(ActivityOutcome.Failed, State.Activity[0].Outcome);
        Assert.Equal("Could not sweep BTC: EFunding:Unknown asset", State.Activity[0].Message);
        Assert.Empty(_wallets.Broadcasts);
    }

    [Fact]
    public async Task ScheduledRunsWaitForTheInterval()
    {
        Configure(dryRun: true);
        var service = Service();
        await service.RunDueAsync(Ct);
        Assert.Single(_wallets.Built);
        _clock.Advance(TimeSpan.FromMinutes(59));
        await service.RunDueAsync(Ct);
        Assert.Single(_wallets.Built);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.RunDueAsync(Ct);
        Assert.Equal(2, _wallets.Built.Count);
    }

    [Fact]
    public async Task ChainActivityRunsOnlyStoresSweepingThatCoin()
    {
        Configure(dryRun: true);
        var other = _storeId + "-ltc";
        Configure(other, dryRun: true, rule: r => r.CryptoCode = "LTC");
        _wallets.Balances[$"{other}:LTC"] = 1m;
        await Service().OnChainActivityAsync("btc", Ct);
        Assert.Single(_wallets.Built);
        Assert.False(_repository.SweepStates.ContainsKey(other));
    }

    [Fact]
    public async Task StoresSharingAWalletNeverBuildAtTheSameTime()
    {
        var second = _storeId + "-second";
        Configure(dryRun: true);
        Configure(second, dryRun: true);
        _wallets.Keys[$"{_storeId}:BTC"] = "shared-wallet";
        _wallets.Keys[$"{second}:BTC"] = "shared-wallet";
        var building = 0;
        var overlapped = false;
        _wallets.OnBuild = async () =>
        {
            if (Interlocked.Increment(ref building) > 1)
                overlapped = true;
            await Task.Delay(50, Ct);
            Interlocked.Decrement(ref building);
        };
        await Service().OnChainActivityAsync("BTC", Ct);
        Assert.Equal(2, _wallets.Built.Count);
        Assert.False(overlapped);
    }

    [Fact]
    public async Task APauseSavedDuringTheWatchKeepsTheConvertRequest()
    {
        Configure(convert: true);
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var awaiting = Assert.Single(State.Awaiting);
        _exchange.Deposits[awaiting.Txid] = new DepositStatus(DepositState.Credited, 0.9999m);
        _exchange.Balances["BTC"] = 0.9999m;
        _exchange.OnFindDeposit = () => _repository.Settings[_storeId].Paused = true; // saved after the watch read the settings

        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Empty(_exchange.Orders);
        Assert.True(State.ConvertRequested);

        _exchange.OnFindDeposit = null;
        _repository.Settings[_storeId].Paused = false;
        await service.WatchDepositsAsync(Ct);
        Assert.False(State.ConvertRequested);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task CreditedDepositConvertsStraightAway()
    {
        Configure(convert: true);
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var awaiting = Assert.Single(State.Awaiting);

        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting); // not seen by the exchange yet
        Assert.Empty(_exchange.Orders);
        Assert.Equal(awaiting.SentAt, _exchange.DepositSearchesSince.Single()); // deposits are searched back to the sweep

        _exchange.Deposits[awaiting.Txid] = new DepositStatus(DepositState.Credited, 0.9999m);
        _exchange.Balances["BTC"] = 0.9999m;
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.False(State.ConvertRequested);
        Assert.Contains(State.Activity, a => a.Message.Contains("credited 0.9999 BTC"));
        var sale = Assert.Single(_exchange.Orders);
        Assert.Equal(("BTC", "CAD", 0.9999m), (sale.Base, sale.Quote, sale.Amount));
    }

    [Fact]
    public async Task CreditedDepositWithConvertOffJustStopsWatching()
    {
        Configure(convert: false);
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        _exchange.Deposits[State.Awaiting[0].Txid] = new DepositStatus(DepositState.Credited, 0.9999m);
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.False(State.ConvertRequested);
        Assert.DoesNotContain("balances", _exchange.Calls);
    }

    [Fact]
    public async Task PausedStoreKeepsTheConvertRequestUntilResumed()
    {
        Configure(convert: true);
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        _exchange.Deposits[State.Awaiting[0].Txid] = new DepositStatus(DepositState.Credited, 0.9999m);
        _repository.Settings[_storeId].Paused = true;
        await service.WatchDepositsAsync(Ct);
        Assert.True(State.ConvertRequested);
        Assert.Empty(_exchange.Orders);

        _repository.Settings[_storeId].Paused = false;
        _exchange.Balances["BTC"] = 0.9999m;
        await service.WatchDepositsAsync(Ct);
        Assert.False(State.ConvertRequested);
        Assert.Single(_exchange.Orders);
    }

    [Fact]
    public async Task FailedDepositStopsTheCoinUntilTheOwnerResumesIt()
    {
        Configure();
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        var txid = State.Awaiting[0].Txid;
        _exchange.Deposits[txid] = new DepositStatus(DepositState.Failed, 0.9999m);
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("as failed or being returned", State.Activity[0].Message);
        Assert.Equal(txid, State.Stopped["BTC"].Txid);

        // The returned funds land in the hot wallet: they aren't swept straight back.
        _wallets.Balances[$"{_storeId}:BTC"] = 1m;
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Single(_wallets.Broadcasts);
        Assert.Contains("Sweeping BTC is stopped", State.Activity[0].Message);

        Assert.True(await service.ResumeCoinAsync(_storeId, "btc", Ct));
        Assert.False(await service.ResumeCoinAsync(_storeId, "BTC", Ct));
        Assert.Equal(ActivityOutcome.Changed, State.Activity[0].Outcome);
        await service.RunAsync(_storeId, wait: false, Ct);
        Assert.Equal(2, _wallets.Broadcasts.Count);
    }

    [Fact]
    public async Task UncreditedDepositsAreReportedOnlyAfterALookupWorks()
    {
        Configure();
        var service = Service();
        await service.RunAsync(_storeId, wait: false, Ct);
        _exchange.DepositError = new ExchangeException("EService:Unavailable");
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting);
        Assert.Contains("Trying again in a minute", State.Activity[0].Message);

        _clock.Advance(SweepService.DepositWatchLimit);
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting); // the exchange couldn't be checked: still watched
        Assert.Contains("Trying again in a minute", State.Activity[0].Message);

        _exchange.DepositError = null;
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("wasn't credited within 24 hours", State.Activity[0].Message);
        // No longer watched, so a later return couldn't be seen: the coin stops until the owner resumes it.
        Assert.True(State.Stopped.ContainsKey("BTC"));
    }

    void Awaiting(string asset, string txid, TimeSpan ago) => _repository.SweepStates.GetOrAdd(_storeId, _ => new SweepState()).Awaiting.Add(new AwaitingDeposit
    {
        Asset = asset, Txid = txid, Amount = 0.5m, ExchangeId = FakeExchange.ExchangeId,
        SentAt = _clock.GetUtcNow() - ago, SignedAt = _clock.GetUtcNow() - ago
    });

    [Fact]
    public async Task EachCoinsDepositsAreLookedUpTogether()
    {
        Configure();
        Awaiting("BTC", "btc-deposit-1", TimeSpan.FromHours(2));
        Awaiting("BTC", "btc-deposit-2", TimeSpan.FromHours(5));
        Awaiting("LTC", "ltc-deposit-1", TimeSpan.FromHours(1));
        _exchange.Deposits["btc-deposit-2"] = new DepositStatus(DepositState.Credited, 0.5m);
        await Service().WatchDepositsAsync(Ct);
        Assert.Equal(["BTC: btc-deposit-1, btc-deposit-2", "LTC: ltc-deposit-1"],
            _exchange.DepositLookups.Select(l => $"{l.Asset}: {string.Join(", ", l.Txids)}"));
        // From the earliest of the coin's sweeps, so none is missed.
        Assert.Equal([_clock.GetUtcNow() - TimeSpan.FromHours(5), _clock.GetUtcNow() - TimeSpan.FromHours(1)], _exchange.DepositSearchesSince.Distinct());
        Assert.Equal(["btc-deposit-1", "ltc-deposit-1"], State.Awaiting.Select(a => a.Txid));
        Assert.Contains("credited 0.5 BTC from the sweep (btc-deposit-2)", Assert.Single(State.Activity, a => a.Outcome == ActivityOutcome.Sent).Message);
    }

    [Fact]
    public async Task RateLimitedDepositCheckIsOneEntryAndStopsTheRun()
    {
        Configure();
        Awaiting("BTC", "btc-deposit-1", TimeSpan.FromHours(2));
        Awaiting("BTC", "btc-deposit-2", TimeSpan.FromHours(30));
        Awaiting("LTC", "ltc-deposit-1", TimeSpan.FromHours(30));
        _exchange.DepositError = new ExchangeException("Kraken: EAPI:Rate limit exceeded", rateLimited: true);
        await Service().WatchDepositsAsync(Ct);
        Assert.Single(_exchange.DepositLookups); // the other coin isn't asked for, to be refused too
        var entry = Assert.Single(State.Activity);
        Assert.Equal(ActivityOutcome.Failed, entry.Outcome);
        Assert.Contains("Fake Exchange is limiting requests (Kraken: EAPI:Rate limit exceeded), so 3 deposits waiting weren't checked", entry.Message);
        // Not checked, so none counts as uncredited, however old.
        Assert.Equal(3, State.Awaiting.Count);
        Assert.Empty(State.Stopped);
    }

    [Fact]
    public async Task RateLimitAfterACoinWasCheckedCountsOnlyTheRest()
    {
        Configure();
        Awaiting("BTC", "btc-deposit-1", TimeSpan.FromHours(2));
        Awaiting("LTC", "ltc-deposit-1", TimeSpan.FromHours(1));
        _exchange.Deposits["btc-deposit-1"] = new DepositStatus(DepositState.Credited, 0.5m);
        _exchange.OnFindDeposit = () =>
        {
            if (_exchange.DepositLookups.Count == 2)
                _exchange.DepositError = new ExchangeException("Kraken: EAPI:Rate limit exceeded", rateLimited: true);
        };
        await Service().WatchDepositsAsync(Ct);
        Assert.Equal("ltc-deposit-1", Assert.Single(State.Awaiting).Txid);
        Assert.Contains(State.Activity, a => a.Message.EndsWith("so the deposit waiting wasn't checked. Trying again in a minute."));
        Assert.Contains(State.Activity, a => a.Outcome == ActivityOutcome.Sent && a.Message.Contains("btc-deposit-1"));
    }

    [Fact]
    public async Task BumpedSweepIsFollowedToItsReplacement()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(23));
        var signedAt = State.Awaiting[0].SignedAt;
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        var awaiting = Assert.Single(State.Awaiting);
        Assert.Equal("bumped-tx", awaiting.Txid);
        Assert.Equal(ActivityOutcome.Changed, State.Activity[0].Outcome);
        Assert.Contains("(sweep-tx) was replaced by bumped-tx", State.Activity[0].Message);
        // The new transaction gets a full watch, searched for from when the first was signed.
        Assert.Equal(_clock.GetUtcNow(), awaiting.SentAt);
        Assert.Equal(signedAt, awaiting.SignedAt);

        _clock.Advance(TimeSpan.FromHours(23));
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting); // past 24 hours since the first was sent, but not since it was replaced
        Assert.Empty(State.Stopped);
        Assert.Equal(signedAt, _exchange.DepositSearchesSince[^1]);

        _exchange.Deposits["bumped-tx"] = new DepositStatus(DepositState.Credited, 0.4999m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("credited 0.4999 BTC from the sweep (bumped-tx)", State.Activity[0].Message);
        Assert.Empty(State.Stopped);
    }

    [Fact]
    public async Task FailedReplacementCheckIsShownOnceAndNeverTimesTheDepositOut()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.ReplacementError = new HttpRequestException("explorer unreachable");
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        var entry = Assert.Single(State.Activity); // shown as soon as it fails
        Assert.Equal(ActivityOutcome.Failed, entry.Outcome);
        Assert.Contains("Could not check whether the 0.5 BTC sweep (sweep-tx) was replaced: explorer unreachable", entry.Message);

        var firstAt = entry.At;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Equal(firstAt, Assert.Single(State.Activity).At); // not again every minute while it keeps failing

        _clock.Advance(SweepService.DepositWatchLimit);
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Awaiting); // not known whether it was replaced: still watched
        Assert.Empty(State.Stopped);
        Assert.Single(State.Activity);

        _wallets.ReplacementError = null;
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("wasn't credited within 24 hours", State.Activity[0].Message);
    }

    [Fact]
    public async Task ReplacementCheckFailingAgainIsShownAgain()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.ReplacementError = new HttpRequestException("explorer unreachable");
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        _wallets.ReplacementError = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.False(Assert.Single(State.Awaiting).ReplacementCheckFailing);
        _wallets.ReplacementError = new HttpRequestException("explorer unreachable");
        _clock.Advance(SweepService.ReplacementCheckInterval);
        await service.WatchDepositsAsync(Ct);
        // Shown again (a repeat of the latest entry replaces it with the new time).
        Assert.StartsWith("Could not check whether", State.Activity[0].Message);
        Assert.Equal(_clock.GetUtcNow(), State.Activity[0].At);
    }

    [Fact]
    public async Task SweepTheExchangeShowsPendingIsFollowedToAReplacement()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Pending, 0.5m);
        await Service().WatchDepositsAsync(Ct);
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid); // the replaced one will never confirm
    }

    [Fact]
    public async Task SweepTheExchangeShowsFailedIsFollowedToAReplacementAndTheCoinStopped()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Failed, 0.5m);
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid); // the new one may still be credited
        Assert.Equal("sweep-tx", State.Stopped["BTC"].Txid); // the failed one may be mined and returned
        Assert.Contains(State.Activity, a => a.Message.Contains("reports sweep-tx, replaced by a later transaction"));

        _exchange.Deposits["bumped-tx"] = new DepositStatus(DepositState.Credited, 0.4999m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("credited 0.4999 BTC from the sweep (bumped-tx)", State.Activity[0].Message);
    }

    [Fact]
    public async Task ReplacedSweepTheExchangeReportsFailedLaterStopsTheCoin()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Stopped);

        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Failed, 0.5m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Equal("sweep-tx", State.Stopped["BTC"].Txid);
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid);
        var entries = State.Activity.Count;

        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Equal(entries, State.Activity.Count); // stopped once, not every minute
    }

    [Fact]
    public async Task EveryTransactionOfAReplacementChainIsLookedFor()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bump-1";
        _wallets.Replacements["bump-1"] = "bump-2";
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        var awaiting = Assert.Single(State.Awaiting);
        Assert.Equal("bump-2", awaiting.Txid);
        Assert.Equal(["sweep-tx", "bump-1"], awaiting.ReplacedTxids);

        _exchange.Deposits["bump-1"] = new DepositStatus(DepositState.Credited, 0.4999m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("credited 0.4999 BTC from the sweep (bump-1)", State.Activity[0].Message);
    }

    [Fact]
    public async Task FailedDepositThatWasNotReplacedStopsTheCoin()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Failed, 0.5m);
        await Service().WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.True(State.Stopped.ContainsKey("BTC"));
    }

    [Fact]
    public async Task ReplacedSweepAMinerConfirmedAnywayIsStillCredited()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid);

        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Credited, 0.4999m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Equal(["bumped-tx", "sweep-tx"], _exchange.DepositLookups[^1].Txids);
        Assert.Empty(State.Awaiting);
        Assert.Contains("credited 0.4999 BTC from the sweep (sweep-tx)", State.Activity[0].Message);
        Assert.Empty(State.Stopped);
    }

    [Fact]
    public async Task ReplacementsAreCheckedEveryFewMinutesNotEveryMinute()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        var service = Service();
        for (var minute = 0; minute < 10; minute++)
        {
            await service.WatchDepositsAsync(Ct);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(1, _wallets.ReplacementChecks);
        await service.WatchDepositsAsync(Ct);
        Assert.Equal(2, _wallets.ReplacementChecks);
        Assert.Single(State.Awaiting);
    }

    [Fact]
    public async Task ReplacementIsCheckedBeforeAFailureStopsTheCoin()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Failed, 0.5m);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct); // within the interval, but a failure is checked straight away
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid);
    }

    [Fact]
    public async Task ReplacementIsCheckedBeforeTheWatchEnds()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", SweepService.DepositWatchLimit - TimeSpan.FromMinutes(1));
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct); // within the interval, but the watch is ending
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid);
        Assert.Empty(State.Stopped);
    }

    [Fact]
    public async Task FailedDepositStopsTheCoinWhileTheReplacementCheckFails()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Failed, 0.5m);
        _wallets.ReplacementError = new HttpRequestException("explorer unreachable");
        var service = Service();
        await service.WatchDepositsAsync(Ct);
        Assert.True(State.Stopped.ContainsKey("BTC")); // no sweeping back what the exchange may be returning
        Assert.Single(State.Awaiting); // still watched: it may have been replaced
        Assert.Contains("couldn't say whether the sweep was replaced: explorer unreachable", Assert.Single(State.Activity).Message);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Single(State.Activity); // not again every minute

        _wallets.ReplacementError = null;
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.WatchDepositsAsync(Ct);
        Assert.Equal("bumped-tx", Assert.Single(State.Awaiting).Txid);
        Assert.True(State.Stopped.ContainsKey("BTC")); // only the owner resumes it
    }

    [Fact]
    public async Task SweepTheExchangeCreditedIsNotFollowedToAReplacement()
    {
        Configure();
        Awaiting("BTC", "sweep-tx", TimeSpan.FromHours(1));
        _wallets.Replacements["sweep-tx"] = "bumped-tx";
        _exchange.Deposits["sweep-tx"] = new DepositStatus(DepositState.Credited, 0.5m);
        await Service().WatchDepositsAsync(Ct);
        Assert.Empty(State.Awaiting);
        Assert.Contains("credited 0.5 BTC from the sweep (sweep-tx)", State.Activity[0].Message);
    }

    [Fact]
    public async Task FailedCheckOfSeveralDepositsIsOneEntryPerCoin()
    {
        Configure();
        Awaiting("BTC", "btc-deposit-1", TimeSpan.FromHours(2));
        Awaiting("BTC", "btc-deposit-2", TimeSpan.FromHours(3));
        Awaiting("LTC", "ltc-deposit-1", TimeSpan.FromHours(1));
        _exchange.DepositError = new ExchangeException("EService:Unavailable");
        await Service().WatchDepositsAsync(Ct);
        Assert.Equal(2, _exchange.DepositLookups.Count); // not a rate limit: the next coin is still tried
        Assert.Equal(["Could not check the 0.5 LTC deposit (ltc-deposit-1) on Fake Exchange: EService:Unavailable. Trying again in a minute.",
                "Could not check the 2 BTC deposits waiting on Fake Exchange: EService:Unavailable. Trying again in a minute."],
            State.Activity.Select(a => a.Message));
        Assert.Equal(3, State.Awaiting.Count);
    }

    [Fact]
    public async Task ColdWalletChangesAreWrittenToTheActivity()
    {
        var before = new SweepSettings { Rules = [new SweepRule { CryptoCode = "BTC", ColdPercent = 10, ColdDestination = "cold-address" }] };
        var after = new SweepSettings
        {
            Rules =
            [
                new SweepRule { CryptoCode = "BTC", ColdPercent = 10, ColdDestination = "xpub-other", ColdStartIndex = 3 },
                new SweepRule { CryptoCode = "LTC", MinimumBalance = 1 } // no cold wallet before or after: not a change
            ]
        };
        var service = Service();
        await service.RecordDestinationChangesAsync(_storeId, before, after, Ct);
        var entry = Assert.Single(State.Activity);
        Assert.Equal(("BTC", ActivityOutcome.Changed), (entry.Asset, entry.Outcome));
        Assert.Equal("Cold wallet changed from 10% to cold-address to 10% to xpub-other from address 3.", entry.Message);

        await service.RecordDestinationChangesAsync(_storeId, after, after, Ct);
        Assert.Single(State.Activity);

        // A rule left out of a save (e.g. a stale form) takes its cold wallet with it.
        await service.RecordDestinationChangesAsync(_storeId, after, new SweepSettings { Rules = [after.Rules[1]] }, Ct);
        Assert.Equal(("BTC", "Cold wallet changed from 10% to xpub-other from address 3 to none."), (State.Activity[0].Asset, State.Activity[0].Message));
    }

    [Fact]
    public void WalletEventsNameTheirCoin()
    {
        Assert.Equal("BTC", SweepWalletListener.CryptoCode(new BTCPayServer.Payments.PaymentMethodId("BTC-CHAIN")));
        Assert.Equal("LTC", SweepWalletListener.CryptoCode(new BTCPayServer.Payments.PaymentMethodId("LTC-CHAIN")));
        Assert.Null(SweepWalletListener.CryptoCode(new BTCPayServer.Payments.PaymentMethodId("BTC-LN")));
        Assert.Null(SweepWalletListener.CryptoCode(null));
    }

    [Fact]
    public async Task BurstsOfWalletEventsCoalesce()
    {
        var runs = new List<string>();
        var first = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var queue = new CoalescingQueue(async (key, _) =>
        {
            lock (runs)
                runs.Add(key);
            first.TrySetResult();
            await release.Task;
        }, NullLogger.Instance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = queue.RunAsync(cts.Token);

        queue.Signal("BTC");
        await first.Task;
        // Signalled while BTC is running: BTC runs once more afterwards, LTC once.
        queue.Signal("BTC");
        queue.Signal("btc");
        queue.Signal("LTC");
        queue.Signal("BTC");
        release.SetResult();
        while (true)
        {
            lock (runs)
                if (runs.Count >= 3)
                    break;
            await Task.Delay(10, Ct);
        }
        await Task.Delay(50, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["BTC", "BTC", "LTC"], runs);
    }

    [Fact]
    public async Task KeysWaitingForASlotStillCoalesce()
    {
        var runs = new List<string>();
        var first = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var queue = new CoalescingQueue(async (key, _) =>
        {
            lock (runs)
                runs.Add(key);
            if (key == "A")
            {
                first.TrySetResult();
                await release.Task;
            }
        }, NullLogger.Instance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = queue.RunAsync(cts.Token);

        queue.Signal("A");
        await first.Task;
        queue.Signal("B");
        await Task.Delay(50, Ct); // B is read and waits for the only slot
        queue.Signal("B");
        queue.Signal("B");
        release.SetResult();
        while (true)
        {
            lock (runs)
                if (runs.Count >= 2)
                    break;
            await Task.Delay(10, Ct);
        }
        await Task.Delay(50, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["A", "B"], runs);
    }

    [Fact]
    public async Task DifferentKeysRunSideBySideButAKeyNeverRunsTwiceAtOnce()
    {
        var runs = new List<string>();
        var concurrentA = 0;
        var maxConcurrentA = 0;
        var aStarted = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var queue = new CoalescingQueue(async (key, _) =>
        {
            lock (runs)
                runs.Add(key);
            if (key != "A")
                return;
            var now = Interlocked.Increment(ref concurrentA);
            lock (runs)
                maxConcurrentA = Math.Max(maxConcurrentA, now);
            aStarted.TrySetResult();
            await releaseA.Task;
            Interlocked.Decrement(ref concurrentA);
        }, NullLogger.Instance, maxConcurrency: 2, StringComparer.Ordinal);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = queue.RunAsync(cts.Token);

        queue.Signal("A");
        await aStarted.Task;
        queue.Signal("A"); // while A runs: runs again after it, never beside it
        queue.Signal("B");
        queue.Signal("b"); // compared exactly, so another key
        async Task WaitFor(int count)
        {
            while (true)
            {
                lock (runs)
                    if (runs.Count >= count)
                        return;
                await Task.Delay(10, Ct);
            }
        }
        await WaitFor(3); // B and b ran while A was still running
        lock (runs)
            Assert.Equal(["A", "B", "b"], runs);
        releaseA.SetResult();
        await WaitFor(4);
        await Task.Delay(50, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(["A", "B", "b", "A"], runs);
        Assert.Equal(1, maxConcurrentA);
    }
}
