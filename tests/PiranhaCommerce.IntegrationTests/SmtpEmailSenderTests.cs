using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Email.Smtp;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The SMTP sender against a tiny in-process SMTP server (no TLS, nothing leaves the machine).</summary>
public class SmtpEmailSenderTests
{
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public List<(string From, List<string> Recipients, string Data)> Received { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSmtpServer()
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try { while (!_stop.IsCancellationRequested) await Serve(await _listener.AcceptTcpClientAsync(_stop.Token)); }
                catch (OperationCanceledException) { }
            });
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost fake smtp");
            string from = "";
            var recipients = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await writer.WriteLineAsync("250 localhost");
                else if (cmd.StartsWith("MAIL FROM")) { from = line[10..].Trim(); await writer.WriteLineAsync("250 ok"); }
                else if (cmd.StartsWith("RCPT TO")) { recipients.Add(line[8..].Trim()); await writer.WriteLineAsync("250 ok"); }
                else if (cmd == "DATA")
                {
                    await writer.WriteLineAsync("354 go");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } d && d != ".") data.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    lock (Received) Received.Add((from, recipients, data.ToString()));
                    from = ""; recipients = [];
                    await writer.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
                else await writer.WriteLineAsync("250 ok");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }

    private static SmtpEmailSender Sender(FakeSmtpServer? server, Action<SmtpEmailOptions>? tweak = null)
    {
        var o = new SmtpEmailOptions
        {
            Host = "127.0.0.1", Port = server?.Port ?? 1, Security = SmtpSecurity.None, FromAddress = "shop@example.com", FromName = "Purple Shop", TimeoutSeconds = 5,
        };
        tweak?.Invoke(o);
        return new SmtpEmailSender(Options.Create(o), NullLogger<SmtpEmailSender>.Instance);
    }

    private static MimeMessage Parse(string data) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(data)));

    [Fact]
    public async Task A_message_arrives_with_its_sender_recipient_subject_text_and_html()
    {
        await using var server = new FakeSmtpServer();
        await Sender(server, o => o.ReplyToAddress = "help@example.com")
            .SendAsync(new EmailMessage("ada@example.com", "Your order ORD-1001", "Plain body £12.34", "<p>Html body</p>", TemplateKey: EmailTemplates.OrderConfirmation,
                Variables: new Dictionary<string, object?> { ["total"] = "12.34" }));

        var got = Assert.Single(server.Received);
        Assert.Contains("shop@example.com", got.From);
        Assert.Equal(["<ada@example.com>"], got.Recipients);
        var mime = Parse(got.Data);
        Assert.Equal("Your order ORD-1001", mime.Subject);
        Assert.Equal("Purple Shop", mime.From.Mailboxes.Single().Name);
        Assert.Equal("help@example.com", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Contains("Plain body £12.34", mime.TextBody);
        Assert.Contains("Html body", mime.HtmlBody);
        Assert.DoesNotContain("order.confirmation", got.Data); // template keys and variables are for template senders, not mail
    }

    [Fact]
    public async Task A_subject_cannot_smuggle_in_another_header_or_recipient()
    {
        await using var server = new FakeSmtpServer();
        await Sender(server).SendAsync(new EmailMessage("ada@example.com", "Hello\r\nBcc: evil@example.com", "Body"));

        var got = Assert.Single(server.Received);
        Assert.Equal(["<ada@example.com>"], got.Recipients);
        var mime = Parse(got.Data);
        Assert.Empty(mime.Bcc);
        Assert.Null(mime.Headers["Bcc"]);
        Assert.StartsWith("Hello", mime.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("a@example.com, b@example.com")]
    [InlineData("a@example.com;b@example.com")]
    [InlineData("ada")]
    public async Task Anything_but_one_valid_recipient_is_refused_before_connecting(string to)
    {
        await using var server = new FakeSmtpServer();
        await Assert.ThrowsAsync<ArgumentException>(() => Sender(server).SendAsync(new EmailMessage(to, "Hi", "Body")));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Missing_settings_and_an_unreachable_server_are_errors_the_caller_can_catch()
    {
        var message = new EmailMessage("ada@example.com", "Hi", "Body");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.Host = null).SendAsync(message));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.FromAddress = "nonsense").SendAsync(message));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.ReplyToAddress = "nonsense").SendAsync(message));
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(null).SendAsync(message)); // nothing listens on port 1
    }
}
