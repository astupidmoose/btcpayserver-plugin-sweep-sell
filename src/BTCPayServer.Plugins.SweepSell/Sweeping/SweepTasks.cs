#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// The scheduled backstop, checked every minute; each store runs when its own interval has passed. Most sweeps start
/// earlier, from wallet activity (<see cref="SweepWalletListener"/>).
/// </summary>
public class SweepTask(SweepService sweep) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken) => sweep.RunDueAsync(cancellationToken);
}

/// <summary>Checks sweeps sent to the exchange every minute, and converts as soon as one is credited.</summary>
public class DepositWatchTask(SweepService sweep) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken) => sweep.WatchDepositsAsync(cancellationToken);
}
