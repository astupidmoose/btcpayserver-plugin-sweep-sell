#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Plugins.SweepSell.Sweeping;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

/// <summary>
/// Hedges an invoice as soon as it is paid in full or settled. Invoice events come from inside BTCPay, not from a
/// webhook; the event only names the invoice, which the hedge job reads again from the database.
/// </summary>
public sealed class HedgeInvoiceListener : IHostedService, IDisposable
{
    readonly EventAggregator _events;
    readonly CoalescingQueue _queue;
    readonly CancellationTokenSource _stopping = new();
    IEventAggregatorSubscription? _subscription;
    Task _running = Task.CompletedTask;

    public HedgeInvoiceListener(EventAggregator events, HedgeService hedge, ILogger<HedgeInvoiceListener> logger)
    {
        _events = events;
        _queue = new CoalescingQueue(hedge.OnInvoiceAsync, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.Subscribe<InvoiceEvent>(e =>
        {
            if (Triggers(e.EventCode))
                _queue.Signal(e.InvoiceId);
        });
        _running = Task.Run(() => _queue.RunAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    /// <summary>The events after which an invoice can be processing or settled.</summary>
    public static bool Triggers(InvoiceEventCode code) => code is InvoiceEventCode.PaidInFull or InvoiceEventCode.Confirmed or
        InvoiceEventCode.Completed or InvoiceEventCode.MarkedCompleted or InvoiceEventCode.PaidAfterExpiration or InvoiceEventCode.PaymentSettled;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        await _stopping.CancelAsync();
        try
        {
            await _running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _stopping.Dispose();
}
