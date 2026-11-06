#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// One lock per exchange and asset (as the exchange names it), shared by every store on the server. Each live sale of the
/// asset holds it shared, so stores' sales never wait on each other; a payout that waits on a reserve of the asset holds
/// it alone, from reading the reserve until the withdrawal is sent, so no store's sale of the reserve lands in between.
/// Always taken after the store's <see cref="SettingsGate"/>, and nothing else is locked while it is held.
/// </summary>
public static class AssetGate
{
    static readonly ConcurrentDictionary<(string, string), AssetLock> Locks = new();

    public static AssetLock For(string exchangeId, string assetKey) => Locks.GetOrAdd((exchangeId, assetKey), _ => new AssetLock());
}

public sealed class AssetLock
{
    readonly SemaphoreSlim _count = new(1, 1);
    readonly SemaphoreSlim _exclusive = new(1, 1);
    int _sales;

    /// <summary>Whether a sale or a reserve payout holds the lock.</summary>
    public bool Held => _exclusive.CurrentCount == 0;

    /// <summary>Held by a sale: other sales go ahead, a reserve payout waits for every sale to end.</summary>
    public async Task<IDisposable> SaleAsync(CancellationToken cancellationToken)
    {
        await _count.WaitAsync(cancellationToken);
        try
        {
            if (_sales == 0)
                await _exclusive.WaitAsync(cancellationToken);
            _sales++;
        }
        finally
        {
            _count.Release();
        }
        return new Releaser(EndSale);
    }

    /// <summary>Held by a reserve payout alone: no sale starts until it is released.</summary>
    public async Task<IDisposable> ReserveAsync(CancellationToken cancellationToken)
    {
        await _exclusive.WaitAsync(cancellationToken);
        return new Releaser(() => _exclusive.Release());
    }

    void EndSale()
    {
        _count.Wait();
        try
        {
            if (--_sales == 0)
                _exclusive.Release();
        }
        finally
        {
            _count.Release();
        }
    }

    sealed class Releaser(Action release) : IDisposable
    {
        int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                release();
        }
    }
}
