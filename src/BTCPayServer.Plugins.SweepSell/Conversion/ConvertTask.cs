#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.HostedServices;

namespace BTCPayServer.Plugins.SweepSell.Conversion;

/// <summary>Checked every minute; each store runs when its own interval has passed.</summary>
public class ConvertTask(ConvertService convert) : IPeriodicTask
{
    public Task Do(CancellationToken cancellationToken) => convert.RunDueAsync(cancellationToken);
}
