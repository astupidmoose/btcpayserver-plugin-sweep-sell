#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// Runs work for keys one at a time, in the order they were signalled. A key signalled again before its work starts
/// is run once, so a burst of wallet events (a block confirming many transactions) causes one sweep check, not many.
/// A key signalled while its work is running is run again afterwards, so nothing signalled is missed.
/// </summary>
public sealed class CoalescingQueue(Func<string, CancellationToken, Task> work, ILogger logger)
{
    readonly Channel<string> _keys = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);

    public void Signal(string key)
    {
        if (_queued.TryAdd(key, 0))
            _keys.Writer.TryWrite(key);
    }

    /// <summary>Works through the signalled keys until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var key in _keys.Reader.ReadAllAsync(cancellationToken))
        {
            // Removed before the work starts: a signal during the work queues the key again.
            _queued.TryRemove(key, out _);
            try
            {
                await work(key, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "Sweep & Sell: handling wallet activity for {Key} failed", key);
            }
        }
    }
}
