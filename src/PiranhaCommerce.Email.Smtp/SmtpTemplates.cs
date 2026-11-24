using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using Fluid;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Email.Smtp;

/// <summary>
/// The words of one email, kept by the SMTP package (so they are edited in its admin section). The key is the email's template key.
/// Subject, text and HTML are Liquid templates over the email's variables. Only edited templates have a row: the rest use the built-in
/// defaults, and "reset" simply deletes the row.
/// </summary>
public class SmtpEmailTemplate
{
    public required string Key { get; set; }
    public required string Subject { get; set; }
    public required string TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

internal sealed class SmtpModelContributor : IModelContributor
{
    public const string Schema = "email";

    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SmtpEmailTemplate>(e =>
        {
            e.ToTable("SmtpTemplate", Schema);
            e.HasKey(t => t.Key);
            e.Property(t => t.Key).HasMaxLength(100);
            e.Property(t => t.Subject).HasMaxLength(250);
            e.Property(t => t.UpdatedBy).HasMaxLength(256);
        });
    }
}

public sealed record StoredSmtpTemplate(string Key, string Subject, string TextBody, string? HtmlBody, DateTimeOffset UpdatedUtc, string? UpdatedBy);

/// <summary>Where edited templates live. A seam, so the sender can be tested without a database.</summary>
public interface ISmtpTemplateStore
{
    Task<StoredSmtpTemplate?> FindAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<StoredSmtpTemplate>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(StoredSmtpTemplate template, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
}

internal sealed class EfSmtpTemplateStore(PiranhaCommerceDbContext db) : ISmtpTemplateStore
{
    private static StoredSmtpTemplate ToStored(SmtpEmailTemplate t) => new(t.Key, t.Subject, t.TextBody, t.HtmlBody, t.UpdatedUtc, t.UpdatedBy);

    public async Task<StoredSmtpTemplate?> FindAsync(string key, CancellationToken ct = default)
    {
        var t = await db.Set<SmtpEmailTemplate>().AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return t is null ? null : ToStored(t);
    }

    public async Task<IReadOnlyList<StoredSmtpTemplate>> ListAsync(CancellationToken ct = default) =>
        (await db.Set<SmtpEmailTemplate>().AsNoTracking().ToListAsync(ct)).Select(ToStored).ToList();

    public async Task SaveAsync(StoredSmtpTemplate t, CancellationToken ct = default)
    {
        // Update in place; insert only when there is no row. Two people saving at once: the later write wins, which is what editing a template means.
        var changed = await db.Set<SmtpEmailTemplate>().Where(x => x.Key == t.Key).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Subject, t.Subject).SetProperty(x => x.TextBody, t.TextBody).SetProperty(x => x.HtmlBody, t.HtmlBody)
            .SetProperty(x => x.UpdatedUtc, t.UpdatedUtc).SetProperty(x => x.UpdatedBy, t.UpdatedBy), ct);
        if (changed > 0) return;

        db.Add(new SmtpEmailTemplate { Key = t.Key, Subject = t.Subject, TextBody = t.TextBody, HtmlBody = t.HtmlBody, UpdatedUtc = t.UpdatedUtc, UpdatedBy = t.UpdatedBy });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A simultaneous first save created it: update that one instead.
            db.ChangeTracker.Clear();
            await db.Set<SmtpEmailTemplate>().Where(x => x.Key == t.Key).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Subject, t.Subject).SetProperty(x => x.TextBody, t.TextBody).SetProperty(x => x.HtmlBody, t.HtmlBody)
                .SetProperty(x => x.UpdatedUtc, t.UpdatedUtc).SetProperty(x => x.UpdatedBy, t.UpdatedBy), ct);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
        await db.Set<SmtpEmailTemplate>().Where(x => x.Key == key).ExecuteDeleteAsync(ct) > 0;
}

/// <summary>
/// Liquid, through Fluid: a templating language with loops and conditions but no way to run code or reach the file system, so staff
/// who edit templates cannot turn the admin into code execution. Variables are only what the email's descriptor lists.
/// HTML bodies HTML-encode every variable (a product name with markup in it cannot inject any); text and subject do not.
/// </summary>
public static class SmtpTemplateRenderer
{
    private static readonly FluidParser Parser = new();

    /// <summary>Null when it parses; otherwise why not.</summary>
    public static string? Check(string source) => Parser.TryParse(source, out _, out var error) ? null : error;

