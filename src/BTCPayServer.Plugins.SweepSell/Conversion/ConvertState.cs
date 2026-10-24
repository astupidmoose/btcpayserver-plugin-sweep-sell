#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.SweepSell.Conversion;

/// <summary>What the convert job of one store has done and is waiting on, saved as a store setting.</summary>
public class ConvertState
{
    public const int MaxActivity = 50;

    public DateTimeOffset? LastRun { get; set; }

    /// <summary>
    /// Orders sent whose outcome isn't known yet (e.g. the response was lost). Each is saved before it is sent,
    /// and nothing more is sold until every one is found on the exchange or known not to exist.
    /// </summary>
    public List<PendingOrder> Pending { get; set; } = [];

    /// <summary>Newest first.</summary>
    public List<ActivityEntry> Activity { get; set; } = [];

    /// <summary>Adds an entry; a repeat of the latest one (same asset, outcome and message) only refreshes its time.</summary>
    public void Add(ActivityEntry entry)
    {
        var latest = Activity.FirstOrDefault();
        if (latest is not null && latest.Asset == entry.Asset && latest.Outcome == entry.Outcome && latest.Message == entry.Message)
        {
            latest.At = entry.At;
            return;
        }
        Activity.Insert(0, entry);
        if (Activity.Count > MaxActivity)
            Activity.RemoveRange(MaxActivity, Activity.Count - MaxActivity);
    }
}

public class PendingOrder
{
    public Guid ClientOrderId { get; set; }
    public string Asset { get; set; } = "";
    public string TargetAsset { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

public enum ActivityOutcome
{
    Sold,
    DryRun,
    Waiting,
    Failed
}

public class ActivityEntry
{
    public DateTimeOffset At { get; set; }
    public string? Asset { get; set; }
    public ActivityOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
}
