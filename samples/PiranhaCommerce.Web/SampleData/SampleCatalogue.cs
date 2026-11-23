namespace PiranhaCommerce.Web.SampleData;

/// <summary>One variant of a sample product: a shade (or a size). <see cref="Price"/> empty means the product's price.</summary>
internal sealed record Shade(string Name, string Hex, int Stock, decimal? Price = null);

/// <param name="Kind">Which picture to draw (see <see cref="SampleImages"/>).</param>
/// <param name="Colours">For a product without shades: the colours shown in its picture.</param>
/// <param name="Stock">For a product without shades: how many are on the shelf.</param>
internal sealed record SampleProduct(
    string Slug, string Name, string Sku, decimal Price, string Category, string Kind, string Description, int WeightGrams,
    IReadOnlyList<Shade>? Shades = null, string OptionName = "Shade", int Stock = 0, IReadOnlyList<string>? Colours = null);

internal sealed record SampleCategory(string Slug, string Name, string Description, string? Parent = null);

/// <summary>
/// The sample shop's catalogue: a small, invented cosmetics brand, "Petalwick". Every name, description and review is made up for this sample.
/// </summary>
internal static class SampleCatalogue
{
    public const string Brand = "Petalwick";

    public static readonly IReadOnlyList<SampleCategory> Categories =
    [
        new("lips", "Lips", "Lipsticks, glosses and liners in shades for every mood."),
        new("lipsticks", "Lipsticks", "Matte and satin lipsticks that last all day.", "lips"),
        new("lip-gloss", "Lip gloss", "Glossy, comfortable colour.", "lips"),
        new("lip-liners", "Lip liners", "Define and set your lipstick.", "lips"),
        new("eyes", "Eyes", "Mascara and eyeshadow palettes."),
        new("mascara", "Mascara", "Length, lift and definition.", "eyes"),
        new("eyeshadow", "Eyeshadow palettes", "Blendable shades in ready-made palettes.", "eyes"),
        new("face", "Face", "Foundation, blush and highlighter."),
        new("foundation", "Foundation", "Skin-like coverage in eight shades.", "face"),
        new("blush-highlighter", "Blush and highlighter", "Colour and glow for cheeks.", "face"),
        new("skin", "Skin", "Simple, gentle skincare."),
        new("cleansers", "Cleansers", "Clean skin without the tight feeling.", "skin"),
        new("moisturisers", "Moisturisers", "Everyday hydration.", "skin"),
        new("serums", "Serums and protection", "Targeted care and daily sun protection.", "skin"),
        new("gift-sets", "Gift sets", "Ready to give."),
    ];

