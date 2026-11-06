using BTCPayServer.Plugins.SweepSell.Settings;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class AssetGateTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SalesRunTogetherAndAReservePayoutWaitsForThemAll()
    {
        var gate = new AssetLock();
        var first = await gate.SaleAsync(Ct);
        var second = await gate.SaleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct); // a second store's sale isn't held up
        var reserve = gate.ReserveAsync(Ct);
        first.Dispose();
        await Task.Delay(50, Ct);
        Assert.False(reserve.IsCompleted); // one sale still running
        second.Dispose();
        var held = await reserve.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var sale = gate.SaleAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(sale.IsCompleted); // no sale starts while the reserve payout holds it
        held.Dispose();
        held.Dispose(); // a second release does nothing
        (await sale.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task AWaitingReservePayoutGoesBeforeLaterSales()
    {
        var gate = new AssetLock();
        var first = await gate.SaleAsync(Ct);
        var reserve = gate.ReserveAsync(Ct);
        await Task.Delay(50, Ct);
        var later = gate.SaleAsync(Ct); // overlapping sales can't hold the payout off forever
        await Task.Delay(50, Ct);
        Assert.False(later.IsCompleted);
        first.Dispose();
        var held = await reserve.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(later.IsCompleted);
        held.Dispose();
        (await later.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task ASaleWhoseOutcomeIsUnknownHoldsTheLockUntilItCanNoLongerExecute()
    {
        var gate = new AssetLock();
        AssetGate.ReleaseAfter(await gate.SaleAsync(Ct), TimeSpan.FromMilliseconds(300));
        var reserve = gate.ReserveAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(reserve.IsCompleted);
        (await reserve.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();

        AssetGate.ReleaseAfter(await gate.SaleAsync(Ct), TimeSpan.Zero); // past its deadline already
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheLockUsable()
    {
        var gate = new AssetLock();
        var held = await gate.ReserveAsync(Ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.SaleAsync(cts.Token));
        held.Dispose();
        (await gate.SaleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        Assert.False(gate.Held);

        // A reserve payout that stops waiting doesn't keep later sales out.
        var sale = await gate.SaleAsync(Ct);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ReserveAsync(cts2.Token));
        (await gate.SaleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
        sale.Dispose();
        Assert.False(gate.Held);
    }

    [Fact]
    public void EachExchangeAndAssetHasItsOwnLock()
    {
        Assert.Same(AssetGate.For("test-exchange", "USDC"), AssetGate.For("test-exchange", "USDC"));
        Assert.NotSame(AssetGate.For("test-exchange", "USDC"), AssetGate.For("test-exchange", "USD"));
        Assert.NotSame(AssetGate.For("test-exchange", "USDC"), AssetGate.For("test-exchange-2", "USDC"));
    }
}
