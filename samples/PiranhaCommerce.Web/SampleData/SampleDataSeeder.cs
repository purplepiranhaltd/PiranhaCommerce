using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.SampleData;

/// <summary>Settings under "Piranha:SampleData". Off unless a host turns it on.</summary>
public sealed class SampleDataOptions
{
    public const string SectionName = "Piranha:SampleData";

    /// <summary>Fills an empty shop with the sample cosmetics catalogue at startup. For development and demonstrations only.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Fills the shop with a small invented cosmetics catalogue (brand "Petalwick"): categories, products with shades and sizes, pictures drawn on the fly,
/// stock, a sale, a promo code, delivery zones with rates, VAT, three shoppers and some reviews. Everything goes through the same services the API and admin
/// use, so the sample exercises the real rules. Safe to run again: anything already there (found by its address, code or name) is left alone.
/// </summary>
public sealed class SampleDataSeeder(
    IServiceProvider services, ILogger<SampleDataSeeder> log)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var categories = sp.GetRequiredService<ICategoryService>();
        var products = sp.GetRequiredService<IProductService>();

        var categoryIds = new Dictionary<string, Guid>();
        foreach (var c in SampleCatalogue.Categories)
        {
            var existing = await categories.GetBySlugAsync(c.Slug, ct);
            var parent = c.Parent is null ? (Guid?)null : categoryIds[c.Parent];
            existing ??= await categories.CreateAsync(new CategoryInput
            {
                Name = c.Name, Description = c.Description, Slug = c.Slug, ParentId = parent, DisplayOrder = categoryIds.Count,
                MetaTitle = $"{c.Name} | {SampleCatalogue.Brand}", MetaDescription = c.Description,
            }, ct);
            categoryIds[c.Slug] = existing.Id;
        }

        var productIds = new Dictionary<string, Guid>();
        var created = 0;
        foreach (var p in SampleCatalogue.Products)
        {
            if (await products.GetBySlugAsync(p.Slug, ct) is { } already) { productIds[p.Slug] = already.Id; continue; }
            productIds[p.Slug] = await CreateProductAsync(sp, p, categoryIds[p.Category], ct);
            created++;
        }

