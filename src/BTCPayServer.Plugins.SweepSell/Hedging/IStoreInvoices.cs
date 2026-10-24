#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

public enum HedgeInvoiceStatus
{
    /// <summary>Not paid in full yet, expired or invalid: never hedged.</summary>
    Other,
    Processing,
    Settled
}

/// <summary>What the hedge job needs to know about an invoice.</summary>
/// <param name="BtcPaid">BTC its counted payments brought in, on-chain and Lightning.</param>
/// <param name="BtcValue">Its price in BTC at the invoice's own rate, or null when the invoice has no BTC rate.</param>
public sealed record HedgeInvoice(string Id, string StoreId, HedgeInvoiceStatus Status, string Currency, DateTimeOffset CreatedAt,
    decimal BtcPaid, decimal? BtcValue);

/// <summary>The store invoices, as the hedge job needs them. BTCPay's implementation is <see cref="BtcPayStoreInvoices"/>.</summary>
public interface IStoreInvoices
{
    /// <summary>The store's invoices created since <paramref name="since"/> that are processing or settled.</summary>
    Task<IReadOnlyList<HedgeInvoice>> GetPaidSinceAsync(string storeId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>The invoice as saved now, or null when there is none.</summary>
    Task<HedgeInvoice?> GetAsync(string invoiceId, CancellationToken cancellationToken);
}
