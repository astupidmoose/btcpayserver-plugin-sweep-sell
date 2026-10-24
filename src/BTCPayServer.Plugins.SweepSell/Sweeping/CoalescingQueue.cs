#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>
/// Runs work for keys in the order they were signalled, up to <paramref name="maxConcurrency"/> keys at a time and never
/// the same key twice at once. A key signalled again before its work starts is run once, so a burst of wallet events (a
/// block confirming many transactions) causes one sweep check, not many. A key signalled while its work is running is
/// run again afterwards, so nothing signalled is missed. Keys are compared with <paramref name="comparer"/>, ignoring
/// case when none is given.
/// </summary>
public sealed class CoalescingQueue(Func<string, CancellationToken, Task> work, ILogger logger, int maxConcurrency = 1,
    IEqualityComparer<string>? comparer = null)
{
    readonly Channel<string> _keys = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<string, byte> _queued = new(comparer ?? StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _running = new(comparer ?? StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _again = new(comparer ?? StringComparer.OrdinalIgnoreCase);

    public void Signal(string key)
    {
        if (_queued.TryAdd(key, 0))
            _keys.Writer.TryWrite(key);
    }

    /// <summary>Works through the signalled keys until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (maxConcurrency < 1)
            throw new InvalidOperationException("A coalescing queue needs to run at least one key at a time.");
        using var slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new List<Task>();
        try
        {
            await foreach (var key in _keys.Reader.ReadAllAsync(cancellationToken))
            {
                // Removed before the work starts: a signal during the work queues the key again.
                _queued.TryRemove(key, out _);
                // A slot first: when the key's earlier work is the one finishing, it then runs again straight away, in order.
                await slots.WaitAsync(cancellationToken);
                lock (_running)
                {
                    if (!_running.Add(key))
                    {
                        // Still running in another slot: run again once that finishes.
                        _again.Add(key);
                        slots.Release();
                        continue;
                    }
                }
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(Task.Run(() => RunOneAsync(key, slots, cancellationToken), CancellationToken.None));
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    async Task RunOneAsync(string key, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try
        {
            await work(key, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(e, "Sweep & Sell: handling activity for {Key} failed", key);
        }
        finally
        {
            bool again;
            lock (_running)
            {
                _running.Remove(key);
                again = _again.Remove(key);
            }
            if (again)
                Signal(key);
            slots.Release();
        }
    }
}
