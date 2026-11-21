using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PiranhaCommerce.Core.Email;

/// <summary>
/// One email. The plain text body is always required and always complete: a sender that just sends mail (SMTP) uses it as is.
/// <paramref name="TemplateKey"/> and <paramref name="Variables"/> are for a sender that renders its own templates (a provider's
/// template engine): the key names the message ("order.confirmation") and the variables are its data. Such a sender maps the key to
/// its own template id and falls back to the text body when it has no template for a key.
/// </summary>
/// <param name="TemplateKey">Stable name of the kind of message, e.g. <c>order.confirmation</c>, <c>customer.set-password</c>. Null for an ad hoc message.</param>
/// <param name="Variables">Plain values (strings, numbers, lists of those, dictionaries) a template can show. Money is already formatted. May hold secrets such as a reset link, so a sender must not log it.</param>
public sealed record EmailMessage(
    string To, string Subject, string TextBody, string? HtmlBody = null, string? TemplateKey = null, IReadOnlyDictionary<string, object?>? Variables = null);

/// <summary>The keys of the messages the platform itself sends.</summary>
public static class EmailTemplates
{
    public const string OrderConfirmation = "order.confirmation";
    public const string SetPassword = "customer.set-password";
}

/// <summary>
/// The one way the platform sends email (password resets, order confirmations, ...). Nothing here knows about SMTP or any
/// provider: a plugin registers its own implementation (SMTP, SendGrid, Azure Communication Services, ...) and replaces the
/// default, which only logs that nothing was sent.
/// </summary>
public interface IEmailSender
{
    /// <summary>Throws if the provider refuses the message. Callers decide whether a failure matters (an order never fails because an email did).</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Registered when no real sender is: it records that an email was skipped, without its content (which may hold a reset link).</summary>
internal sealed class NoEmailSender(ILogger<NoEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogWarning("Email not sent (no IEmailSender is registered): subject \"{Subject}\". Register an email plugin to send mail.", message.Subject);
        return Task.CompletedTask;
    }
}

public static class EmailServiceCollectionExtensions
{
    /// <summary>Registers the do-nothing default unless a real sender is already registered. Safe to call more than once.</summary>
    public static IServiceCollection AddPiranhaEmailDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmailSender, NoEmailSender>();
        return services;
    }
}
