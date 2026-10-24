#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SweepSell.Exchanges;

namespace BTCPayServer.Plugins.SweepSell.Conversion;

/// <summary>
/// Looks up sells whose outcome wasn't known when they were sent. Shared by every job that sells (convert, hedge), so
/// they all treat a lost response the same way: an order is only dropped once the exchange shows it closed, refused it,
/// or still has no record of it after <see cref="Grace"/>.
/// </summary>
public static class PendingOrders
{
    /// <summary>
    /// How long an order the exchange has no record of stays pending before it counts as never placed. Connectors make
    /// live orders expire well before this (see <see cref="IExchangeConnector.SellAsync"/>).
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Looks up every order in <paramref name="pending"/>, removing the settled ones. <paramref name="filled"/> is called
    /// with what each closed order sold (never zero). True when none is left, so selling may continue.
    /// </summary>
    public static async Task<bool> ReconcileAsync(List<PendingOrder> pending, IExchangeConnector exchange, ExchangeCredentials credentials,
        DateTimeOffset now, Action<PendingOrder, decimal> filled, Action<string?, ActivityOutcome, string> record,
        Func<Exception, bool> isRecordedFailure, CancellationToken cancellationToken)
    {
        var fingerprint = credentials.Fingerprint();
        foreach (var order in pending.ToList())
        {
            if (order.ExchangeId != exchange.Id || order.KeyFingerprint != fingerprint)
            {
                // Another account would report no such order, and the order would wrongly count as never placed.
                record(order.Asset, ActivityOutcome.Failed,
                    $"The earlier sell of {Amount(order.Amount)} {order.Asset} (client order {order.ClientOrderId:D}) was sent with a different exchange key, so it can't be checked with this one. " +
                    "Put that key back, or check the order in your exchange yourself and then remove it under Pending orders. Nothing is sold until then.");
                return false;
            }
            OrderLookup? lookup;
            try
            {
                lookup = await exchange.FindOrderAsync(credentials, order.ClientOrderId, cancellationToken);
            }
            catch (Exception e) when (isRecordedFailure(e))
            {
                record(order.Asset, ActivityOutcome.Failed,
                    $"Could not look up the earlier sell of {Amount(order.Amount)} {order.Asset} on {exchange.Name}: {e.Message}. Nothing is sold until it is found.");
                return false;
            }
            if (lookup is null)
            {
                if (order.OrderIds.Count > 0)
                {
                    // The exchange confirmed this order, so it exists; it is never treated as unplaced and sold again.
                    record(order.Asset, now - order.SentAt < Grace ? ActivityOutcome.Waiting : ActivityOutcome.Failed,
                        $"The sell of {Amount(order.Amount)} {order.Asset} ({string.Join(", ", order.OrderIds)}) was placed but {exchange.Name} doesn't list it. " +
                        "If this lasts, check it in your exchange and remove it under Pending orders. Nothing is sold until then.");
                    continue;
                }
                if (now - order.SentAt < Grace)
                {
                    record(order.Asset, ActivityOutcome.Waiting,
                        $"The earlier {order.Asset} sell isn't on {exchange.Name} yet. Checking again on the next run.");
                    continue;
                }
                pending.Remove(order);
                record(order.Asset, ActivityOutcome.Failed,
                    $"The earlier sell of {Amount(order.Amount)} {order.Asset} was never placed on {exchange.Name}. It will be tried again.");
                continue;
            }
            if (lookup.Open)
            {
                record(order.Asset, ActivityOutcome.Waiting,
                    $"The earlier {order.Asset} sell ({string.Join(", ", lookup.OrderIds)}) is still open on {exchange.Name}.");
                continue;
            }
            if (Implausible(lookup, order, exchange.Name) is { } bad)
            {
                record(order.Asset, ActivityOutcome.Failed, bad);
                return false;
            }
            pending.Remove(order);
            var orders = string.Join(", ", lookup.OrderIds);
            if (lookup.ExecutedAmount <= 0)
            {
                // Closed without a fill: no buyer at the floor, canceled or expired. Nothing was sold, so selling may retry.
                record(order.Asset, ActivityOutcome.Waiting,
                    $"The earlier sell of {Amount(order.Amount)} {order.Asset} ({orders}) closed on {exchange.Name} without selling anything. It will be tried again.");
                continue;
            }
            filled(order, lookup.ExecutedAmount);
            record(order.Asset, ActivityOutcome.Sold,
                $"Found the earlier sell on {exchange.Name}: {Amount(lookup.ExecutedAmount)} of {Amount(order.Amount)} {order.Asset} sold for {order.TargetAsset} ({orders}).");
        }
        return pending.Count == 0;
    }

    /// <summary>
    /// Why a closed order's reported fill can't be right (less than nothing, or more than was ordered), or null. Such an
    /// answer is never counted: the order stays pending for the owner to check.
    /// </summary>
    public static string? Implausible(OrderLookup lookup, PendingOrder order, string exchangeName) =>
        lookup.ExecutedAmount < 0 || lookup.ExecutedAmount > order.Amount
            ? $"{exchangeName} reports the sell of {Amount(order.Amount)} {order.Asset} ({string.Join(", ", lookup.OrderIds)}) as filling {Amount(lookup.ExecutedAmount)}, which can't be right. " +
              "It stays pending and nothing more is sold: check it in your exchange, then remove it under Pending orders."
            : null;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
