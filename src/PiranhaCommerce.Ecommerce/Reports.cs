using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public enum ReportInterval { Day, Week, Month }
public enum ProductReportSort { Revenue, Quantity }

/// <param name="Goods">Goods before any discount and before tax.</param>
/// <param name="Refunded">Money refunded in the period (by the date it was refunded) on orders that were not cancelled.</param>
/// <param name="Net">Total less Refunded.</param>
public sealed record SalesBucketDto(DateOnly Period, int Orders, int Units, decimal Goods, decimal Discounts, decimal Shipping, decimal Tax, decimal Total, decimal Refunded, decimal Net);
public sealed record SalesReportDto(string Currency, DateOnly From, DateOnly To, ReportInterval Interval, IReadOnlyList<SalesBucketDto> Buckets, SalesBucketDto Totals);

/// <param name="Revenue">The goods after any discount and before tax.</param>
/// <param name="Returned">Units on these lines that have since been returned.</param>
public sealed record ProductSalesDto(Guid ProductId, string Name, string? Sku, int Orders, int Quantity, decimal Revenue, int Returned);
public sealed record ProductSalesReportDto(string Currency, DateOnly From, DateOnly To, ProductReportSort Sort, bool RolledUp, IReadOnlyList<ProductSalesDto> Rows);

/// <param name="CategoryId">Null for products that are in no category.</param>
public sealed record CategorySalesDto(Guid? CategoryId, string Path, int Products, int Quantity, decimal Revenue);
public sealed record CategorySalesReportDto(string Currency, DateOnly From, DateOnly To, IReadOnlyList<CategorySalesDto> Rows);

public sealed record CustomerSpendDto(Guid CustomerId, string Email, string? Name, int Orders, decimal Total, DateTimeOffset LastOrderUtc);
public sealed record CustomerReportDto(string Currency, DateOnly From, DateOnly To, int NewCustomers, int GuestOrders, decimal GuestTotal, IReadOnlyList<CustomerSpendDto> Top);

/// <param name="Shipping">True for the tax charged on delivery.</param>
/// <param name="Taxable">What the tax was charged on.</param>
public sealed record TaxRowDto(string Name, decimal Rate, bool Shipping, decimal Taxable, decimal Tax);
public sealed record TaxReportDto(string Currency, DateOnly From, DateOnly To, IReadOnlyList<TaxRowDto> Rows, decimal TaxableTotal, decimal TaxTotal);

/// <summary>
/// Reports over placed orders. Every report counts orders that were not cancelled, by the day they were placed (UTC), for an inclusive range of dates
/// (the last 30 days when none is given). Amounts are in the store's currency.
/// </summary>
public interface IReportService
{
    Task<SalesReportDto> SalesAsync(DateOnly? from, DateOnly? to, ReportInterval interval, CancellationToken ct = default);

    /// <param name="rollUpVariants">Count a variant's sales under its parent product.</param>
    Task<ProductSalesReportDto> ProductsAsync(DateOnly? from, DateOnly? to, ProductReportSort sort, int top, bool rollUpVariants, CancellationToken ct = default);

    /// <summary>A product in several categories counts in each, so the rows can add up to more than the total. A variant counts in its parent's categories.</summary>
    Task<CategorySalesReportDto> CategoriesAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);

    Task<CustomerReportDto> CustomersAsync(DateOnly? from, DateOnly? to, int top, CancellationToken ct = default);

    /// <summary>Tax charged, by rate, before any refund. For the lines and for delivery.</summary>
    Task<TaxReportDto> TaxAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

internal sealed class ReportService(PiranhaDbContext db, IOptions<EcommerceOptions> options) : IReportService
{
    private const int MaxDays = 1100;

    private string Currency => options.Value.Currency;

