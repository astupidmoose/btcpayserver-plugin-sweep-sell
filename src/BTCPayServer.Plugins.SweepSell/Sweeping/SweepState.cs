#nullable enable
using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.SweepSell.Conversion;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

/// <summary>What the sweep job of one store has done and is waiting on, saved as a store setting.</summary>
public class SweepState
{
    public const int MaxActivity = 50;

    public DateTimeOffset? LastRun { get; set; }

    /// <summary>
    /// Signed sweeps whose broadcast hasn't been confirmed. Each is saved before it is broadcast; until it is seen on the
    /// network or known to be rejected, its coin isn't swept again, and only the same transaction is rebroadcast.
    /// </summary>
    public List<PendingSweep> Pending { get; set; } = [];

    /// <summary>Sweeps sent to the exchange, watched until the exchange credits them; then the store converts straight away.</summary>
    public List<AwaitingDeposit> Awaiting { get; set; } = [];

    /// <summary>A deposit was credited but the convert run couldn't start yet (e.g. one was in progress); retried every minute.</summary>
    public bool ConvertRequested { get; set; }

    /// <summary>Next receive address index per cold wallet extended public key, so every sweep gets a new address.</summary>
    public Dictionary<string, int> NextColdIndex { get; set; } = new();

    /// <summary>Coin swept per UTC day, by coin, for the daily caps.</summary>
    public DailyTotals Swept { get; set; } = new();

    /// <summary>Newest first.</summary>
    public List<ActivityEntry> Activity { get; set; } = [];

    public void Add(ActivityEntry entry) => ActivityLog.Add(Activity, entry, MaxActivity);
}

public class PendingSweep
{
    public string CryptoCode { get; set; } = "";
    public string Txid { get; set; } = "";

    /// <summary>The signed transaction. Rebroadcasting it is always safe: it can only ever be mined once.</summary>
    public string Hex { get; set; } = "";

    public string? ExchangeId { get; set; }
    public string ExchangeAddress { get; set; } = "";
    public decimal ToExchange { get; set; }
    public string? ColdAddress { get; set; }
    public decimal ToCold { get; set; }
    public decimal Fee { get; set; }

    /// <summary>The cold wallet key and index the cold address came from, to move past it once the sweep is sent.</summary>
    public string? ColdDestination { get; set; }

    public int? ColdIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public decimal Spent => ToExchange + ToCold + Fee;
}

public class AwaitingDeposit
{
    public string Asset { get; set; } = "";
    public string Txid { get; set; } = "";
    public decimal Amount { get; set; }

    /// <summary>The exchange the sweep went to: its deposits are only looked up there.</summary>
    public string? ExchangeId { get; set; }

    public DateTimeOffset SentAt { get; set; }
}

/// <summary>Totals per key for one UTC day; a total from an earlier day never counts against a later one.</summary>
public class DailyTotals
{
    /// <summary>The UTC day (yyyy-MM-dd) <see cref="Totals"/> counts.</summary>
    public string? Day { get; set; }

    public Dictionary<string, decimal> Totals { get; set; } = new();

    public decimal On(DateTimeOffset at, string key) => Day == DayOf(at) ? Totals.GetValueOrDefault(key) : 0;

    public void Add(DateTimeOffset at, string key, decimal value)
    {
        var day = DayOf(at);
        if (Day is not null && string.CompareOrdinal(day, Day) < 0)
            return;
        if (Day != day)
        {
            Day = day;
            Totals.Clear();
        }
        Totals[key] = Totals.GetValueOrDefault(key) + value;
    }

    static string DayOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
