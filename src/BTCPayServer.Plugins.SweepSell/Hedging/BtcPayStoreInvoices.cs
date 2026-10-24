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
    /// Pages by creation time, oldest first, each page starting at the last one's newest invoice (and skipping those already
    /// read), rather than by offset: an invoice becoming paid while the pages are read can't shift the rest so one is missed.
    /// </summary>
    public async Task<IReadOnlyList<HedgeInvoice>> GetPaidSinceAsync(string storeId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, HedgeInvoice>();
        var from = since;
        while (true)
        {
            var page = await invoices.GetInvoices(new InvoiceQuery
            {
                StoreId = [storeId],
                StartDate = from,
                Status = [nameof(InvoiceStatus.Processing), nameof(InvoiceStatus.Settled)],
                OrderByDesc = false,
                Take = PageSize
            }, cancellationToken);
            var added = 0;
            foreach (var invoice in page)
                if (found.TryAdd(invoice.Id, From(invoice)))
                    added++;
            // A full page adding nothing new means more invoices than a page share one creation time; their invoice events
            // still hedge them, and the scan stops rather than loop.
            if (page.Length < PageSize || added == 0)
                return found.Values.ToList();
            from = page[^1].InvoiceTime;
        }
    }

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