    private static (DateOnly From, DateOnly To, DateTimeOffset FromUtc, DateTimeOffset ToUtc) Range(DateOnly? from, DateOnly? to)
    {
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddDays(-29);
        if (start > end) throw new PiranhaValidationException("from", "The start date is after the end date.");
        if (end.DayNumber - start.DayNumber >= MaxDays) throw new PiranhaValidationException("from", $"A report can cover at most {MaxDays} days.");
        return (start, end, new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    private IQueryable<Order> Orders(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        db.Set<Order>().AsNoTracking().Where(o => o.Stage != OrderStage.Cancelled && o.CreatedUtc >= fromUtc && o.CreatedUtc < toUtc);

    private IQueryable<OrderLine> Lines(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        from l in db.Set<OrderLine>().AsNoTracking()
        join o in Orders(fromUtc, toUtc) on l.OrderId equals o.Id
        select l;

    // ---- sales over time

    private static DateOnly Bucket(DateTimeOffset when, ReportInterval interval)
    {
        var d = DateOnly.FromDateTime(when.UtcDateTime);
        return interval switch
        {
            ReportInterval.Week => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)), // weeks start on Monday
            ReportInterval.Month => new DateOnly(d.Year, d.Month, 1),
            _ => d,
        };
    }

    private static DateOnly Next(DateOnly bucket, ReportInterval interval) => interval switch
    {
        ReportInterval.Week => bucket.AddDays(7),
        ReportInterval.Month => bucket.AddMonths(1),
        _ => bucket.AddDays(1),
    };

    public async Task<SalesReportDto> SalesAsync(DateOnly? from, DateOnly? to, ReportInterval interval, CancellationToken ct = default)
    {
        var (start, end, fromUtc, toUtc) = Range(from, to);
        if (interval == ReportInterval.Day && end.DayNumber - start.DayNumber >= 366) throw new PiranhaValidationException("interval", "A daily report can cover at most a year: choose week or month.");

        var orders = await Orders(fromUtc, toUtc).Select(o => new { o.CreatedUtc, o.Subtotal, o.DiscountTotal, o.ShippingTotal, o.TaxTotal, o.Total }).ToListAsync(ct);
        var units = await (from l in db.Set<OrderLine>().AsNoTracking()
                           join o in Orders(fromUtc, toUtc) on l.OrderId equals o.Id
                           select new { o.CreatedUtc, l.Quantity }).ToListAsync(ct);
        var refunds = await (from p in db.Set<Payment>().AsNoTracking()
                             join o in db.Set<Order>().AsNoTracking() on p.OrderId equals o.Id
                             where p.Kind == PaymentKind.Refund && p.CreatedUtc >= fromUtc && p.CreatedUtc < toUtc && o.Stage != OrderStage.Cancelled
                             select new { p.CreatedUtc, p.Amount }).ToListAsync(ct);

        var buckets = new SortedDictionary<DateOnly, SalesBucketDto>();
        for (var b = Bucket(fromUtc, interval); b <= end; b = Next(b, interval)) buckets[b] = new SalesBucketDto(b, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        void Add(DateTimeOffset when, Func<SalesBucketDto, SalesBucketDto> change)
        {
            var key = Bucket(when, interval);
            buckets[key] = change(buckets[key]);
        }
        foreach (var o in orders)
            Add(o.CreatedUtc, b => b with { Orders = b.Orders + 1, Goods = b.Goods + o.Subtotal, Discounts = b.Discounts + o.DiscountTotal, Shipping = b.Shipping + o.ShippingTotal, Tax = b.Tax + o.TaxTotal, Total = b.Total + o.Total });
        foreach (var u in units) Add(u.CreatedUtc, b => b with { Units = b.Units + u.Quantity });
        foreach (var r in refunds) Add(r.CreatedUtc, b => b with { Refunded = b.Refunded + r.Amount });

        var rows = buckets.Values.Select(b => b with { Net = b.Total - b.Refunded }).ToList();
        var totals = new SalesBucketDto(start, rows.Sum(b => b.Orders), rows.Sum(b => b.Units), rows.Sum(b => b.Goods), rows.Sum(b => b.Discounts), rows.Sum(b => b.Shipping),
            rows.Sum(b => b.Tax), rows.Sum(b => b.Total), rows.Sum(b => b.Refunded), rows.Sum(b => b.Net));
        return new SalesReportDto(Currency, start, end, interval, rows, totals);
    }

    // ---- products

    private sealed record Sold(Guid ProductId, string Name, string? Sku, int Orders, int Quantity, decimal Revenue, int Returned);

    private async Task<List<Sold>> SoldByProductAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        (await Lines(fromUtc, toUtc).GroupBy(l => l.ProductId).Select(g => new
        {
            ProductId = g.Key, Orders = g.Select(l => l.OrderId).Distinct().Count(), Quantity = g.Sum(l => l.Quantity), Revenue = g.Sum(l => l.LineTotal - l.DiscountAmount),
            Returned = g.Sum(l => l.QuantityReturned), Name = g.Max(l => l.Name), Sku = g.Max(l => l.Sku),
        }).ToListAsync(ct)).Select(x => new Sold(x.ProductId, x.Name ?? "", x.Sku, x.Orders, x.Quantity, x.Revenue, x.Returned)).ToList();

    public async Task<ProductSalesReportDto> ProductsAsync(DateOnly? from, DateOnly? to, ProductReportSort sort, int top, bool rollUpVariants, CancellationToken ct = default)
    {
        var (start, end, fromUtc, toUtc) = Range(from, to);
        if (top is < 1 or > 1000) throw new PiranhaValidationException("top", "Ask for between 1 and 1000 products.");

        List<Sold> sold;
        if (!rollUpVariants) sold = await SoldByProductAsync(fromUtc, toUtc, ct);
        else
        {
            // Orders must be counted once per parent, so look at product-and-order pairs and merge them in memory.
            var pairs = (await Lines(fromUtc, toUtc).GroupBy(l => new { l.ProductId, l.OrderId }).Select(g => new
            {
                g.Key.ProductId, g.Key.OrderId, Quantity = g.Sum(l => l.Quantity), Revenue = g.Sum(l => l.LineTotal - l.DiscountAmount), Returned = g.Sum(l => l.QuantityReturned), Name = g.Max(l => l.Name), Sku = g.Max(l => l.Sku),
            }).ToListAsync(ct));
            var ids = pairs.Select(p => p.ProductId).Distinct().ToList();
            var parentOf = await db.Set<ProductVariant>().AsNoTracking().Where(v => ids.Contains(v.ProductId)).ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);
            var parents = await db.Set<Product>().AsNoTracking().Where(p => parentOf.Values.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => new { p.Name, p.Sku }, ct);
            sold = pairs.GroupBy(p => parentOf.GetValueOrDefault(p.ProductId, p.ProductId)).Select(g =>
            {
                var parent = parents.GetValueOrDefault(g.Key);
                var first = g.First();
                return new Sold(g.Key, parent?.Name ?? first.Name ?? "", parent is null ? first.Sku : parent.Sku, g.Select(p => p.OrderId).Distinct().Count(), g.Sum(p => p.Quantity), g.Sum(p => p.Revenue), g.Sum(p => p.Returned));
            }).ToList();
        }

        var rows = (sort == ProductReportSort.Quantity ? sold.OrderByDescending(s => s.Quantity).ThenByDescending(s => s.Revenue) : sold.OrderByDescending(s => s.Revenue).ThenByDescending(s => s.Quantity))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.ProductId).Take(top)
            .Select(s => new ProductSalesDto(s.ProductId, s.Name, s.Sku, s.Orders, s.Quantity, s.Revenue, s.Returned)).ToList();
        return new ProductSalesReportDto(Currency, start, end, sort, rollUpVariants, rows);
    }

    public async Task<CategorySalesReportDto> CategoriesAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var (start, end, fromUtc, toUtc) = Range(from, to);
        var sold = await SoldByProductAsync(fromUtc, toUtc, ct);
        var inCategories = await ProductVariants.CategoriesAsync(db, sold.Select(s => s.ProductId).ToList(), ct);
        var categories = await db.Set<Category>().AsNoTracking().Select(c => new { c.Id, c.Name, c.ParentId }).ToDictionaryAsync(c => c.Id, ct);
        string PathOf(Guid id)
        {
            var parts = new List<string>();
            for (Guid? cursor = id; cursor is { } c && categories.TryGetValue(c, out var node) && parts.Count < 100; cursor = node.ParentId) parts.Insert(0, node.Name);
            return string.Join(" > ", parts);
        }

        var totals = new Dictionary<Guid, (int Products, int Quantity, decimal Revenue)>(); // Guid.Empty stands for no category
        foreach (var s in sold)
            foreach (var category in inCategories.TryGetValue(s.ProductId, out var list) && list.Count > 0 ? list : [Guid.Empty])
            {
                var t = totals.GetValueOrDefault(category);
                totals[category] = (t.Products + 1, t.Quantity + s.Quantity, t.Revenue + s.Revenue);
            }
        var rows = totals.Select(t => new CategorySalesDto(t.Key == Guid.Empty ? null : t.Key, t.Key == Guid.Empty ? "(no category)" : PathOf(t.Key), t.Value.Products, t.Value.Quantity, t.Value.Revenue))
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new CategorySalesReportDto(Currency, start, end, rows);
    }

