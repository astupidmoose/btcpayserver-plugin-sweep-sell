#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

/// <summary>The store invoices through BTCPay's invoice repository. Read only: hedging never changes an invoice.</summary>
public class BtcPayStoreInvoices(InvoiceRepository invoices) : IStoreInvoices
{
    const int PageSize = 100;

    /// <summary>
    /// Pages by creation time, oldest first, rather than by offset: an invoice becoming paid while the pages are read can't
    /// shift the rest so one is missed. After a full page, every invoice created at that page's newest time is read in one
    /// go, however many share it, and the next page starts just after it.
    /// </summary>
    public async Task<IReadOnlyList<HedgeInvoice>> GetPaidSinceAsync(string storeId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, HedgeInvoice>();
        var from = since;
        while (true)
        {
            var page = await PaidAsync(storeId, from, null, PageSize, cancellationToken);
            foreach (var invoice in page)
                found.TryAdd(invoice.Id, From(invoice));
            if (page.Length < PageSize)
                return found.Values.ToList();
            var last = page[^1].InvoiceTime;
            foreach (var invoice in await PaidAsync(storeId, last, last, null, cancellationToken))
                found.TryAdd(invoice.Id, From(invoice));
            from = After(last); // always later than the page started, so the scan ends
        }
    }

    /// <summary>
    /// The first time the database can store after <paramref name="time"/>. PostgreSQL keeps whole microseconds, so a
    /// smaller step would round back to the same time and read the same page forever.
    /// </summary>
    public static DateTimeOffset After(DateTimeOffset time) =>
        new DateTimeOffset(time.Ticks - time.Ticks % TimeSpan.TicksPerMicrosecond, time.Offset).AddTicks(TimeSpan.TicksPerMicrosecond);

    Task<InvoiceEntity[]> PaidAsync(string storeId, DateTimeOffset from, DateTimeOffset? to, int? take, CancellationToken cancellationToken) =>
        invoices.GetInvoices(new InvoiceQuery
        {
            StoreId = [storeId],
            StartDate = from,
            EndDate = to,
            Status = [nameof(InvoiceStatus.Processing), nameof(InvoiceStatus.Settled)],
            OrderByDesc = false,
            Take = take
        }, cancellationToken);

    public async Task<HedgeInvoice?> GetAsync(string invoiceId, CancellationToken cancellationToken) =>
        await invoices.GetInvoice(invoiceId) is { } invoice ? From(invoice) : null;

    public static HedgeInvoice From(InvoiceEntity invoice) => new(
        invoice.Id,
        invoice.StoreId,
        invoice.Status switch
        {
            InvoiceStatus.Processing => HedgeInvoiceStatus.Processing,
            InvoiceStatus.Settled => HedgeInvoiceStatus.Settled,
            _ => HedgeInvoiceStatus.Other
        },
        invoice.Currency,
        invoice.InvoiceTime,
        invoice.GetPayments("BTC", accountedOnly: true).Sum(p => p.Value),
        BtcValue(invoice));

    static decimal? BtcValue(InvoiceEntity invoice)
    {
        if (string.Equals(invoice.Currency, "BTC", StringComparison.OrdinalIgnoreCase))
            return invoice.Price;
        // The invoice's own rate: the price of one BTC in the invoice currency when it was created.
        return invoice.TryGetRate("BTC", out var rate) && rate > 0 ? invoice.Price / rate : null;
    }
}
