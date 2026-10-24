#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.SweepSell.Payout;

/// <summary>Checked every minute; each store runs when its own interval has passed.</summary>
public class PayoutTask(PayoutService payout) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken) => payout.RunDueAsync(cancellationToken);
}
