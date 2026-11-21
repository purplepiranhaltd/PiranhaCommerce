using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Email;

namespace PiranhaCommerce.IntegrationTests;

internal static class EmailContract
{
    /// <summary>
    /// The variable names are a public contract (SMTP templates and Mailjet templates refer to them): a message must carry exactly the
    /// names its descriptor lists, so a rename in the code cannot silently break someone's template.
    /// </summary>
    public static void AssertMatchesDescriptor(PiranhaFactory factory, EmailMessage message)
    {
        var descriptor = factory.Services.GetRequiredService<IEmailTemplateCatalog>().Find(message.TemplateKey);
        Assert.NotNull(descriptor);
        Assert.Equal(descriptor!.Variables.Select(v => v.Name).Order(StringComparer.Ordinal), message.Variables.Keys.Order(StringComparer.Ordinal));
    }
}