    public static readonly IReadOnlyList<SampleProduct> Products =
    [
        new("velvet-matte-lipstick", "Velvet Matte Lipstick", "PW-LIP-VM", 14.00m, "lipsticks", "lipstick",
            "A weightless matte lipstick with rich, even colour.\nComfortable for hours, with a soft, velvety finish that does not dry the lips.", 30,
            [new("Ruby Red", "#B3122E", 40), new("Coral Crush", "#F2644F", 32), new("Rosewood", "#9C4A4F", 25), new("Nude Blush", "#D9A08E", 3), new("Berry Bold", "#8E1C4D", 18), new("Plum Noir", "#4A1330", 0)]),
        new("satin-cream-lipstick", "Satin Cream Lipstick", "PW-LIP-SC", 13.00m, "lipsticks", "lipstick",
            "A creamy lipstick with a soft satin shine.\nBuildable colour from sheer to bold, enriched with shea butter.", 30,
            [new("Peony Pink", "#E8789C", 28), new("Cherry Pop", "#C2182B", 20), new("Mocha Mauve", "#A86F7A", 14), new("Soft Petal", "#E9B5B0", 22), new("Hot Coral", "#FF6F59", 9)]),
        new("hydra-shine-lip-gloss", "Hydra Shine Lip Gloss", "PW-LIP-GL", 9.50m, "lip-gloss", "gloss",
            "A non-sticky gloss that leaves lips plump and glossy.\nWear it alone or over lipstick.", 20,
            [new("Clear Dew", "#F4DDE0", 35), new("Rosy Glaze", "#E88AA2", 27), new("Peach Glow", "#F4A582", 16), new("Cherry Gloss", "#C73A52", 11)]),
        new("precision-lip-liner", "Precision Lip Liner", "PW-LIP-LN", 7.50m, "lip-liners", "liner",
            "A creamy pencil that glides on and stays put.\nBuy three or more and save.", 10,
            [new("Ruby", "#A3122A", 30), new("Rosewood", "#8F4348", 30), new("Nude", "#C98F7C", 30), new("Berry", "#7C1B45", 30)]),
        new("lash-lift-mascara", "Lash Lift Mascara", "PW-EYE-MA", 12.50m, "mascara", "mascara",
            "Lifts, lengthens and separates without clumping.\nA flexible brush reaches every lash.", 25,
            [new("Black", "#1A1A1A", 50), new("Brown", "#4B2E22", 26)]),
        new("sunset-eyeshadow-palette", "Sunset Eyeshadow Palette", "PW-EYE-SS", 24.00m, "eyeshadow", "palette",
            "Eight warm shades from soft peach to deep terracotta.\nMatte and shimmer finishes that blend easily.", 120, Stock: 15,
            Colours: ["#F4A261", "#E76F51", "#E9C46A", "#9C4A4F", "#7A3B3F", "#D68C45"]),
        new("smoky-nights-eyeshadow-palette", "Smoky Nights Eyeshadow Palette", "PW-EYE-SN", 24.00m, "eyeshadow", "palette",
            "Eight cool greys, charcoals and silvers for a classic smoky eye.", 120, Stock: 8,
            Colours: ["#2B2D42", "#4A4E69", "#8D99AE", "#C9C9D4", "#1B1B2F", "#6D6875"]),
        new("rose-quartz-eyeshadow-palette", "Rose Quartz Eyeshadow Palette", "PW-EYE-RQ", 24.00m, "eyeshadow", "palette",
            "Eight soft pinks and rosy neutrals for a fresh, romantic look.", 120, Stock: 0,
            Colours: ["#F4C2C2", "#E8A0B4", "#D17A9B", "#F7D9DD", "#B76E79", "#E5989B"]),
        new("skin-glow-foundation", "Skin Glow Foundation", "PW-FACE-FD", 22.00m, "foundation", "foundation",
            "A lightweight, buildable foundation with a natural, healthy glow.\nTwenty-four hour comfort, in eight shades from porcelain to espresso.", 90,
            [new("Porcelain", "#F6DFCB", 20), new("Ivory", "#F0D1B5", 20), new("Sand", "#E3BC98", 20), new("Honey", "#D1A074", 20), new("Caramel", "#B98058", 20), new("Amber", "#9D6540", 20), new("Mocha", "#7A4B33", 20), new("Espresso", "#55331F", 20)]),
        new("cheek-bloom-blush", "Cheek Bloom Blush", "PW-FACE-BL", 15.00m, "blush-highlighter", "blush",
            "A silky powder blush that melts into skin for a natural flush.", 40,
            [new("Petal", "#F4A9B8", 25), new("Peach Fizz", "#F6B08C", 25), new("Berry Flush", "#C95D78", 17), new("Rosy Nude", "#D99A8F", 19)]),
        new("luminous-highlighter", "Luminous Highlighter", "PW-FACE-HL", 16.00m, "blush-highlighter", "highlighter",
            "A finely milled highlighter for a soft, lit-from-within glow.", 40,
            [new("Champagne", "#F3E1C0", 20), new("Rose Gold", "#E8B4A0", 20), new("Bronze Glow", "#C99A62", 12)]),
        new("gentle-cream-cleanser", "Gentle Cream Cleanser 150ml", "PW-SKIN-CL", 11.00m, "cleansers", "cleanser",
            "A soft cream that removes make-up and the day without stripping skin.\nFragrance free. 150ml.", 190, Stock: 60, Colours: ["#BFD8D2"]),
        new("daily-moisture-cream", "Daily Moisture Cream", "PW-SKIN-MC", 18.00m, "moisturisers", "cream",
            "A light, non-greasy cream with hyaluronic acid for all-day hydration.\nChoose a 50ml jar or the 100ml value size.", 150,
            [new("50ml", "#E8A0B4", 40, 18.00m), new("100ml", "#E8A0B4", 22, 30.00m)], OptionName: "Size"),
        new("vitamin-c-serum", "Vitamin C Serum 30ml", "PW-SKIN-VC", 26.00m, "serums", "serum",
            "A brightening serum with stabilised vitamin C.\nUse in the morning under moisturiser. 30ml.", 80, Stock: 4, Colours: ["#C9822B"]),
        new("spf-30-daily-fluid", "SPF 30 Daily Fluid 50ml", "PW-SKIN-SP", 17.00m, "serums", "spf",
            "A sheer, lightweight sun protection that sits well under make-up. 50ml.", 90, Stock: 35, Colours: ["#F29B38"]),
        new("everyday-lip-trio-gift-set", "Everyday Lip Trio Gift Set", "PW-GIFT-LT", 32.00m, "gift-sets", "giftset",
            "A lipstick, a gloss and a liner in a matching trio of shades, in a gift box.\nOur best-loved lip products, ready to give.", 160, Stock: 12, Colours: ["#B3122E", "#E88AA2", "#A3122A"]),
    ];

