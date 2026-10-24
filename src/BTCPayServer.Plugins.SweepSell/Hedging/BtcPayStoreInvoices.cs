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

    public async Task<IReadOnlyList<HedgeInvoice>> GetPaidSinceAsync(string storeId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var found = new List<HedgeInvoice>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await invoices.GetInvoices(new InvoiceQuery
            {
                StoreId = [storeId],
                StartDate = since,
                Status = [nameof(InvoiceStatus.Processing), nameof(InvoiceStatus.Settled)],
                Skip = skip,
                Take = PageSize
            }, cancellationToken);
            found.AddRange(page.Select(From));
            if (page.Length < PageSize)
                return found;
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
