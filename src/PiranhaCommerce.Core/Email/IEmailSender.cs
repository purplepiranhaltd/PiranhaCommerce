using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PiranhaCommerce.Core.Email;

/// <summary>One email. Plain text is always required; HTML is optional.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

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