    public static async Task<string> RenderAsync(string source, IReadOnlyDictionary<string, object?> variables, bool html)
    {
        if (!Parser.TryParse(source, out var template, out var error)) throw new FormatException(error);
        // A runaway template (a loop over a loop, or one that builds a huge string) is stopped instead of hanging a request.
        var context = new TemplateContext(TemplateOptions.Default) { MaxSteps = 20_000, MaxOutputSize = 1_000_000 };
        foreach (var (name, value) in variables)
            if (Plain(value) is { } plain) context.SetValue(name, plain); // a missing value is simply undefined: false in a condition, empty when printed
        return await template.RenderAsync(context, html ? HtmlEncoder.Default : NullEncoder.Default);
    }

    /// <summary>One-line, bounded: a value that contained a line break could otherwise start another mail header.</summary>
    public static string OneLine(string subject)
    {
        var line = subject.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 250 ? line : line[..250];
    }

    // Fluid reads dictionaries and lists best as the concrete types: copy whatever the sender gave into those.
    private static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool or int or long or double or decimal => value,
        IReadOnlyDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        _ => value.ToString(),
    };
}

/// <summary>The wording the platform's own emails start with, until someone edits them.</summary>
public static class SmtpTemplateDefaults
{
    public sealed record Content(string Subject, string TextBody, string? HtmlBody = null);

    public static Content? Find(string key) => key switch
    {
        "order.confirmation" => new Content("Your order {{ reference }}", OrderConfirmationText),
        "customer.set-password" => new Content("Set your password", SetPasswordText),
        "customer.verify-email" => new Content("Confirm your email address", VerifyEmailText),
        "order.shipped" => new Content(
            "{% if complete %}Your order {{ reference }} is on its way{% else %}Part of your order {{ reference }} is on its way{% endif %}", OrderShippedText),
        "order.cancelled" => new Content("Your order {{ reference }} has been cancelled", OrderCancelledText),
        "order.refunded" => new Content("Your refund for order {{ reference }}", OrderRefundedText),
        "alert.unreconciled-payment" => new Content("Payment needs attention: {{ amount }} {{ currency }} ({{ provider }})", UnreconciledAlertText),
        "alert.dispute-opened" => new Content("Payment disputed: {{ amount }} {{ currency }} on order {{ orderReference }}", DisputeOpenedText),
        "alert.return-requested" => new Content("Return requested on order {{ orderReference }}", ReturnRequestedText),
        "return.answered" => new Content("{% if approved %}Your return for order {{ reference }} is approved{% else %}About your return for order {{ reference }}{% endif %}", ReturnAnsweredText),
        "stock.back-in-stock" => new Content("{{ productName }} is back in stock", BackInStockText),
        "review.replied" => new Content("{{ productName }}: the shop replied to your review", ReviewRepliedText),
        "alert.low-stock" => new Content("Low stock: {{ count }} {% if count == 1 %}product{% else %}products{% endif %} at {{ level }} or fewer", LowStockText),
        _ => null,
    };

    private const string ReviewRepliedText = """
        Hello {{ displayName }},

        Thank you for reviewing {{ productName }}. The shop has replied:

        {{ reply }}
        {% if published -%}
        Your review and our reply are on the product page.
        {%- else -%}
        Your review and our reply will appear on the product page once your review has been approved.
        {%- endif %}
        """;

    private const string LowStockText = """
        These products on sale have run low (at {{ level }} or fewer on hand across every location):

        {% for p in products -%}
        {{ p.quantity }} left: {{ p.name }} ({{ p.sku }})
        {% endfor %}
        You will not be told about them again until they have been restocked above {{ level }}. The Low stock page in the admin shows the same list at any level.
        """;

    private const string BackInStockText = """
        Good news: {{ productName }} is back in stock.

        You asked us to tell you when it could be bought again. This is the only email we will send about it, and stock can sell quickly.
        {% if unsubscribeUrl %}
        If you no longer want to hear about it, you can cancel your request here: {{ unsubscribeUrl }}
        {% endif %}
        """;

    private const string DisputeOpenedText = """
        A buyer has disputed a payment with their bank.

        Order: {{ orderReference }}
        Amount: {{ amount }} {{ currency }}
        {% if reason -%}
        Reason given: {{ reason }}
        {% endif -%}
        {% if respondBy -%}
        Answer by: {{ respondBy }}
        {% endif %}
        Respond in {{ provider }}'s own dashboard (dispute {{ providerReference }}). If the dispute is lost the amount is taken back and recorded as a refund on the order.
        """;

