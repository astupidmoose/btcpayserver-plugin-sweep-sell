#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Plugins.SweepSell.Sweeping;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

/// <summary>
/// Hedges an invoice as soon as it is paid in full or settled. Invoice events come from inside BTCPay, not from a
/// webhook; the event only names the invoice, which the hedge job reads again from the database. Stores are handled
/// side by side (each one's invoices in turn), so one store waiting on a slow exchange doesn't hold up another's hedges.
/// </summary>
public sealed class HedgeInvoiceListener : IHostedService, IDisposable
{
    readonly EventAggregator _events;
    readonly HedgeService _hedge;
    readonly CoalescingQueue _queue;
    /// <summary>The invoices signalled for each store and not handled yet.</summary>
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _signalled = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _stopping = new();
    IEventAggregatorSubscription? _subscription;
    Task _running = Task.CompletedTask;

    public HedgeInvoiceListener(EventAggregator events, HedgeService hedge, ILogger<HedgeInvoiceListener> logger)
    {
        _events = events;
        _hedge = hedge;
        _queue = new CoalescingQueue(OnStoreAsync, logger, HedgeService.MaxParallelStores, StringComparer.Ordinal);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.Subscribe<InvoiceEvent>(e =>
        {
            if (Triggers(e.EventCode) && e.Invoice?.StoreId is { } storeId)
                Signal(storeId, e.InvoiceId);
        });
        _running = Task.Run(() => RunAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    /// <summary>Queues <paramref name="invoiceId"/> to be hedged with the rest of its store's signalled invoices.</summary>
    public void Signal(string storeId, string invoiceId)
    {
        _signalled.GetOrAdd(storeId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal)).TryAdd(invoiceId, 0);
        _queue.Signal(storeId);
    }

    /// <summary>Runs the queue until <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => _queue.RunAsync(cancellationToken);

    Task OnStoreAsync(string storeId, CancellationToken cancellationToken)
    {
        // Taken off before the run: one signalled during it queues the store again.
        var invoiceIds = _signalled.TryGetValue(storeId, out var signalled)
            ? signalled.Keys.Where(id => signalled.TryRemove(id, out _)).ToList()
            : new List<string>();
        return invoiceIds.Count == 0 ? Task.CompletedTask : _hedge.OnInvoicesAsync(storeId, invoiceIds, cancellationToken);
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