    /// <summary>Products suggested alongside others: (product slug, related slugs, cross-sell slugs).</summary>
    public static readonly IReadOnlyList<(string Slug, string[] Related, string[] CrossSell)> Relations =
    [
        ("velvet-matte-lipstick", ["satin-cream-lipstick", "hydra-shine-lip-gloss"], ["precision-lip-liner", "hydra-shine-lip-gloss"]),
        ("satin-cream-lipstick", ["velvet-matte-lipstick", "hydra-shine-lip-gloss"], ["precision-lip-liner"]),
        ("hydra-shine-lip-gloss", ["velvet-matte-lipstick", "satin-cream-lipstick"], ["precision-lip-liner"]),
        ("skin-glow-foundation", ["cheek-bloom-blush", "luminous-highlighter"], ["cheek-bloom-blush", "luminous-highlighter", "lash-lift-mascara"]),
        ("cheek-bloom-blush", ["luminous-highlighter", "skin-glow-foundation"], ["luminous-highlighter"]),
        ("lash-lift-mascara", ["sunset-eyeshadow-palette", "smoky-nights-eyeshadow-palette"], ["sunset-eyeshadow-palette"]),
        ("gentle-cream-cleanser", ["daily-moisture-cream", "vitamin-c-serum"], ["daily-moisture-cream"]),
        ("daily-moisture-cream", ["vitamin-c-serum", "spf-30-daily-fluid"], ["spf-30-daily-fluid", "vitamin-c-serum"]),
    ];

    /// <summary>The labels the shop stocks. All invented for this sample; none is a real company or product line.</summary>
    public sealed record SampleBrand(string Slug, string Name, string Description);

    public static readonly IReadOnlyList<SampleBrand> Brands =
    [
        new("petalwick", "Petalwick", "Our own range: lipsticks, glosses and everyday colour."),
        new("marlowe-and-finch", "Marlowe & Finch", "Eye makeup made for long days: mascara and blendable palettes."),
        new("juniper-row", "Juniper Row", "Skin-first face makeup."),
        new("fern-and-ash", "Fern & Ash", "Gentle, simple skincare."),
    ];

    /// <summary>The facts the shop records about products: each with its fixed list of answers. Shoppers filter by those marked filterable.</summary>
    public sealed record SampleSpec(string Name, bool Filterable, params string[] Values);

    public static readonly IReadOnlyList<SampleSpec> Specs =
    [
        new("Finish", true, "Matte", "Satin", "Glossy", "Shimmer", "Dewy"),
        new("Coverage", true, "Sheer", "Medium", "Full"),
        new("Skin type", true, "All skin types", "Dry", "Oily", "Combination", "Sensitive"),
        new("Vegan", true, "Yes"),
        new("Key ingredient", false, "Shea butter", "Hyaluronic acid", "Vitamin C", "Mineral SPF"),
    ];