        await SeedBrandsAsync(sp, productIds, ct);
        await SeedSpecsAsync(sp, productIds, ct);
        await SeedRelationsAsync(sp, productIds, ct);
        await SeedStoreSettingsAsync(sp, categoryIds, ct);
        await SeedShoppersAndReviewsAsync(sp, productIds, ct);
        log.LogInformation("Sample data: {Created} products added ({Total} in the sample catalogue).", created, SampleCatalogue.Products.Count);
    }

    private static async Task<Guid> CreateProductAsync(IServiceProvider sp, SampleProduct p, Guid categoryId, CancellationToken ct)
    {
        var products = sp.GetRequiredService<IProductService>();
        var listings = sp.GetRequiredService<IProductListingService>();
        var variants = sp.GetRequiredService<IProductVariantService>();
        var stock = sp.GetRequiredService<IStockService>();
        var images = sp.GetRequiredService<IProductImageService>();

        var firstLine = p.Description.Split('\n')[0];
        var product = await products.CreateAsync(new ProductInput
        {
            Name = p.Name, Sku = p.Sku, Description = p.Description, Price = p.Price, Slug = p.Slug,
            MetaTitle = $"{p.Name} | {SampleCatalogue.Brand}", MetaDescription = firstLine,
        }, ct);
        await sp.GetRequiredService<ICategoryService>().SetProductCategoriesAsync(product.Id, [categoryId], ct);
        await listings.SetAsync(product.Id, new ProductListingInput { Published = true, WeightGrams = p.WeightGrams }, ct); // before the variants, which copy it

        async Task Picture(Guid productId, string kind, IReadOnlyList<string> colours, string alt)
        {
            await using var stream = new MemoryStream(SampleImages.Render(kind, colours));
            await images.AddAsync(productId, stream, $"{p.Slug}.jpg", alt, ct);
        }

        if (p.Shades is { Count: > 0 } shades)
        {
            await variants.AddOptionAsync(product.Id, new ProductOptionInput { Name = p.OptionName, Values = shades.Select(s => s.Name).ToList() }, ct);
            var set = await variants.GenerateAsync(product.Id, new GenerateVariantsInput { Price = p.Price }, ct)
                ?? throw new InvalidOperationException($"Could not make the variants of {p.Name}.");
            var number = 0;
            foreach (var v in set.Variants)
            {
                var shade = shades.First(s => s.Name == v.Values[0].Value);
                number++;
                await products.UpdateAsync(v.ProductId, new ProductInput { Name = v.Name, Sku = $"{p.Sku}-{number:00}", Description = p.Description, Price = shade.Price ?? p.Price }, ct);
                if (shade.Stock > 0) await stock.AdjustAsync(v.ProductId, new StockAdjustmentInput { Delta = shade.Stock, Reason = "Sample data" }, "sample data", ct);
                await Picture(v.ProductId, p.Kind, [shade.Hex], $"{p.Name} in {shade.Name}");
                if (p.Slug == "precision-lip-liner")
                    await sp.GetRequiredService<IPriceBreakService>().SetAsync(v.ProductId, new PriceBreaksInput { Breaks = [new PriceBreakInput { MinQuantity = 3, Percent = 15m }] }, ct);
            }
            if (p.OptionName == "Shade")
            {
                await Picture(product.Id, p.Kind, shades.Select(s => s.Hex).ToList(), p.Name);
                if (shades.Count > 1) await Picture(product.Id, "swatches", shades.Select(s => s.Hex).ToList(), $"The {shades.Count} shades of {p.Name}");
            }
            else await Picture(product.Id, p.Kind, [shades[0].Hex], p.Name);
        }
        else
        {
            if (p.Stock > 0) await stock.AdjustAsync(product.Id, new StockAdjustmentInput { Delta = p.Stock, Reason = "Sample data" }, "sample data", ct);
            await Picture(product.Id, p.Kind, p.Colours ?? [], p.Name);
        }
        return product.Id;
    }

    /// <summary>Makes the labels and gives each product its label, unless it already has one (so staff changes, and a second run, are left alone).</summary>
    private static async Task SeedBrandsAsync(IServiceProvider sp, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        var brands = sp.GetRequiredService<IBrandService>();
        var brandIds = new Dictionary<string, Guid>();
        var order = 0;
        foreach (var b in SampleCatalogue.Brands)
        {
            var existing = await brands.GetBySlugAsync(b.Slug, ct)
                ?? await brands.CreateAsync(new BrandInput { Name = b.Name, Slug = b.Slug, Description = b.Description, DisplayOrder = order, MetaTitle = $"{b.Name} | {SampleCatalogue.Brand}", MetaDescription = b.Description }, ct);
            brandIds[b.Slug] = existing.Id;
            order++;
        }
        foreach (var (productSlug, brandSlug) in SampleCatalogue.BrandOf)
            if (ids.TryGetValue(productSlug, out var productId) && (await brands.GetProductBrandAsync(productId, ct))?.BrandId is null)
                await brands.SetProductBrandAsync(productId, brandIds[brandSlug], ct);
    }

    /// <summary>Makes the attributes and their answers, then gives each product its answers unless it already has some (so staff changes, and a second run, are left alone).</summary>
    private static async Task SeedSpecsAsync(IServiceProvider sp, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        var specs = sp.GetRequiredService<ISpecificationService>();
        var valueIds = new Dictionary<(string Attribute, string Value), Guid>();
        var order = 0;
        foreach (var s in SampleCatalogue.Specs)
        {
            var attribute = (await specs.ListAsync(ct)).FirstOrDefault(a => string.Equals(a.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                ?? await specs.CreateAsync(new SpecAttributeInput { Name = s.Name, DisplayOrder = order, Filterable = s.Filterable }, ct);
            order++;
            var valueOrder = 0;
            foreach (var v in s.Values)
            {
                var existing = attribute.Values.FirstOrDefault(x => string.Equals(x.Value, v, StringComparison.OrdinalIgnoreCase));
                var id = existing?.Id ?? (await specs.AddValueAsync(attribute.Id, new SpecValueInput { Value = v, DisplayOrder = valueOrder }, ct))!.Id;
                valueIds[(s.Name, v)] = id;
                valueOrder++;
            }
        }
        foreach (var (productSlug, answers) in SampleCatalogue.SpecsOf)
            if (ids.TryGetValue(productSlug, out var productId) && (await specs.GetProductAsync(productId, ct))?.ValueIds.Count == 0)
                await specs.SetProductAsync(productId, answers.SelectMany(a => a.Values.Select(v => valueIds[(a.Attribute, v)])).ToList(), ct);
    }

    private static async Task SeedRelationsAsync(IServiceProvider sp, Dictionary<string, Guid> ids, CancellationToken ct)
    {
        var relations = sp.GetRequiredService<IProductRelationService>();
        foreach (var (slug, related, crossSell) in SampleCatalogue.Relations)
        {
            var current = await relations.GetAsync(ids[slug], ct);
            if (current is { Related.Count: > 0 } or { CrossSell.Count: > 0 }) continue;
            await relations.SetAsync(ids[slug], new ProductRelationsInput { Related = related.Select(s => ids[s]).ToList(), CrossSell = crossSell.Select(s => ids[s]).ToList() }, ct);
        }
    }

    private static async Task SeedStoreSettingsAsync(IServiceProvider sp, Dictionary<string, Guid> categoryIds, CancellationToken ct)
    {
        // Tax: the default class at 20% for the UK, nothing elsewhere.
        var tax = sp.GetRequiredService<ITaxService>();
        if (!(await tax.ListAsync(ct)).Any(r => r.CountryCode == "GB"))
            await tax.CreateAsync(new TaxRuleInput { CountryCode = "GB", Name = "VAT", Rate = 20m }, ct);

        // Delivery: two zones, three methods, each priced by zone.
        var zones = sp.GetRequiredService<IShippingZoneService>();
        var shipping = sp.GetRequiredService<IShippingService>();
        var existingZones = await zones.ListAsync(ct);
        var uk = existingZones.FirstOrDefault(z => z.Name == "United Kingdom")
            ?? await zones.CreateAsync(new ShippingZoneInput { Name = "United Kingdom", Position = 1, Regions = [new ShippingZoneRegionInput { CountryCode = "GB" }] }, ct);
        var world = existingZones.FirstOrDefault(z => z.Name == "Rest of the world")
            ?? await zones.CreateAsync(new ShippingZoneInput { Name = "Rest of the world", Position = 2, Regions = [new ShippingZoneRegionInput { CountryCode = "*" }] }, ct);
        var methods = await shipping.ListAsync(false, ct);
        async Task Method(string name, decimal price, decimal? freeOver, int position, Guid zone)
        {
            if (methods.Any(m => m.Name == name)) return;
            var m = await shipping.CreateAsync(new ShippingMethodInput { Name = name, Price = price, FreeOver = freeOver, Position = position }, ct);
            await shipping.SetRatesAsync(m.Id, new ShippingRatesInput { Rates = [new ShippingRateInput { ZoneId = zone, Price = price }] }, ct);
        }
        await Method("Standard delivery (3-5 working days)", 3.95m, 40m, 1, uk.Id);
        await Method("Next working day delivery", 6.95m, null, 2, uk.Id);
        await Method("International tracked delivery (5-10 working days)", 12.95m, null, 3, world.Id);

        // A promo code, and an eye make-up sale.
        var promotions = sp.GetRequiredService<IPromotionService>();
        if (!(await promotions.ListAsync(ct)).Any(p => p.Code.Equals("WELCOME10", StringComparison.OrdinalIgnoreCase)))
            await promotions.CreateAsync(new PromotionInput { Code = "WELCOME10", Name = "10% off your first order", Kind = PromotionKind.Percent, Value = 10m, MinSubtotal = 20m }, ct);
        var sales = sp.GetRequiredService<ISaleService>();
        if (!(await sales.ListAsync(null, ct)).Any(s => s.Name == "Autumn eye event"))
        {
            var sale = await sales.CreateAsync(new SaleInput { Name = "Autumn eye event", StartsUtc = DateTimeOffset.UtcNow.AddDays(-1), EndsUtc = DateTimeOffset.UtcNow.AddDays(60), Percent = 20m }, ct);
            await sales.AddCategoryAsync(sale.Id, categoryIds["eyes"], ct);
        }
    }

    private static async Task SeedShoppersAndReviewsAsync(IServiceProvider sp, Dictionary<string, Guid> productIds, CancellationToken ct)
    {
        var customers = sp.GetRequiredService<ICustomerService>();
        var accounts = sp.GetRequiredService<ICustomerAccountService>();
        var reviews = sp.GetRequiredService<IReviewService>();

        var shopperIds = new List<Guid>();
        foreach (var s in SampleCatalogue.Shoppers)
        {
            var found = (await customers.ListAsync(1, 5, s.Email, ct)).Items.FirstOrDefault(c => c.Email.Equals(s.Email, StringComparison.OrdinalIgnoreCase));
            if (found is not null) { shopperIds.Add(found.Id); continue; }
            var profile = await accounts.RegisterAsync(new RegisterInput { Email = s.Email, Password = SampleCatalogue.ShopperPassword, FirstName = s.FirstName, LastName = s.LastName }, ct);
            shopperIds.Add(profile.Id);
        }

        foreach (var (slug, shopper, rating, title, body) in SampleCatalogue.Reviews)
        {
            try
            {
                var review = await reviews.CreateAsync(shopperIds[shopper], new ReviewInput { ProductId = productIds[slug], Rating = rating, Title = title, Body = body }, ct);
                await reviews.SetStatusAsync(review.Id, ReviewStatus.Approved, "sample data", ct);
            }
            catch (PiranhaConflictException) { /* already reviewed on an earlier run */ }
        }
    }
}
