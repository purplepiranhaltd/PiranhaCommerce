using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PiranhaCommerce.Core.Email;

/// <summary>
/// One email to send: who it is for, WHICH email it is (<paramref name="TemplateKey"/>), and the data it needs. It deliberately has no
/// subject or body: the words belong to the email sender. An SMTP sender keeps its own editable templates (and so has an admin section
/// for them); a provider such as Mailjet maps the key to a template in its own system (and its admin is where it is edited).
/// </summary>
/// <param name="TemplateKey">Names the kind of message, e.g. <c>order.confirmation</c>. Declared by the package that sends it, with <see cref="IEmailTemplateContributor"/>.</param>
/// <param name="Variables">
/// The data, named exactly as the template's descriptor lists. Plain values only (strings, numbers, booleans, null, and lists or
/// dictionaries of those). Money is already formatted. May hold secrets such as a reset link, so a sender must not log it.
/// </param>
public sealed record EmailMessage(string To, string TemplateKey, IReadOnlyDictionary<string, object?> Variables);

/// <summary>One piece of data an email template can show. The <paramref name="Sample"/> is used to preview templates and to check they render.</summary>
public sealed record EmailTemplateVariable(string Name, string Description, object? Sample);

/// <summary>
/// What one email is and what data it carries. The names in <see cref="Variables"/> are a public contract: templates (in an SMTP
/// admin, or in a provider such as Mailjet) refer to them, so changing one breaks those templates.
/// </summary>
public sealed record EmailTemplateDescriptor(string Key, string Name, string Description, IReadOnlyList<EmailTemplateVariable> Variables)
{
    /// <summary>The sample value of every variable, for previews.</summary>
    public IReadOnlyDictionary<string, object?> Samples => Variables.ToDictionary(v => v.Name, v => v.Sample);
}

/// <summary>A package that sends emails declares them here, next to the code that fills in the variables.</summary>
public interface IEmailTemplateContributor
{
    IEnumerable<EmailTemplateDescriptor> GetTemplates();
}

/// <summary>Every email the installed packages can send. A sender uses this to list, preview and map templates.</summary>
public interface IEmailTemplateCatalog
{
    IReadOnlyList<EmailTemplateDescriptor> All { get; }
    EmailTemplateDescriptor? Find(string key);
}

internal sealed class EmailTemplateCatalog(IEnumerable<IEmailTemplateContributor> contributors) : IEmailTemplateCatalog
{
    private readonly Lazy<IReadOnlyList<EmailTemplateDescriptor>> _all = new(() =>
    {
        var all = contributors.SelectMany(c => c.GetTemplates()).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var duplicate = all.GroupBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Two packages declare the email template \"{duplicate.Key}\".");
        return all;
    });

    public IReadOnlyList<EmailTemplateDescriptor> All => _all.Value;
    public EmailTemplateDescriptor? Find(string key) => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// The one way the platform sends email (password resets, order confirmations, ...). Nothing here knows about SMTP or any
/// provider: a plugin registers its own implementation (SMTP, Mailjet, ...) and replaces the default, which only logs that nothing
/// was sent.
/// </summary>
public interface IEmailSender
{
    /// <summary>Throws if the provider refuses the message or has no template for its key. Callers decide whether a failure matters (an order never fails because an email did).</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Registered when no real sender is: it records that an email was skipped, without its data (which may hold a reset link).</summary>
internal sealed class NoEmailSender(ILogger<NoEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogWarning("Email not sent (no IEmailSender is registered): template \"{TemplateKey}\". Register an email plugin to send mail.", message.TemplateKey);
        return Task.CompletedTask;
    }
}

public static class EmailServiceCollectionExtensions
{
    /// <summary>Registers the do-nothing default unless a real sender is already registered, and the template catalog. Safe to call more than once.</summary>
    public static IServiceCollection AddPiranhaEmailDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmailSender, NoEmailSender>();
        services.TryAddSingleton<IEmailTemplateCatalog, EmailTemplateCatalog>();
        return services;
    }
}