    private const string ReturnRequestedText = """
        A buyer has asked to send goods back from order {{ orderReference }}.

        Reason: {{ reason }}

        {% for line in lines -%}
        {{ line.quantity }} x {{ line.name }} ({{ line.sku }})
        {% endfor %}
        Open the order in the admin to approve or turn it down.
        """;

    private const string ReturnAnsweredText = """
        {% if approved -%}
        We have approved your request to send back items from order {{ reference }}.
        {%- else -%}
        We are sorry, we cannot take back the items you asked about on order {{ reference }}.
        {%- endif %}

        {% for line in lines -%}
        {{ line.quantity }} x {{ line.name }}
        {% endfor %}
        {% if note -%}
        {{ note }}
        {% endif -%}
        """;

    private const string OrderConfirmationText = """
        Thank you for your order {{ reference }}.

        {% for line in lines -%}
        {{ line.quantity }} x {{ line.name }} ({{ line.sku }})  {{ line.total }} {{ currency }}
        {% endfor %}
        Subtotal: {{ subtotal }} {{ currency }}
        {% if discount -%}
        Discount{% if promotionCode %} ({{ promotionCode }}){% endif %}: -{{ discount }} {{ currency }}
        {% endif -%}
        {% if shippingMethod or shipping != "0.00" -%}
        Shipping{% if shippingMethod %} ({{ shippingMethod }}){% endif %}: {{ shipping }} {{ currency }}
        {% endif -%}
        {% if taxes.size > 0 -%}
        {% for t in taxes -%}
        {{ t.name }} ({{ t.rate }}%): {{ t.amount }} {{ currency }}
        {% endfor -%}
        {% elsif tax != "0.00" -%}
        Tax: {{ tax }} {{ currency }}
        {% endif -%}
        Total: {{ total }} {{ currency }}
        {% if paid -%}
        Paid: {{ paid }} {{ currency }}
        Still to pay: {{ stillToPay }} {{ currency }}
        {% endif %}
        {% if shippingAddress.size > 0 -%}
        Delivering to:
        {% for l in shippingAddress -%}
        {{ l }}
        {% endfor -%}
        {% endif -%}
        """;

    private const string OrderShippedText = """
        {% if complete -%}
        Your order {{ reference }} has been sent.
        {% else -%}
        Part of your order {{ reference }} has been sent.
        {% endif %}
        In this parcel:
        {% for line in lines -%}
        {{ line.quantity }} x {{ line.name }}{% if line.sku %} ({{ line.sku }}){% endif %}
        {% endfor %}
        {% if carrier or trackingNumber -%}
        {% if carrier %}Carrier: {{ carrier }}
        {% endif -%}
        {% if trackingNumber %}Tracking number: {{ trackingNumber }}
        {% endif -%}
        {% endif -%}
        {% if trackingUrl -%}
        Track it here: {{ trackingUrl }}
        {% endif -%}
        {% if stillToSend.size > 0 %}
        Still to come:
        {% for line in stillToSend -%}
        {{ line.quantity }} x {{ line.name }}{% if line.sku %} ({{ line.sku }}){% endif %}
        {% endfor -%}
        {% endif -%}
        {% if shippingAddress.size > 0 %}
        Delivering to:
        {% for l in shippingAddress -%}
        {{ l }}
        {% endfor -%}
        {% endif -%}
        """;

    private const string OrderCancelledText = """
        Your order {{ reference }} ({{ total }} {{ currency }}) has been cancelled.
        {% if stillToRefund %}
        You paid {{ stillToRefund }} {{ currency }} that has not been returned yet. We will refund it to you; it can take a few days to appear.
        {% endif %}
        If you did not expect this, please reply to this email.
        """;

    private const string OrderRefundedText = """
        We have refunded {{ amount }} {{ currency }} on your order {{ reference }}.

        Refunded so far: {{ totalRefunded }} {{ currency }} of {{ orderTotal }} {{ currency }}.

        It can take a few days to appear on your statement.
        """;

    private const string UnreconciledAlertText = """
        {{ provider }} confirmed {{ amount }} {{ currency }} ({{ kind }}) that could not be booked against an order.

        {% if orderReference -%}
        Order: {{ orderReference }}
        {% else -%}
        Order: not found
        {% endif -%}
        Reason: {{ reason }}
        {{ provider }} reference: {{ providerReference }}

        Open the admin, Orders > Unreconciled payments, to retry it or mark it dealt with.
        """;

