using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

public sealed class CatalogImportOptions
{
    public const string SectionName = "Piranha:Catalog:Import";

    /// <summary>Rows (products and variants) in one import file.</summary>
    [Range(1, 100_000)]
    public int MaxRows { get; set; } = 5000;

    /// <summary>The biggest file accepted, in bytes (default 10 MB).</summary>
    [Range(1024, 200_000_000)]
    public int MaxBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// Lets another package add columns to the product import and export (the online store adds <c>published</c>, <c>tax_class</c> and <c>weight_grams</c>),
/// so a channel's data travels with the catalogue file without the catalogue knowing about the channel.
/// </summary>
public interface IProductImportExtension
{
    /// <summary>Column names: lower case letters, digits and underscores, unique across every extension and the catalogue's own.</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>The cells for each product, in <see cref="Columns"/> order. A product left out of the result gets empty cells.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string?>>> ExportAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);

    /// <summary>
    /// Applies the cells of one row. <paramref name="cells"/> holds only the columns that had something in them: a value, or null when the cell said
    /// <c>[clear]</c>. Throws <see cref="PiranhaValidationException"/> for a value that cannot be used; the whole row is then undone.
    /// </summary>
    Task ImportAsync(Guid productId, IReadOnlyDictionary<string, string?> cells, bool created, CancellationToken ct = default);
}

/// <param name="DryRun">Only check: nothing is saved, whatever the result.</param>
/// <param name="SkipInvalid">Save the rows that are fine even when others are not. Otherwise one bad row means nothing is saved.</param>
/// <param name="Actor">Recorded on stock movements.</param>
/// <param name="CanAdjustStock">False when the caller may not change stock at the default location (a user limited to other locations).</param>
public sealed record ProductImportRequest(bool DryRun = false, bool SkipInvalid = false, string? Actor = null, bool CanAdjustStock = true);

/// <param name="Row">The row as a spreadsheet numbers it: the header is row 1, so the first product is row 2.</param>
/// <param name="Action"><c>created</c>, <c>updated</c> or <c>failed</c>; in a check that saves nothing, what would have happened.</param>
public sealed record ProductImportRowDto(int Row, string? Sku, string Action, Guid? ProductId, IReadOnlyList<string> Errors);

/// <param name="Applied">Whether anything was saved.</param>
public sealed record ProductImportResultDto(
    bool DryRun, bool Applied, int Rows, int Created, int Updated, int Failed, IReadOnlyList<ProductImportRowDto> Results);

public interface ICatalogTransferService
{
    /// <summary>Writes every product (and variant) as CSV, UTF-8 with a byte order mark so Excel reads it. <paramref name="search"/> narrows it by name or SKU.</summary>
    Task ExportProductsAsync(Stream output, string? search, CancellationToken ct = default);

