#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

/// <summary>
/// Every minute: retries BTC still waiting to be sold and looks up pending orders. Most hedges start earlier, from the
/// invoice event (<see cref="HedgeInvoiceListener"/>).
/// </summary>
public class HedgeTask(HedgeService hedge) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken) => hedge.RunDueAsync(cancellationToken);
}
