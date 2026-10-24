#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Plugins.SweepSell.Sweeping;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

/// <summary>What the hedge job of one store has done and is waiting on, saved as a store setting.</summary>
public class HedgeState
{
    public const int MaxActivity = 50;

    /// <summary>How long a hedged invoice is remembered. Invoices older than <see cref="HedgeService.MaxInvoiceAge"/> are never hedged.</summary>
    public static readonly TimeSpan KeepHedged = TimeSpan.FromDays(45);

    public DateTimeOffset? LastRun { get; set; }

    /// <summary>When recent invoices were last listed, to catch any whose event was missed (e.g. while the server was down).</summary>
    public DateTimeOffset? LastScan { get; set; }

    /// <summary>
    /// Every invoice taken for hedging, by invoice ID, with what is still to sell. An invoice is added once, so a repeated
    /// event, a restart or a rescan can't hedge it twice. The one exception: an invoice with nothing sold yet is dropped when
    /// the settings it was taken under change (taken while Processing and the trigger is now Settled, or another amount to
    /// hedge), to be taken again under the current ones.
    /// </summary>
    public Dictionary<string, HedgedInvoice> Invoices { get; set; } = new();

    /// <summary>Sells sent whose outcome isn't known yet; nothing more is sold until each is found (see <see cref="PendingOrders"/>).</summary>
    public List<PendingOrder> Pending { get; set; } = [];

    /// <summary>BTC sold per UTC day, for the daily cap.</summary>
    public DailyTotals Sold { get; set; } = new();

    /// <summary>Newest first.</summary>
    public List<ActivityEntry> Activity { get; set; } = [];

    public void Add(ActivityEntry entry) => ActivityLog.Add(Activity, entry, MaxActivity);

    /// <summary>
    /// Invoices with BTC still to sell into <paramref name="market"/>, taken while hedging was on since
    /// <paramref name="enabledSince"/>, oldest invoice first.
    /// </summary>
    public IEnumerable<KeyValuePair<string, HedgedInvoice>> Queued(string market, bool dryRun, DateTimeOffset? enabledSince) =>
        Invoices.Where(i => i.Value.Remaining > 0 && i.Value.DryRun == dryRun && i.Value.Market == market && i.Value.EnabledSince == enabledSince)
            .OrderBy(i => i.Value.CreatedAt)
            .ThenBy(i => i.Key, StringComparer.Ordinal);

    /// <summary>
    /// Counts <paramref name="sold"/> BTC against the queued invoices of <paramref name="market"/> and of the hedging
    /// period that started at <paramref name="enabledSince"/>, oldest first. What no such invoice needs is dropped.
    /// </summary>
    public void Allocate(string market, decimal sold, DateTimeOffset? enabledSince, bool dryRun = false)
    {
        foreach (var (_, invoice) in Queued(market, dryRun, enabledSince).ToList())
        {
            if (sold <= 0)
                break;
            var part = Math.Min(sold, invoice.Remaining);
            invoice.Remaining -= part;
            sold -= part;
        }
    }

    /// <summary>Forgets fully hedged invoices added before <paramref name="before"/>.</summary>
    public void Prune(DateTimeOffset before)
    {
        foreach (var id in Invoices.Where(i => i.Value.Remaining <= 0 && i.Value.AddedAt < before).Select(i => i.Key).ToList())
            Invoices.Remove(id);
    }
}

public class HedgedInvoice
{
    /// <summary>BTC to sell for the invoice.</summary>
    public decimal Amount { get; set; }

    /// <summary>BTC not sold yet; 0 once hedged.</summary>
    public decimal Remaining { get; set; }

    /// <summary>What the BTC is sold for, e.g. CAD. Follows the settings if the market for <see cref="Currency"/> changes.</summary>
    public string Market { get; set; } = "";

    /// <summary>The invoice's currency, which decides <see cref="Market"/>.</summary>
    public string? Currency { get; set; }

    /// <summary>Taken while hedging was in dry run: only ever checked with the exchange, never sold.</summary>
    public bool DryRun { get; set; }

    /// <summary>Which amount <see cref="Amount"/> is (the BTC paid or the invoice's value).</summary>
    public HedgeAmount Basis { get; set; }

    /// <summary>Taken while the invoice was Processing (not yet Settled), under the Processing trigger.</summary>
    public bool TakenProcessing { get; set; }

    /// <summary>When hedging was turned on when the invoice was taken; one left from an earlier period isn't sold.</summary>
    public DateTimeOffset? EnabledSince { get; set; }

    /// <summary>When the invoice was created: fills are counted against the oldest invoices first.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was taken for hedging; fully hedged invoices are forgotten <see cref="HedgeState.KeepHedged"/> after.</summary>
    public DateTimeOffset AddedAt { get; set; }
}