    private const string VerifyEmailText = """
        Please confirm this email address to finish setting up your account.

        {% if link -%}
        Confirm it here:
        {{ link }}
        {% else -%}
        Your code is:
        {{ code }}
        {% endif %}
        This works once and expires in {{ expiresInMinutes }} minutes. If it was not you, ignore this email: no account is used until it is confirmed.
        """;

    private const string SetPasswordText = """
        Someone asked to set a password for this email address.

        {% if link -%}
        Choose a password here:
        {{ link }}
        {% else -%}
        Your code is:
        {{ code }}
        {% endif %}
        This works once and expires in {{ expiresInMinutes }} minutes. If it was not you, ignore this email: nothing changes.
        """;
}

public enum EmailTemplateStatus
{
    /// <summary>Using the wording the platform ships with.</summary>
    Default = 0,

    /// <summary>Edited here.</summary>
    Customised = 1,

    /// <summary>A package declared this email but this sender has no wording for it: sending it fails until one is written.</summary>
    NotSetUp = 2,
}

public sealed class EmailTemplateInput
{
    [Required, StringLength(250)] public string Subject { get; set; } = "";
    [Required, StringLength(100_000)] public string TextBody { get; set; } = "";
    [StringLength(200_000)] public string? HtmlBody { get; set; }
}

public sealed record EmailTemplateVariableDto(string Name, string Description);

public sealed record EmailTemplateSummaryDto(string Key, string Name, string Description, EmailTemplateStatus Status, DateTimeOffset? UpdatedUtc);

public sealed record EmailTemplateDto(
    string Key, string Name, string Description, EmailTemplateStatus Status, IReadOnlyList<EmailTemplateVariableDto> Variables,
    string Subject, string TextBody, string? HtmlBody, DateTimeOffset? UpdatedUtc, string? UpdatedBy);

/// <summary>An email rendered with the sample values.</summary>
public sealed record EmailTemplatePreviewDto(string Subject, string TextBody, string? HtmlBody);

public interface IEmailTemplateService
{
    Task<IReadOnlyList<EmailTemplateSummaryDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Null when no installed package declares that key.</summary>
    Task<EmailTemplateDto?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Saves new wording. A template that does not parse, or cannot be rendered with the email's sample data, is a validation error and
    /// nothing is saved. Null when no installed package declares that key.
    /// </summary>
    [RequiresPermission(Permissions.SettingsManage)]
    Task<EmailTemplateDto?> SaveAsync(string key, EmailTemplateInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Throws away the edit, so the shipped wording is used again. Null when no package declares the key.</summary>
    [RequiresPermission(Permissions.SettingsManage)]
    Task<EmailTemplateDto?> ResetAsync(string key, CancellationToken ct = default);

    /// <summary>Renders the given wording (or, with null, what is saved now) with the email's sample data. Does not save.</summary>
    Task<EmailTemplatePreviewDto?> PreviewAsync(string key, EmailTemplateInput? input, CancellationToken ct = default);
}

internal sealed class EmailTemplateService(ISmtpTemplateStore store, IEmailTemplateCatalog catalog) : IEmailTemplateService
{
    public async Task<IReadOnlyList<EmailTemplateSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var stored = (await store.ListAsync(ct)).ToDictionary(s => s.Key);
        return catalog.All.Select(d =>
        {
            stored.TryGetValue(d.Key, out var s);
            return new EmailTemplateSummaryDto(d.Key, d.Name, d.Description, StatusOf(d.Key, s), s?.UpdatedUtc);
        }).ToList();
    }

    public async Task<EmailTemplateDto?> GetAsync(string key, CancellationToken ct = default)
    {
        var descriptor = catalog.Find(key);
        if (descriptor is null) return null;
        return Build(descriptor, await store.FindAsync(key, ct));
    }

    public async Task<EmailTemplateDto?> SaveAsync(string key, EmailTemplateInput input, string? actor = null, CancellationToken ct = default)
    {
        var descriptor = catalog.Find(key);
        if (descriptor is null) return null;
        ModelValidator.ValidateOrThrow(input);
        var html = string.IsNullOrWhiteSpace(input.HtmlBody) ? null : input.HtmlBody;
        await CheckAsync(descriptor, input.Subject, input.TextBody, html);

        await store.SaveAsync(new StoredSmtpTemplate(key, input.Subject, input.TextBody, html, DateTimeOffset.UtcNow, actor), ct);
        return Build(descriptor, await store.FindAsync(key, ct));
    }