    // ---- customers

    public async Task<CustomerReportDto> CustomersAsync(DateOnly? from, DateOnly? to, int top, CancellationToken ct = default)
    {
        var (start, end, fromUtc, toUtc) = Range(from, to);
        if (top is < 1 or > 1000) throw new PiranhaValidationException("top", "Ask for between 1 and 1000 customers.");

        var grouped = await Orders(fromUtc, toUtc).GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Orders = g.Count(), Total = g.Sum(o => o.Total), Last = g.Max(o => o.CreatedUtc) }).ToListAsync(ct);
        var guests = grouped.FirstOrDefault(g => g.CustomerId == null);
        var best = grouped.Where(g => g.CustomerId != null).OrderByDescending(g => g.Total).ThenByDescending(g => g.Orders).ThenBy(g => g.CustomerId).Take(top).ToList();
        var ids = best.Select(b => b.CustomerId!.Value).ToList();
        var people = await db.Set<Customer>().AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Email, c.FirstName, c.LastName }).ToDictionaryAsync(c => c.Id, ct);
        var rows = best.Select(b =>
        {
            var p = people.GetValueOrDefault(b.CustomerId!.Value);
            var name = string.Join(' ', new[] { p?.FirstName, p?.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
            return new CustomerSpendDto(b.CustomerId!.Value, p?.Email ?? "", name.Length == 0 ? null : name, b.Orders, b.Total, b.Last);
        }).ToList();
        var newCustomers = await db.Set<Customer>().AsNoTracking().CountAsync(c => c.CreatedUtc >= fromUtc && c.CreatedUtc < toUtc, ct);
        return new CustomerReportDto(Currency, start, end, newCustomers, guests?.Orders ?? 0, guests?.Total ?? 0, rows);
    }

    // ---- tax

    public async Task<TaxReportDto> TaxAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var (start, end, fromUtc, toUtc) = Range(from, to);
        var lines = await Lines(fromUtc, toUtc).GroupBy(l => new { l.TaxName, l.TaxRate })
            .Select(g => new { g.Key.TaxName, g.Key.TaxRate, Taxable = g.Sum(l => l.LineTotal - l.DiscountAmount), Tax = g.Sum(l => l.TaxAmount) }).ToListAsync(ct);
        var shipping = await Orders(fromUtc, toUtc).Where(o => o.ShippingTotal > 0).GroupBy(o => new { o.ShippingTaxName, o.ShippingTaxRate })
            .Select(g => new { g.Key.ShippingTaxName, g.Key.ShippingTaxRate, Taxable = g.Sum(o => o.ShippingTotal), Tax = g.Sum(o => o.ShippingTax) }).ToListAsync(ct);

        var rows = lines.Select(l => new TaxRowDto(l.TaxName ?? "(not taxed)", l.TaxRate, false, l.Taxable, l.Tax))
            .Concat(shipping.Select(s => new TaxRowDto(s.ShippingTaxName ?? "(not taxed)", s.ShippingTaxRate, true, s.Taxable, s.Tax)))
            .OrderBy(r => r.Shipping).ThenByDescending(r => r.Rate).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new TaxReportDto(Currency, start, end, rows, rows.Sum(r => r.Taxable), rows.Sum(r => r.Tax));
    }
}

