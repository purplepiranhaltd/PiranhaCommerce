using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Email.Smtp;

public enum SmtpSecurity
{
    /// <summary>Upgrade to TLS if the server offers it, otherwise send in the clear. Convenient, but a network attacker can force the clear option: prefer <see cref="StartTls"/>.</summary>
    Auto = 0,

    /// <summary>Plain connection, then required TLS (usually port 587). The default.</summary>
    StartTls = 1,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect = 2,

    /// <summary>No encryption at all. Only for a mail catcher on your own machine (smtp4dev, Papercut): credentials and mail travel in the clear.</summary>
    None = 3,
}

/// <summary>
/// SMTP settings (section <c>Piranha:Email:Smtp</c>). Keep the password out of source control: user secrets in development,
/// your host's secret store in production. Server certificates are always validated.
/// </summary>
public sealed class SmtpEmailOptions
{
    public const string SectionName = "Piranha:Email:Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Leave empty for a server that needs no sign-in.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>The sender address shown to shoppers. Most servers only accept an address they have verified.</summary>
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }

    /// <summary>Where replies go, if not to the sender address.</summary>
    public string? ReplyToAddress { get; set; }

    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Sends every platform email through an SMTP server. The words come from this package's own templates (edited in its admin section,
/// or the shipped defaults) filled in with the message's variables. Scoped, because the templates are read from the database.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpEmailOptions> options, ISmtpTemplateStore templates, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private SmtpEmailOptions Options => options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = Options;
        if (string.IsNullOrWhiteSpace(o.Host)) throw new InvalidOperationException("SMTP is not configured: Piranha:Email:Smtp:Host is empty.");
        if (!TryAddress(o.FromAddress, out var from)) throw new InvalidOperationException("SMTP is not configured: Piranha:Email:Smtp:FromAddress is not a valid address.");
        if (!string.IsNullOrWhiteSpace(o.FromName)) from.Name = Clean(o.FromName);

        // One recipient, parsed as an address: a value like "a@x.com, b@y.com" or one carrying header text is refused rather than sent.
        if (!TryAddress(message.To, out var to) || (message.To ?? "").Contains(',') || (message.To ?? "").Contains(';'))
            throw new ArgumentException("The recipient is not a single valid email address.", nameof(message));

        var mime = new MimeMessage();
        mime.From.Add(from);
        mime.To.Add(to);
        if (!string.IsNullOrWhiteSpace(o.ReplyToAddress))
        {
            if (!TryAddress(o.ReplyToAddress, out var reply)) throw new InvalidOperationException("SMTP is not configured: Piranha:Email:Smtp:ReplyToAddress is not a valid address.");
            mime.ReplyTo.Add(reply);
        }
        var content = await RenderAsync(message, ct);
        mime.Subject = Clean(content.Subject);
        mime.Body = new BodyBuilder { TextBody = content.Text, HtmlBody = content.Html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = Math.Max(1, o.TimeoutSeconds) * 1000 };
        var secure = o.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTlsWhenAvailable,
        };

        await client.ConnectAsync(o.Host, o.Port, secure, ct);
        if (!string.IsNullOrWhiteSpace(o.Username)) await client.AuthenticateAsync(o.Username, o.Password ?? "", ct);
        await client.SendAsync(new MailKit.SendRequest(mime), ct);
        await client.DisconnectAsync(quit: true, ct);

        // The recipient and subject are not logged: they identify a customer and a reset subject says what the mail is for.
        logger.LogInformation("Email sent through {Host}:{Port}.", o.Host, o.Port);
    }

    private async Task<(string Subject, string Text, string? Html)> RenderAsync(EmailMessage message, CancellationToken ct)
    {
        var stored = await templates.FindAsync(message.TemplateKey, ct);
        var subject = stored?.Subject; var text = stored?.TextBody; var html = stored?.HtmlBody;
        if (stored is null)
        {
            var fallback = SmtpTemplateDefaults.Find(message.TemplateKey)
                ?? throw new InvalidOperationException($"There is no SMTP template for \"{message.TemplateKey}\". Write one in the admin under Email > Templates.");
            (subject, text, html) = (fallback.Subject, fallback.TextBody, fallback.HtmlBody);
        }

        var variables = message.Variables ?? new Dictionary<string, object?>();
        return (SmtpTemplateRenderer.OneLine(await SmtpTemplateRenderer.RenderAsync(subject!, variables, html: false)),
            await SmtpTemplateRenderer.RenderAsync(text!, variables, html: false),
            html is null ? null : await SmtpTemplateRenderer.RenderAsync(html, variables, html: true));
    }

    /// <summary>A real address has a name and a domain; "ada" alone parses but would be a local mailbox, so it is refused.</summary>
    private static bool TryAddress(string? text, out MailboxAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text) || !MailboxAddress.TryParse(text, out var parsed)) return false;
        var at = parsed.Address.LastIndexOf('@');
        if (at <= 0 || at == parsed.Address.Length - 1) return false;
        address = parsed;
        return true;
    }

    /// <summary>A header value is one line: carriage returns and line feeds could start a new header.</summary>
    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>Sends platform email through SMTP. Configure <see cref="SmtpEmailOptions"/>.</summary>
public sealed class SmtpEmailPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Email.Smtp", "SMTP email", "0.1.0", "Sends platform email through an SMTP server.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<SmtpEmailOptions>().BindConfiguration(SmtpEmailOptions.SectionName);
        // Registered after the core's do-nothing default, so this one is the sender. Scoped: it reads templates from the database.
        services.AddSingleton<IModelContributor, SmtpModelContributor>();
        services.AddScoped<ISmtpTemplateStore, EfSmtpTemplateStore>();
        services.AddScoped<IEmailTemplateService, EmailTemplateService>();
        services.AddSingleton<IApiEndpointContributor, EmailTemplateApiEndpoints>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
    }
}
