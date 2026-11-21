using PiranhaCommerce.Core.Email;

namespace PiranhaCommerce.Customers;

/// <summary>The emails sent to shoppers about their account. The wording belongs to the email sender; this names the email and its data.</summary>
public static class CustomerEmailTemplates
{
    public const string SetPassword = "customer.set-password";
}

internal sealed class CustomerEmailContributor : IEmailTemplateContributor
{
    public IEnumerable<EmailTemplateDescriptor> GetTemplates() =>
    [
        new(CustomerEmailTemplates.SetPassword, "Set your password",
            "Sent when a shopper asks to reset their password, and when a customer made by staff or at checkout first signs in. Works once.",
        [
            new("email", "The shopper's email address.", "ada@example.com"),
            new("link", "The page where they choose a password. Empty when the shop has no reset page set up: then show the code instead.", "https://shop.example/reset?token=abc123"),
            new("code", "The one-time code. Show it when there is no link.", "0f3a9c.Xk2j9"),
            new("expiresInMinutes", "How long the link or code works.", 60),
        ]),
    ];
}
