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

    /// <summary>UTC day (yyyy-MM-dd) that <see cref="Sold"/> counts, for the daily cap.</summary>
    public string? SoldDay { get; set; }

    /// <summary>
    /// Value sold on <see cref="SoldDay"/>, by the asset it was sold for and valued at the 24-hour average when each order
    /// was sent. Kept per asset because the cap is in the target asset, and the target can change during a day.
    /// </summary>
    public Dictionary<string, decimal> Sold { get; set; } = new();

    public decimal SoldOn(DateTimeOffset now, string asset) => SoldDay == Day(now) ? Sold.GetValueOrDefault(asset) : 0;

    public void AddSold(DateTimeOffset now, string asset, decimal value)
    {
        if (SoldDay != Day(now))
        {
            SoldDay = Day(now);
            Sold.Clear();
        }
        Sold[asset] = Sold.GetValueOrDefault(asset) + value;
    }

    static string Day(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds an entry. A repeat of the latest entry of the same asset (same outcome and message) replaces it instead, so
    /// several coins waiting run after run don't push sales and failures out of the list.
    /// </summary>
    public void Add(ActivityEntry entry)
    {
        var latest = Activity.FirstOrDefault(a => a.Asset == entry.Asset);
        if (latest is not null && latest.Outcome == entry.Outcome && latest.Message == entry.Message)
            Activity.Remove(latest);
        Activity.Insert(0, entry);
        if (Activity.Count > MaxActivity)
            Activity.RemoveRange(MaxActivity, Activity.Count - MaxActivity);
    }
}

public class PendingOrder
{
    public Guid ClientOrderId { get; set; }

    /// <summary>The exchange and key the order was sent with: a client order ID only means something on that account.</summary>
    public string? ExchangeId { get; set; }

    public string? KeyFingerprint { get; set; }

    public string Asset { get; set; } = "";
    public string TargetAsset { get; set; } = "";
    public decimal Amount { get; set; }

    /// <summary>24-hour average price when sent, used to count the sale against the daily cap.</summary>
    public decimal ReferencePrice { get; set; }

    public DateTimeOffset SentAt { get; set; }

    /// <summary>The exchange's IDs once it confirmed the order. A confirmed order exists, so it is never treated as unplaced.</summary>
    public List<string> OrderIds { get; set; } = [];
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
