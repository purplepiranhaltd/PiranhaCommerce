using System.Collections.Concurrent;
using PiranhaCommerce.Core.Email;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Keeps every email in memory. Set <see cref="Fail"/> to prove a broken mail server never breaks the caller.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool Fail { get; set; }

    public IReadOnlyList<EmailMessage> To(string address) =>
        _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("The mail server is down.");
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
