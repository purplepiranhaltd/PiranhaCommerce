using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Email.Smtp;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The SMTP package's own templates: the shipped wording, editing through the API, and what is refused.</summary>
public class EmailTemplateTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static string Url(string key) => $"/api/v1/email-templates/{key}";

    [Fact]
    public async Task Every_email_a_package_declares_has_shipped_wording_that_renders_with_its_own_sample_data()
    {
        var catalog = factory.Services.GetRequiredService<IEmailTemplateCatalog>();
        Assert.Contains(catalog.All, t => t.Key == "order.confirmation");
        Assert.Contains(catalog.All, t => t.Key == "customer.set-password");

        foreach (var d in catalog.All)
        {
            var shipped = SmtpTemplateDefaults.Find(d.Key);
            Assert.NotNull(shipped);
            var subject = await SmtpTemplateRenderer.RenderAsync(shipped!.Subject, d.Samples, html: false);
            var text = await SmtpTemplateRenderer.RenderAsync(shipped.TextBody, d.Samples, html: false);
            Assert.False(string.IsNullOrWhiteSpace(subject));
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public async Task The_shipped_order_confirmation_shows_the_order_the_way_the_old_fixed_text_did()
    {
        var d = factory.Services.GetRequiredService<IEmailTemplateCatalog>().Find("order.confirmation")!;
        var shipped = SmtpTemplateDefaults.Find("order.confirmation")!;
        var text = await SmtpTemplateRenderer.RenderAsync(shipped.TextBody, d.Samples, html: false);

        Assert.Equal("Your order ORD-1001", await SmtpTemplateRenderer.RenderAsync(shipped.Subject, d.Samples, html: false));
        Assert.Contains("Thank you for your order ORD-1001.", text);
        Assert.Contains("1 x Blue mug (MUG-BLU)  10.00 GBP", text);
        Assert.Contains("2 x Teapot (POT-1)  20.00 GBP", text);
        Assert.Contains("Subtotal: 30.00 GBP", text);
        Assert.Contains("Discount (SPRING): -3.00 GBP", text);
        Assert.Contains("Shipping (Royal Mail Tracked): 5.00 GBP", text);
        Assert.Contains("VAT (20%): 3.70 GBP", text);
        Assert.Contains("Total: 35.70 GBP", text);
        Assert.Contains("Delivering to:", text);
        Assert.Contains("Ada Lovelace", text);

        // With nothing optional present, the optional lines are simply not there.
        var bare = d.Samples.ToDictionary(kv => kv.Key, kv => kv.Value);
        bare["discount"] = null; bare["promotionCode"] = null; bare["paid"] = null; bare["shippingMethod"] = null; bare["shipping"] = "0.00";
        bare["taxes"] = new List<object?>(); bare["tax"] = "0.00"; bare["shippingAddress"] = new List<object?>();
        var plain = await SmtpTemplateRenderer.RenderAsync(shipped.TextBody, bare, html: false);
        Assert.DoesNotContain("Discount", plain);
        Assert.DoesNotContain("Shipping", plain);
        Assert.DoesNotContain("Tax", plain);
        Assert.DoesNotContain("Delivering to", plain);
        Assert.DoesNotContain("Paid:", plain);
        Assert.Contains("Total: 35.70 GBP", plain);
    }

    [Fact]
    public async Task The_set_password_wording_shows_the_link_when_there_is_one_and_the_code_when_there_is_not()
    {
        var d = factory.Services.GetRequiredService<IEmailTemplateCatalog>().Find("customer.set-password")!;
        var shipped = SmtpTemplateDefaults.Find("customer.set-password")!;

        var withLink = d.Samples.ToDictionary(kv => kv.Key, kv => kv.Value);
        var linked = await SmtpTemplateRenderer.RenderAsync(shipped.TextBody, withLink, html: false);
        Assert.Contains("Choose a password here:", linked);
        Assert.Contains("https://shop.example/reset?token=abc123", linked);
        Assert.DoesNotContain("Your code is", linked);

        withLink["link"] = null;
        var coded = await SmtpTemplateRenderer.RenderAsync(shipped.TextBody, withLink, html: false);
        Assert.Contains("Your code is:", coded);
        Assert.Contains("0f3a9c.Xk2j9", coded);
        Assert.DoesNotContain("Choose a password here", coded);
    }

    [Fact]
    public async Task Templates_are_listed_edited_previewed_and_reset_through_the_api()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        const string key = "customer.set-password";
        await staff.DeleteAsync(Url(key)); // start from the shipped wording whatever ran before

        var list = await Read<List<EmailTemplateSummaryDto>>(await staff.GetAsync("/api/v1/email-templates"));
        Assert.Equal(EmailTemplateStatus.Default, list.Single(t => t.Key == key).Status);

        var before = await Read<EmailTemplateDto>(await staff.GetAsync(Url(key)));
        Assert.Equal("Set your password", before.Subject);
        Assert.Contains(before.Variables, v => v.Name == "code");

        var edit = new EmailTemplateInput { Subject = "Your new password for {{ email }}", TextBody = "Code: {{ code }}", HtmlBody = "<p>{{ code }}</p>" };
        var saved = await Read<EmailTemplateDto>(await staff.PutAsJsonAsync(Url(key), edit));
        Assert.Equal((EmailTemplateStatus.Customised, "Your new password for {{ email }}", "admin@piranha.local"), (saved.Status, saved.Subject, saved.UpdatedBy));

        var preview = await Read<EmailTemplatePreviewDto>(await staff.PostAsJsonAsync(Url(key) + "/preview", new EmailTemplateInput { Subject = "Hi {{ email }}", TextBody = "Use {{ code }}" }));
        Assert.Equal(("Hi ada@example.com", "Use 0f3a9c.Xk2j9"), (preview.Subject, preview.TextBody));
        var current = await Read<EmailTemplatePreviewDto>(await staff.PostAsync(Url(key) + "/preview", null));
        Assert.Equal("Your new password for ada@example.com", current.Subject); // with no body, what is saved
        Assert.Equal(EmailTemplateStatus.Customised, (await Read<EmailTemplateDto>(await staff.GetAsync(Url(key)))).Status);

        var reset = await Read<EmailTemplateDto>(await staff.DeleteAsync(Url(key)));
        Assert.Equal((EmailTemplateStatus.Default, "Set your password"), (reset.Status, reset.Subject));
    }

    [Fact]
    public async Task A_template_that_does_not_parse_or_cannot_render_is_refused_and_nothing_is_saved()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        const string key = "customer.set-password";
        await staff.DeleteAsync(Url(key));

        foreach (var bad in new[]
        {
            new EmailTemplateInput { Subject = "Hi {{ ", TextBody = "ok" },
            new EmailTemplateInput { Subject = "Hi", TextBody = "{% for x in lines %} never closed" },
            new EmailTemplateInput { Subject = "Hi", TextBody = "ok", HtmlBody = "{% if %}" },
            new EmailTemplateInput { Subject = "", TextBody = "ok" },
            new EmailTemplateInput { Subject = "Hi", TextBody = "" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync(Url(key), bad)).StatusCode);

        // A template that loops without end is stopped rather than hanging the request.
        var runaway = new EmailTemplateInput { Subject = "Hi", TextBody = "{% for a in (1..1000) %}{% for b in (1..1000) %}x{% endfor %}{% endfor %}" };
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync(Url(key), runaway)).StatusCode);

        Assert.Equal(EmailTemplateStatus.Default, (await Read<EmailTemplateDto>(await staff.GetAsync(Url(key)))).Status);
    }

    [Fact]
    public async Task A_template_cannot_reach_beyond_its_variables_and_unknown_keys_are_not_found()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        const string key = "customer.set-password";
        await staff.DeleteAsync(Url(key));

        // Liquid has no way to call code or read the system: reaching for it just prints nothing.
        var probe = new EmailTemplateInput { Subject = "Hi", TextBody = "[{{ code.GetType }}][{{ email.Length }}][{{ \"x\" | upcase }}]" };
        var saved = await Read<EmailTemplateDto>(await staff.PutAsJsonAsync(Url(key), probe));
        var preview = await Read<EmailTemplatePreviewDto>(await staff.PostAsync(Url(key) + "/preview", null));
        Assert.DoesNotContain("System", preview.TextBody);
        Assert.Contains("[X]", preview.TextBody);
        await staff.DeleteAsync(Url(key));

        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync(Url("no.such.email"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync(Url("no.such.email"), new EmailTemplateInput { Subject = "a", TextBody = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync(Url("no.such.email"))).StatusCode);
    }

    [Fact]
    public async Task A_read_only_user_can_read_and_preview_templates_but_not_change_them()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        const string key = "customer.set-password";
        var edit = new EmailTemplateInput { Subject = "a", TextBody = "b" };

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/email-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Url(key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.PostAsJsonAsync(Url(key) + "/preview", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(Url(key), edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync(Url(key))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/email-templates")).StatusCode);
    }
}
