#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.SweepSell.Conversion;

namespace BTCPayServer.Plugins.SweepSell.Payout;

/// <summary>What the payout job of one store has done and is waiting on, saved as a store setting.</summary>
public class PayoutState
{
    public const int MaxActivity = 50;
    public const int MaxKnown = 100;

    public DateTimeOffset? LastRun { get; set; }

    /// <summary>
    /// Withdrawals sent whose outcome isn't known (the response was lost). Each is saved before it is sent, and nothing
    /// more is withdrawn until it is found in the exchange's withdrawals or the owner removes it.
    /// </summary>
    public List<PendingWithdrawal> Pending { get; set; } = [];

    /// <summary>
    /// The exchange references of withdrawals already accounted for (newest first), so a lost withdrawal is never matched
    /// to one of them.
    /// </summary>
    public List<string> Known { get; set; } = [];

    /// <summary>UTC day (yyyy-MM-dd) that <see cref="Withdrawn"/> counts, for the daily cap.</summary>
    public string? WithdrawnDay { get; set; }

    /// <summary>Amount withdrawn on <see cref="WithdrawnDay"/>, by asset, fees included.</summary>
    public Dictionary<string, decimal> Withdrawn { get; set; } = new();

    /// <summary>Newest first.</summary>
    public List<ActivityEntry> Activity { get; set; } = [];

    public decimal WithdrawnOn(DateTimeOffset now, string asset) => WithdrawnDay == Day(now) ? Withdrawn.GetValueOrDefault(asset) : 0;

    /// <summary>Adds a withdrawal made at <paramref name="at"/>. One from a day before the one being counted is left out.</summary>
    public void AddWithdrawn(DateTimeOffset at, string asset, decimal amount)
    {
        var day = Day(at);
        if (WithdrawnDay is not null && string.CompareOrdinal(day, WithdrawnDay) < 0)
            return;
        if (WithdrawnDay != day)
        {
            WithdrawnDay = day;
            Withdrawn.Clear();
        }
        Withdrawn[asset] = Withdrawn.GetValueOrDefault(asset) + amount;
    }

    public void Remember(string reference)
    {
        Known.Remove(reference);
        Known.Insert(0, reference);
        if (Known.Count > MaxKnown)
            Known.RemoveRange(MaxKnown, Known.Count - MaxKnown);
    }

    /// <summary>Adds an entry. A repeat of the latest entry (same outcome and message) replaces it, so waiting run after run doesn't fill the list.</summary>
    public void Add(ActivityEntry entry)
    {
        var latest = Activity.FirstOrDefault();
        if (latest is not null && latest.Outcome == entry.Outcome && latest.Message == entry.Message)
            Activity.Remove(latest);
        Activity.Insert(0, entry);
        if (Activity.Count > MaxActivity)
            Activity.RemoveRange(MaxActivity, Activity.Count - MaxActivity);
    }

    static string Day(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public class PendingWithdrawal
{
    public Guid Id { get; set; }

    /// <summary>The exchange and key it was sent with: only that account can show whether it went through.</summary>
    public string? ExchangeId { get; set; }

    public string? KeyFingerprint { get; set; }

    public string Asset { get; set; } = "";
    public string Destination { get; set; } = "";

    /// <summary>What leaves the account, fees included.</summary>
    public decimal Amount { get; set; }

    /// <summary>What the quote said would arrive.</summary>
    public decimal NetAmount { get; set; }

    public decimal Fee { get; set; }

    public DateTimeOffset SentAt { get; set; }

    /// <summary>
    /// The exchange's withdrawals already listed just before this one was sent, which can never be it: an earlier withdrawal
    /// of the same amount and destination must not be mistaken for it when the response is lost.
    /// </summary>
    public List<string> SeenBefore { get; set; } = [];
}