    /// <summary>The answers each product has, by product slug: attribute name, then its values.</summary>
    public static readonly IReadOnlyDictionary<string, (string Attribute, string[] Values)[]> SpecsOf = new Dictionary<string, (string, string[])[]>
    {
        ["velvet-matte-lipstick"] = [("Finish", ["Matte"]), ("Vegan", ["Yes"]), ("Key ingredient", ["Shea butter"])],
        ["satin-cream-lipstick"] = [("Finish", ["Satin"]), ("Key ingredient", ["Shea butter"])],
        ["hydra-shine-lip-gloss"] = [("Finish", ["Glossy"]), ("Vegan", ["Yes"])],
        ["precision-lip-liner"] = [("Finish", ["Matte"]), ("Vegan", ["Yes"])],
        ["lash-lift-mascara"] = [("Finish", ["Matte"]), ("Vegan", ["Yes"])],
        ["sunset-eyeshadow-palette"] = [("Finish", ["Matte", "Shimmer"])],
        ["smoky-nights-eyeshadow-palette"] = [("Finish", ["Matte", "Shimmer"]), ("Vegan", ["Yes"])],
        ["rose-quartz-eyeshadow-palette"] = [("Finish", ["Satin", "Shimmer"])],
        ["skin-glow-foundation"] = [("Finish", ["Dewy"]), ("Coverage", ["Medium"]), ("Skin type", ["Dry", "Combination"])],
        ["cheek-bloom-blush"] = [("Finish", ["Satin"]), ("Coverage", ["Sheer"]), ("Vegan", ["Yes"])],
        ["luminous-highlighter"] = [("Finish", ["Shimmer", "Dewy"]), ("Coverage", ["Sheer"])],
        ["gentle-cream-cleanser"] = [("Skin type", ["All skin types"]), ("Vegan", ["Yes"])],
        ["daily-moisture-cream"] = [("Skin type", ["Dry", "Sensitive"]), ("Key ingredient", ["Hyaluronic acid"]), ("Vegan", ["Yes"])],
        ["vitamin-c-serum"] = [("Skin type", ["All skin types"]), ("Key ingredient", ["Vitamin C"])],
        ["spf-30-daily-fluid"] = [("Skin type", ["Oily", "Combination"]), ("Key ingredient", ["Mineral SPF"]), ("Vegan", ["Yes"])],
    };

    /// <summary>Which label each product belongs to, by product slug then brand slug.</summary>
    public static readonly IReadOnlyDictionary<string, string> BrandOf = new Dictionary<string, string>
    {
        ["velvet-matte-lipstick"] = "petalwick", ["satin-cream-lipstick"] = "petalwick", ["hydra-shine-lip-gloss"] = "petalwick",
        ["precision-lip-liner"] = "petalwick", ["everyday-lip-trio-gift-set"] = "petalwick", ["cheek-bloom-blush"] = "petalwick",
        ["lash-lift-mascara"] = "marlowe-and-finch", ["sunset-eyeshadow-palette"] = "marlowe-and-finch",
        ["smoky-nights-eyeshadow-palette"] = "marlowe-and-finch", ["rose-quartz-eyeshadow-palette"] = "marlowe-and-finch",
        ["skin-glow-foundation"] = "juniper-row", ["luminous-highlighter"] = "juniper-row",
        ["gentle-cream-cleanser"] = "fern-and-ash", ["daily-moisture-cream"] = "fern-and-ash",
        ["vitamin-c-serum"] = "fern-and-ash", ["spf-30-daily-fluid"] = "fern-and-ash",
    };

    public sealed record SampleShopper(string Email, string FirstName, string LastName);

    public static readonly IReadOnlyList<SampleShopper> Shoppers =
    [
        new("demo@example.com", "Demo", "Shopper"),
        new("maya@example.com", "Maya", "Patel"),
        new("jess@example.com", "Jess", "Turner"),
    ];

    public const string ShopperPassword = "Demo-Passw0rd!";

    /// <summary>(product slug, shopper index, stars, title, text)</summary>
    public static readonly IReadOnlyList<(string Slug, int Shopper, int Rating, string Title, string Body)> Reviews =
    [
        ("velvet-matte-lipstick", 1, 5, "My new everyday red", "Ruby Red is the perfect true red. It really does stay put and it does not feel dry at all."),
        ("velvet-matte-lipstick", 2, 4, "Lovely colour", "Beautiful finish. I would love a few more nude shades."),
        ("velvet-matte-lipstick", 0, 5, "Worth it", "I bought two. Goes on smoothly and lasts through lunch."),
        ("satin-cream-lipstick", 1, 4, "So comfortable", "Peony Pink is gorgeous and very comfortable to wear."),
        ("skin-glow-foundation", 2, 5, "Finally my shade", "Eight shades is a good range and Honey matches me exactly. Natural, not cakey."),
        ("skin-glow-foundation", 1, 4, "Light and glowy", "Great for everyday. I use a little concealer on top."),
        ("cheek-bloom-blush", 2, 5, "Pretty flush", "Petal gives just enough colour. Blends in seconds."),
        ("vitamin-c-serum", 0, 4, "Noticeable glow", "My skin looks brighter after a few weeks. A little goes a long way."),
        ("lash-lift-mascara", 1, 3, "Good but not dramatic", "Separates well but I want more volume. Nice and easy to remove."),
    ];
}