/// <summary>One report as plain rows and columns, for a CSV file or for the admin to show.</summary>
public sealed record ReportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

public static class ReportTables
{
    private static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string N(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public static ReportTable Of(SalesReportDto r)
    {
        var label = r.Interval switch { ReportInterval.Month => "Month starting", ReportInterval.Week => "Week starting", _ => "Day" };
        return new($"Sales by {r.Interval.ToString().ToLowerInvariant()}, {D(r.From)} to {D(r.To)} ({r.Currency})",
            [label, "Orders", "Units", "Goods", "Discounts", "Shipping", "Tax", "Total", "Refunded", "Net"],
            [.. r.Buckets.Select(Row), [.. Row(r.Totals with { Period = r.From }).Select((c, i) => i == 0 ? "Total" : c)]]);
        static IReadOnlyList<string> Row(SalesBucketDto b) => [D(b.Period), b.Orders.ToString(CultureInfo.InvariantCulture), b.Units.ToString(CultureInfo.InvariantCulture), M(b.Goods), M(b.Discounts), M(b.Shipping), M(b.Tax), M(b.Total), M(b.Refunded), M(b.Net)];
    }

    public static ReportTable Of(ProductSalesReportDto r) => new($"Best sellers by {r.Sort.ToString().ToLowerInvariant()}, {D(r.From)} to {D(r.To)} ({r.Currency}){(r.RolledUp ? ", variants under their parent" : "")}",
        ["Product", "SKU", "Orders", "Quantity", "Revenue", "Returned"],
        [.. r.Rows.Select(p => (IReadOnlyList<string>)[p.Name, p.Sku ?? "", p.Orders.ToString(CultureInfo.InvariantCulture), p.Quantity.ToString(CultureInfo.InvariantCulture), M(p.Revenue), p.Returned.ToString(CultureInfo.InvariantCulture)])]);

    public static ReportTable Of(CategorySalesReportDto r) => new($"Sales by category, {D(r.From)} to {D(r.To)} ({r.Currency})",
        ["Category", "Products", "Quantity", "Revenue"],
        [.. r.Rows.Select(c => (IReadOnlyList<string>)[c.Path, c.Products.ToString(CultureInfo.InvariantCulture), c.Quantity.ToString(CultureInfo.InvariantCulture), M(c.Revenue)])]);

    public static ReportTable Of(CustomerReportDto r) => new($"Top customers, {D(r.From)} to {D(r.To)} ({r.Currency}); {r.NewCustomers} new customers; {r.GuestOrders} guest orders worth {M(r.GuestTotal)}",
        ["Customer", "Email", "Orders", "Total", "Last order"],
        [.. r.Top.Select(c => (IReadOnlyList<string>)[c.Name ?? "", c.Email, c.Orders.ToString(CultureInfo.InvariantCulture), M(c.Total), c.LastOrderUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)])]);

    public static ReportTable Of(TaxReportDto r) => new($"Tax charged, {D(r.From)} to {D(r.To)} ({r.Currency}), before refunds",
        ["Tax", "Rate %", "On", "Taxable", "Tax"],
        [.. r.Rows.Select(t => (IReadOnlyList<string>)[t.Name, N(t.Rate), t.Shipping ? "Delivery" : "Goods", M(t.Taxable), M(t.Tax)]), ["Total", "", "", M(r.TaxableTotal), M(r.TaxTotal)]]);
}