    public async Task<EmailTemplateDto?> ResetAsync(string key, CancellationToken ct = default)
    {
        var descriptor = catalog.Find(key);
        if (descriptor is null) return null;
        await store.DeleteAsync(key, ct);
        return Build(descriptor, null);
    }

    public async Task<EmailTemplatePreviewDto?> PreviewAsync(string key, EmailTemplateInput? input, CancellationToken ct = default)
    {
        var descriptor = catalog.Find(key);
        if (descriptor is null) return null;

        string subject, text; string? html;
        if (input is not null)
        {
            subject = input.Subject; text = input.TextBody; html = string.IsNullOrWhiteSpace(input.HtmlBody) ? null : input.HtmlBody;
        }
        else
        {
            var current = Build(descriptor, await store.FindAsync(key, ct));
            subject = current.Subject; text = current.TextBody; html = current.HtmlBody;
        }
        return await CheckAsync(descriptor, subject, text, html);
    }

    /// <summary>Parses and renders all three parts with the sample data; the first problem is a validation error naming the part.</summary>
    private static async Task<EmailTemplatePreviewDto> CheckAsync(EmailTemplateDescriptor d, string subject, string text, string? html)
    {
        var samples = d.Samples;
        async Task<string> Part(string field, string source, bool isHtml)
        {
            if (SmtpTemplateRenderer.Check(source) is { } parseError)
                throw new PiranhaCommerceValidationException(field, $"This template is not valid: {parseError}");
            try { return await SmtpTemplateRenderer.RenderAsync(source, samples, isHtml); }
            catch (Exception ex) when (ex is not PiranhaCommerceValidationException)
            {
                throw new PiranhaCommerceValidationException(field, $"This template could not be rendered: {ex.Message}");
            }
        }

        var renderedSubject = SmtpTemplateRenderer.OneLine(await Part(nameof(EmailTemplateInput.Subject), subject, false));
        var renderedText = await Part(nameof(EmailTemplateInput.TextBody), text, false);
        var renderedHtml = html is null ? null : await Part(nameof(EmailTemplateInput.HtmlBody), html, true);
        return new EmailTemplatePreviewDto(renderedSubject, renderedText, renderedHtml);
    }

    private static EmailTemplateStatus StatusOf(string key, StoredSmtpTemplate? stored) =>
        stored is not null ? EmailTemplateStatus.Customised : SmtpTemplateDefaults.Find(key) is not null ? EmailTemplateStatus.Default : EmailTemplateStatus.NotSetUp;

    private static EmailTemplateDto Build(EmailTemplateDescriptor d, StoredSmtpTemplate? stored)
    {
        var fallback = SmtpTemplateDefaults.Find(d.Key);
        var subject = stored?.Subject ?? fallback?.Subject ?? "";
        var text = stored?.TextBody ?? fallback?.TextBody ?? "";
        var html = stored is not null ? stored.HtmlBody : fallback?.HtmlBody;
        return new EmailTemplateDto(d.Key, d.Name, d.Description, StatusOf(d.Key, stored),
            d.Variables.Select(v => new EmailTemplateVariableDto(v.Name, v.Description)).ToList(), subject, text, html, stored?.UpdatedUtc, stored?.UpdatedBy);
    }
}

internal sealed class EmailTemplateApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var templates = api.MapGroup("/email-templates").WithTags("Email templates").RequirePermission(Permissions.Access);

        templates.MapGet("/", async (IEmailTemplateService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        templates.MapGet("/{key}", async (string key, IEmailTemplateService svc, CancellationToken ct) =>
            await svc.GetAsync(key, ct) is { } t ? Results.Ok(t) : Results.NotFound());

        templates.MapPut("/{key}", async (string key, EmailTemplateInput input, IEmailTemplateService svc, HttpContext http, CancellationToken ct) =>
                await svc.SaveAsync(key, input, http.User.FindFirst("email")?.Value, ct) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequirePermission(Permissions.SettingsManage);

        // Reset to the shipped wording.
        templates.MapDelete("/{key}", async (string key, IEmailTemplateService svc, CancellationToken ct) =>
                await svc.ResetAsync(key, ct) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequirePermission(Permissions.SettingsManage);

        templates.MapPost("/{key}/preview", async (string key, EmailTemplateInput? input, IEmailTemplateService svc, CancellationToken ct) =>
            await svc.PreviewAsync(key, input, ct) is { } p ? Results.Ok(p) : Results.NotFound());
    }
}
