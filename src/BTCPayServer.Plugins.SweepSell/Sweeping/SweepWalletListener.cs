#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// Starts a sweep check as soon as an on-chain wallet sees a transaction or a block is found, instead of waiting for the
/// schedule. The events only say which coin had activity; the sweep run reads balances from the wallet itself.
/// </summary>
public sealed class SweepWalletListener : IHostedService, IDisposable
{
    readonly EventAggregator _events;
    readonly CoalescingQueue _queue;
    readonly CancellationTokenSource _stopping = new();
    IEventAggregatorSubscription? _transactions;
    IEventAggregatorSubscription? _blocks;
    Task _running = Task.CompletedTask;

    public SweepWalletListener(EventAggregator events, SweepService sweep, ILogger<SweepWalletListener> logger)
    {
        _events = events;
        _queue = new CoalescingQueue(sweep.OnChainActivityAsync, logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _transactions = _events.Subscribe<NewOnChainTransactionEvent>(e => Signal(e.PaymentMethodId));
        _blocks = _events.Subscribe<NewBlockEvent>(e => Signal(e.PaymentMethodId));
        _running = Task.Run(() => _queue.RunAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    void Signal(PaymentMethodId? paymentMethodId)
    {
        if (CryptoCode(paymentMethodId) is { } code)
            _queue.Signal(code);
    }

    /// <summary>The coin of an on-chain payment method ("BTC-CHAIN" → "BTC"); null for anything else.</summary>
    public static string? CryptoCode(PaymentMethodId? paymentMethodId)
    {
        var id = paymentMethodId?.ToString();
        const string suffix = "-CHAIN";
        return id is not null && id.Length > suffix.Length && id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? id[..^suffix.Length]
            : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _transactions?.Dispose();
        _blocks?.Dispose();
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