    /// <summary>
    /// Creates and updates products from a CSV file. Rows are checked first, in a transaction that is rolled back; only if every row is fine (or
    /// <see cref="ProductImportRequest.SkipInvalid"/> is set) are they applied for real, each row on its own. A file that cannot be read at all is a validation error.
    /// </summary>
    Task<ProductImportResultDto> ImportProductsAsync(Stream csv, ProductImportRequest request, CancellationToken ct = default);
}

internal sealed class CatalogTransferService(
    PiranhaDbContext db,
    IProductService products,
    ICategoryService categories,
    IBarcodeService barcodes,
    IProductVariantService variants,
    IStockService stock,
    IEnumerable<IProductImportExtension> extensions,
    IOptions<CatalogImportOptions> options,
    ILogger<CatalogTransferService> logger) : ICatalogTransferService
{
    private const string Clear = "[clear]";
    private static readonly string[] CoreColumns =
    [
        "id", "sku", "parent_id", "parent_sku", "name", "description", "price", "slug", "meta_title", "meta_description", "categories", "barcodes", "stock",
        "option1_name", "option1_value", "option2_name", "option2_value", "option3_name", "option3_value",
    ];

    private IReadOnlyList<IProductImportExtension> Exts { get; } = extensions.ToList();

    private string[] AllColumns => [.. CoreColumns, .. Exts.SelectMany(e => e.Columns)];

    // ---------------------------------------------------------------- export

    public async Task ExportProductsAsync(Stream output, string? search, CancellationToken ct = default)
    {
        var query = db.Set<Product>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(s) || (x.Sku != null && x.Sku.ToLower().Contains(s)));
        }
        var found = await query.Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var parentOf = await db.Set<ProductVariant>().AsNoTracking().ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);

        // A parent, then its variants. A variant found without its parent still names it, so the row can be read back.
        var parentIds = found.Where(f => parentOf.ContainsKey(f.Id)).Select(f => parentOf[f.Id]).Distinct().ToList();
        var names = found.ToDictionary(f => f.Id, f => f.Name);
        foreach (var p in await db.Set<Product>().AsNoTracking().Where(p => parentIds.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToListAsync(ct)) names[p.Id] = p.Name;
        var ordered = found
            .OrderBy(f => names[parentOf.GetValueOrDefault(f.Id, f.Id)], StringComparer.OrdinalIgnoreCase).ThenBy(f => parentOf.GetValueOrDefault(f.Id, f.Id))
            .ThenBy(f => parentOf.ContainsKey(f.Id)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Id)
            .Select(f => f.Id).ToList();

        var categoryList = await db.Set<Category>().AsNoTracking().Select(c => new { c.Id, c.Name, c.ParentId }).ToDictionaryAsync(c => c.Id, ct);
        string PathOf(Guid id)
        {
            var parts = new List<string>();
            for (Guid? cursor = id; cursor is { } c && categoryList.TryGetValue(c, out var node) && parts.Count < 100; cursor = node.ParentId) parts.Insert(0, node.Name);
            return string.Join(" > ", parts);
        }

        await using var writer = new StreamWriter(output, new UTF8Encoding(true), 4096, leaveOpen: true);
        Csv.WriteRow(writer, AllColumns);

        foreach (var chunk in ordered.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            var rows = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var cats = (await db.Set<ProductCategory>().AsNoTracking().Where(c => ids.Contains(c.ProductId)).ToListAsync(ct))
                .GroupBy(c => c.ProductId).ToDictionary(g => g.Key, g => g.Select(c => PathOf(c.CategoryId)).Where(p => p.Length > 0).Order().ToList());
            var codes = (await db.Set<ProductBarcode>().AsNoTracking().Where(b => ids.Contains(b.ProductId)).ToListAsync(ct))
                .GroupBy(b => b.ProductId).ToDictionary(g => g.Key, g => g.Select(b => b.Code).Order().ToList());
            var levels = await db.Set<StockLevel>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.LocationId == StockLocations.DefaultId).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, ct);
            var variantOf = chunk.Where(parentOf.ContainsKey).ToDictionary(id => id, id => parentOf[id]);
            var parents = await db.Set<Product>().AsNoTracking().Where(p => variantOf.Values.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
            var optionValues = await (from x in db.Set<ProductVariantValue>().AsNoTracking()
                                      join o in db.Set<ProductOption>().AsNoTracking() on x.OptionId equals o.Id
                                      join v in db.Set<ProductOptionValue>().AsNoTracking() on x.OptionValueId equals v.Id
                                      where ids.Contains(x.ProductId)
                                      orderby o.Position
                                      select new { x.ProductId, Option = o.Name, v.Value }).ToListAsync(ct);
            var perVariant = optionValues.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.ToList());
            var extra = new List<IReadOnlyDictionary<Guid, IReadOnlyList<string?>>>();
            foreach (var e in Exts) extra.Add(await e.ExportAsync(ids, ct));

            foreach (var id in chunk)
            {
                var p = rows[id];
                var cells = new List<string?>
                {
                    p.Id.ToString(), Csv.Protect(p.Sku), variantOf.TryGetValue(id, out var parent) ? parent.ToString() : null, parents.GetValueOrDefault(parent) is { } ps ? Csv.Protect(ps) : null,
                    Csv.Protect(p.Name), Csv.Protect(p.Description), p.Price.ToString("0.####", CultureInfo.InvariantCulture), p.Slug, Csv.Protect(p.MetaTitle), Csv.Protect(p.MetaDescription),
                    Csv.Protect(string.Join(" | ", cats.GetValueOrDefault(id) ?? [])), Csv.Protect(string.Join(" | ", codes.GetValueOrDefault(id) ?? [])),
                    levels.TryGetValue(id, out var q) ? q.ToString("0.####", CultureInfo.InvariantCulture) : null,
                };
                var values = perVariant.GetValueOrDefault(id) ?? [];
                for (var n = 0; n < 3; n++)
                {
                    cells.Add(n < values.Count ? Csv.Protect(values[n].Option) : null);
                    cells.Add(n < values.Count ? Csv.Protect(values[n].Value) : null);
                }
                for (var i = 0; i < Exts.Count; i++)
                {
                    var mine = extra[i].GetValueOrDefault(id);
                    var width = Exts[i].Columns.Count;
                    for (var c = 0; c < width; c++) cells.Add(mine is not null && c < mine.Count ? Csv.Protect(mine[c]) : null);
                }
                Csv.WriteRow(writer, cells);
            }
        }
        await writer.FlushAsync(ct);
    }

    // ---------------------------------------------------------------- import

    private sealed record ImportRow(int Number, Dictionary<string, string?> Cells)
    {
        public bool Has(string column) => Cells.ContainsKey(column);
        public string? Get(string column) => Cells.GetValueOrDefault(column);
        public string? Sku => Get("sku");
        public bool IsVariant => Has("parent_id") || Has("parent_sku");
    }

    /// <summary>A problem with one row, reported to the person importing.</summary>
    private sealed class RowProblem(string message) : Exception(message);

    private sealed record RowOutcome(Guid ProductId, bool Created);

    public async Task<ProductImportResultDto> ImportProductsAsync(Stream csv, ProductImportRequest request, CancellationToken ct = default)
    {
        var opts = options.Value;
        string text;
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await csv.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > opts.MaxBytes) throw new PiranhaValidationException("file", $"A file can be at most {opts.MaxBytes / (1024 * 1024d):0.#} MB.");
                buffer.Write(chunk, 0, read);
            }
            text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer.ToArray());
        }

        List<List<string>> table;
        try { table = Csv.Parse(text); }
        catch (FormatException ex) { throw new PiranhaValidationException("file", "That is not a readable CSV file: " + ex.Message); }
        if (table.Count == 0) throw new PiranhaValidationException("file", "The file is empty.");

        var header = table[0].Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_')).ToList();
        var known = AllColumns.ToHashSet();
        var unknown = header.Where(h => h.Length > 0 && !known.Contains(h)).ToList();
        if (unknown.Count > 0) throw new PiranhaValidationException("file", "Unknown column" + (unknown.Count > 1 ? "s" : "") + ": " + string.Join(", ", unknown) + ". Use the columns of an export.");
        var dup = header.Where(h => h.Length > 0).GroupBy(h => h).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new PiranhaValidationException("file", $"The column {dup.Key} appears twice.");
        if (!header.Contains("sku") && !header.Contains("id")) throw new PiranhaValidationException("file", "The file needs an sku column (or an id column) to say which product each row is.");
        if (table.Count - 1 > opts.MaxRows) throw new PiranhaValidationException("file", $"A file can have at most {opts.MaxRows} rows.");
        if (request.CanAdjustStock == false && header.Contains("stock"))
            throw new PiranhaForbiddenException("You are not assigned to the default stock location, so you cannot change stock by import. Remove the stock column.");

        var rows = new List<ImportRow>();
        for (var i = 1; i < table.Count; i++)
        {
            var cells = new Dictionary<string, string?>();
            for (var c = 0; c < header.Count && c < table[i].Count; c++)
            {
                var raw = table[i][c].Trim();
                if (header[c].Length == 0 || raw.Length == 0) continue;
                cells[header[c]] = raw.Equals(Clear, StringComparison.OrdinalIgnoreCase) ? null : Csv.Unprotect(raw);
            }
            rows.Add(new ImportRow(i + 1, cells));
        }

        // A parent must exist before its variants, so rows without a parent reference go first; otherwise the file's own order stands.
        var ordered = rows.OrderBy(r => r.IsVariant ? 1 : 0).ThenBy(r => r.Number).ToList();
        var reference = "import-" + Guid.NewGuid().ToString("N")[..8];

        var early = new Dictionary<int, string>();
        foreach (var group in ordered.Where(r => r.Sku is not null).GroupBy(r => r.Sku!, StringComparer.OrdinalIgnoreCase))
            foreach (var again in group.Skip(1)) early[again.Number] = $"The SKU {again.Sku} is already on row {group.First().Number}.";

        // Pass one: everything, for real but inside a transaction that is always rolled back. Nothing a row does can survive it.
        var checkedRows = await RunCheckAsync(ordered, early, request, reference, ct);
        var failed = checkedRows.Count(r => r.Action == "failed");
        var apply = !request.DryRun && (failed == 0 || request.SkipInvalid);
        var results = checkedRows;

        // Pass two: each row on its own, in the order that worked. A row that only fails now (someone else changed the data meanwhile) is reported as failed.
        if (apply)
        {
            results = [];
            foreach (var row in ordered)
            {
                if (checkedRows.First(r => r.Row == row.Number).Action == "failed") { results.Add(checkedRows.First(r => r.Row == row.Number)); continue; }
                results.Add(await ApplyRowAsync(row, request, reference, ct));
            }
        }

        results = [.. results.OrderBy(r => r.Row)];
        var applied = apply && results.Any(r => r.Action != "failed");
        return new ProductImportResultDto(request.DryRun, applied, rows.Count, results.Count(r => r.Action == "created"), results.Count(r => r.Action == "updated"),
            results.Count(r => r.Action == "failed"), results);
    }

    private async Task<List<ProductImportRowDto>> RunCheckAsync(List<ImportRow> ordered, Dictionary<int, string> early, ProductImportRequest request, string reference, CancellationToken ct)
    {
        var results = new List<ProductImportRowDto>();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            results.Clear();
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var n = 0;
                foreach (var row in ordered)
                {
                    if (early.TryGetValue(row.Number, out var problem)) { results.Add(Failed(row, problem)); continue; }
                    var savepoint = "row" + n++;
                    await tx.CreateSavepointAsync(savepoint, ct);
                    try
                    {
                        var outcome = await RunRowAsync(row, request, reference, ct);
                        results.Add(Succeeded(row, outcome));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        db.ChangeTracker.Clear();
                        await tx.RollbackToSavepointAsync(savepoint, ct);
                        results.Add(Failed(row, Describe(ex)));
                    }
                }
            }
            finally
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }
        });
        return results;
    }

    private async Task<ProductImportRowDto> ApplyRowAsync(ImportRow row, ProductImportRequest request, string reference, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            RowOutcome? outcome = null;
            // A row is repeatable (it finds its product by SKU or id, and stock is set to a target, not added to), so after an unknown commit result it may simply run again.
            await strategy.ExecuteInTransactionAsync(async token =>
            {
                db.ChangeTracker.Clear();
                outcome = await RunRowAsync(row, request, reference, token);
                return true;
            }, _ => Task.FromResult(false), ct);
            return Succeeded(row, outcome!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return Failed(row, Describe(ex));
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private static ProductImportRowDto Succeeded(ImportRow row, RowOutcome o) => new(row.Number, row.Sku, o.Created ? "created" : "updated", o.ProductId, []);
    private static ProductImportRowDto Failed(ImportRow row, string error) => new(row.Number, row.Sku, "failed", null, [error]);

    private string Describe(Exception ex)
    {
        switch (ex)
        {
            case RowProblem or PiranhaConflictException: return ex.Message;
            case PiranhaValidationException v: return string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => string.IsNullOrEmpty(e.Key) ? m : $"{e.Key}: {m}")));
            default:
                logger.LogError(ex, "Unexpected error importing a catalogue row");
                return "Unexpected error (see the server log).";
        }
    }

    private async Task<Guid?> FindAsync(string? sku, CancellationToken ct)
    {
        if (sku is null) return null;
        var lower = sku.ToLower();
        return await db.Set<Product>().AsNoTracking().Where(p => p.Sku != null && p.Sku.ToLower() == lower).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<RowOutcome> RunRowAsync(ImportRow row, ProductImportRequest request, string reference, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        ProductDto? existing = null;
        if (row.Has("id"))
        {
            if (!Guid.TryParse(row.Get("id"), out var id)) throw new RowProblem("The id is not a valid id.");
            existing = await products.GetAsync(id, ct) ?? throw new RowProblem("There is no product with that id.");
            if (row.Sku is not null && !row.Sku.Equals(existing.Sku, StringComparison.OrdinalIgnoreCase) && await FindAsync(row.Sku, ct) is { } other && other != existing.Id)
                throw new RowProblem($"The SKU {row.Sku} belongs to a different product.");
        }
        else if (row.Sku is null) throw new RowProblem("Each row needs a sku or an id.");
        else if (await FindAsync(row.Sku, ct) is { } byId) existing = await products.GetAsync(byId, ct);

        var created = existing is null;
        Guid productId;
        if (row.IsVariant) productId = await VariantRowAsync(row, existing, ct);
        else
        {
            if (row.Has("option1_name") || row.Has("option1_value") || row.Has("option2_name") || row.Has("option2_value") || row.Has("option3_name") || row.Has("option3_value"))
                throw new RowProblem("Option values only belong on a variant row, which names its parent in parent_sku or parent_id.");
            productId = (await SaveFieldsAsync(row, existing, ct)).Id;
        }

        // The rest are on a product that now exists.
        if (row.Has("categories"))
        {
            if (row.IsVariant || await variants.GetAsync(productId, ct) is { ParentProductId: not null })
                throw new RowProblem("A variant has no categories of its own: it uses its parent's.");
            await SetCategoriesAsync(productId, row.Get("categories"), ct);
        }
        if (row.Has("barcodes"))
        {
            var codes = (row.Get("barcodes") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            await barcodes.SetAsync(productId, codes, ct);
        }
        if (row.Has("stock")) await SetStockAsync(productId, row, request, reference, ct);

        foreach (var ext in Exts)
        {
            var mine = ext.Columns.Where(row.Has).ToDictionary(c => c, c => row.Get(c));
            if (mine.Count > 0) await ext.ImportAsync(productId, mine, created, ct);
        }
        return new RowOutcome(productId, created);
    }

    private static decimal ParseNumber(string? text, string column)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            throw new RowProblem($"{column} is not a number (use a full stop for decimals, for example 12.50).");
        return n;
    }

    /// <summary>Creates the product, or updates the fields the row has; a blank cell leaves a field alone and <c>[clear]</c> empties it.</summary>
    private async Task<ProductDto> SaveFieldsAsync(ImportRow row, ProductDto? existing, CancellationToken ct)
    {
        string? Pick(string column, string? current) => row.Has(column) ? row.Get(column) : current;
        if (existing is null)
        {
            if (row.Get("name") is null) throw new RowProblem("A new product needs a name.");
            if (row.Get("price") is null) throw new RowProblem("A new product needs a price.");
            return await products.CreateAsync(new ProductInput
            {
                Name = row.Get("name")!, Sku = row.Sku, Description = row.Get("description"), Price = ParseNumber(row.Get("price"), "price"),
                Slug = row.Get("slug"), MetaTitle = row.Get("meta_title"), MetaDescription = row.Get("meta_description"),
            }, ct);
        }
        if (row.Has("name") && row.Get("name") is null) throw new RowProblem("A product cannot have no name.");
        if (row.Has("price") && row.Get("price") is null) throw new RowProblem("A product cannot have no price.");
        var input = new ProductInput
        {
            Name = Pick("name", existing.Name)!, Sku = Pick("sku", existing.Sku), Description = Pick("description", existing.Description),
            Price = row.Has("price") ? ParseNumber(row.Get("price"), "price") : existing.Price,
            Slug = Pick("slug", existing.Slug), MetaTitle = Pick("meta_title", existing.MetaTitle), MetaDescription = Pick("meta_description", existing.MetaDescription),
        };
        return await products.UpdateAsync(existing.Id, input, ct) ?? throw new RowProblem("That product no longer exists.");
    }

    private async Task<Guid> VariantRowAsync(ImportRow row, ProductDto? existing, CancellationToken ct)
    {
        Guid parentId;
        if (row.Has("parent_id"))
        {
            if (!Guid.TryParse(row.Get("parent_id"), out parentId) || await products.GetAsync(parentId, ct) is null) throw new RowProblem("There is no product with that parent_id.");
        }
        else parentId = await FindAsync(row.Get("parent_sku"), ct) ?? throw new RowProblem($"There is no product with the SKU {row.Get("parent_sku")} to be the parent.");
        if (row.Has("parent_id") && row.Has("parent_sku") && await FindAsync(row.Get("parent_sku"), ct) is { } bySku && bySku != parentId)
            throw new RowProblem("parent_id and parent_sku name different products.");

        var parent = (await variants.GetAsync(parentId, ct))!;
        if (parent.ParentProductId is not null) throw new RowProblem("The parent is itself a variant, and a variant cannot have variants.");
        if (existing is not null && existing.Id == parentId) throw new RowProblem("A variant cannot be its own parent.");
        if (row.Sku is null && existing is null) throw new RowProblem("A new variant needs a sku.");

        var pairs = new List<(string Name, string Value)>();
        for (var n = 1; n <= 3; n++)
        {
            var (name, value) = (row.Get($"option{n}_name"), row.Get($"option{n}_value"));
            if (name is null && value is null) continue;
            if (name is null || value is null) throw new RowProblem($"Option {n} needs both option{n}_name and option{n}_value.");
            pairs.Add((name, value));
        }
        if (pairs.Count == 0) throw new RowProblem("A variant row needs its option names and values (option1_name, option1_value, ...).");
        if (pairs.Select(p => p.Name.ToLowerInvariant()).Distinct().Count() != pairs.Count) throw new RowProblem("An option is named twice.");

        // Make the options and values the row mentions, if the parent does not have them yet.
        foreach (var (name, value) in pairs)
        {
            var option = parent.Options.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            parent = option is null
                ? (await variants.AddOptionAsync(parentId, new ProductOptionInput { Name = name, Values = [value] }, ct))!
                : option.Values.Any(v => v.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ? parent
                : (await variants.AddValueAsync(parentId, option.Id, new ProductOptionValueInput { Value = value }, ct))!;
        }
        var valueIds = new List<Guid>();
        foreach (var option in parent.Options)
        {
            var pair = pairs.FirstOrDefault(p => p.Name.Equals(option.Name, StringComparison.OrdinalIgnoreCase));
            if (pair.Name is null) throw new RowProblem($"Give a value for the option {option.Name}: the parent has it.");
            valueIds.Add(option.Values.First(v => v.Value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)).Id);
        }

        if (existing is not null)
        {
            var info = await variants.GetAsync(existing.Id, ct);
            if (info!.ParentProductId != parentId) throw new RowProblem("That product is not a variant of this parent, so it cannot be turned into one here.");
            if (!info.Values.Select(v => v.ValueId).Order().SequenceEqual(valueIds.Order()))
                throw new RowProblem("A variant's option values cannot be changed by import: delete the variant and add it again.");
            await SaveFieldsAsync(row, existing, ct);
            return existing.Id;
        }

        var made = await variants.AddVariantAsync(parentId, new ProductVariantInput
        {
            ValueIds = valueIds, Sku = row.Sku, Price = row.Has("price") && row.Get("price") is not null ? ParseNumber(row.Get("price"), "price") : null,
        }, ct);
        var variant = made!.Variants.First(v => string.Equals(v.Sku, row.Sku, StringComparison.OrdinalIgnoreCase));
        var dto = await products.GetAsync(variant.ProductId, ct);
        if (row.Has("name") || row.Has("description") || row.Has("slug") || row.Has("meta_title") || row.Has("meta_description")) await SaveFieldsAsync(row, dto, ct);
        return variant.ProductId;
    }

    private async Task SetCategoriesAsync(Guid productId, string? cell, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (cell is not null)
        {
            var all = (await categories.ListAsync(ct)).ToList();
            foreach (var path in cell.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Guid? parent = null;
                foreach (var name in path.Split('>', StringSplitOptions.TrimEntries))
                {
                    if (name.Length == 0) throw new RowProblem($"The category path \"{path}\" has an empty part.");
                    var match = all.FirstOrDefault(c => c.ParentId == parent && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        match = await categories.CreateAsync(new CategoryInput { Name = name, ParentId = parent }, ct); // categories the file names are made when missing
                        all.Add(match);
                    }
                    parent = match.Id;
                }
                if (parent is { } leaf && !ids.Contains(leaf)) ids.Add(leaf);
            }
        }
        await categories.SetProductCategoriesAsync(productId, ids, ct);
    }

    private async Task SetStockAsync(Guid productId, ImportRow row, ProductImportRequest request, string reference, CancellationToken ct)
    {
        if (row.Get("stock") is null) throw new RowProblem("Stock cannot be cleared: give the quantity.");
        var target = ParseNumber(row.Get("stock"), "stock");
        if (target < 0 || decimal.Round(target, 4) != target || target > StockAdjustmentInput.MaxMagnitude)
            throw new RowProblem("Stock must be a quantity of zero or more, with up to 4 decimal places.");
        var current = await db.Set<StockLevel>().AsNoTracking().Where(l => l.ProductId == productId && l.LocationId == StockLocations.DefaultId).Select(l => (decimal?)l.Quantity).FirstOrDefaultAsync(ct) ?? 0m;
        var delta = target - current;
        if (delta == 0) return;
        // Set to a target, so a repeated row changes nothing. The movement is in the ledger like any other change.
        await stock.EnsureLevelsAsync([productId], StockLocations.DefaultId, ct);
        await stock.ApplyInTransactionAsync([new StockChange(productId, delta, "Import", reference, row.Sku)], StockLocations.DefaultId, allowNegative: false, request.Actor, ct);
        await db.SaveChangesAsync(ct);
    }
}
