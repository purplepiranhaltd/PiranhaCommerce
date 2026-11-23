using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// Reports for the shop's owners. Every one takes <c>from</c> and <c>to</c> (dates, inclusive; the last 30 days by default) and <c>format=csv</c> for a spreadsheet file instead of JSON.
/// They contain customer names and emails, so they need the same sign-in as any other staff call.
/// </summary>
internal sealed class ReportEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/reports").WithTags("Reports");

        g.MapGet("/sales", async (DateOnly? from, DateOnly? to, ReportInterval? interval, string? format, IReportService svc, CancellationToken ct) =>
            Answer(await svc.SalesAsync(from, to, interval ?? ReportInterval.Day, ct), ReportTables.Of, format, "sales"));

        g.MapGet("/products", async (DateOnly? from, DateOnly? to, ProductReportSort? sort, int? top, bool? rollUpVariants, string? format, IReportService svc, CancellationToken ct) =>
            Answer(await svc.ProductsAsync(from, to, sort ?? ProductReportSort.Revenue, top ?? 20, rollUpVariants ?? false, ct), ReportTables.Of, format, "products"));

        g.MapGet("/categories", async (DateOnly? from, DateOnly? to, string? format, IReportService svc, CancellationToken ct) =>
            Answer(await svc.CategoriesAsync(from, to, ct), ReportTables.Of, format, "categories"));

        g.MapGet("/customers", async (DateOnly? from, DateOnly? to, int? top, string? format, IReportService svc, CancellationToken ct) =>
            Answer(await svc.CustomersAsync(from, to, top ?? 20, ct), ReportTables.Of, format, "customers"));

        g.MapGet("/tax", async (DateOnly? from, DateOnly? to, string? format, IReportService svc, CancellationToken ct) =>
            Answer(await svc.TaxAsync(from, to, ct), ReportTables.Of, format, "tax"));
    }

    private static IResult Answer<T>(T report, Func<T, ReportTable> table, string? format, string name)
    {
        if (string.IsNullOrEmpty(format) || format.Equals("json", StringComparison.OrdinalIgnoreCase)) return Results.Ok(report);
        if (!format.Equals("csv", StringComparison.OrdinalIgnoreCase)) throw new PiranhaValidationException("format", "Use json or csv.");
        var t = table(report);
        return Results.Stream(async stream =>
        {
            // Build the file in memory first: Kestrel does not allow synchronous writes to the response.
            using var buffer = new MemoryStream();
            await using (var writer = new StreamWriter(buffer, new UTF8Encoding(true), 4096, leaveOpen: true))
            {
                Csv.WriteRow(writer, [Csv.Protect(t.Title)]);
                Csv.WriteRow(writer, t.Columns);
                foreach (var row in t.Rows) Csv.WriteRow(writer, row.Select(c => Csv.Protect(c)));
            }
            buffer.Position = 0;
            await buffer.CopyToAsync(stream);
        }, "text/csv; charset=utf-8", $"report-{name}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}
