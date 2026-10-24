#nullable enable
using System.Collections.Concurrent;
using System.Threading;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// One lock per store, shared by settings saves and every money-moving step (sending an order, broadcasting a sweep).
/// A job re-reads the saved settings under it before acting, so a save (e.g. a pause) applies before anything more is
/// sent, and a save waits at most for one such step.
/// </summary>
public static class SettingsGate
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public static SemaphoreSlim For(string storeId) => Locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
}
