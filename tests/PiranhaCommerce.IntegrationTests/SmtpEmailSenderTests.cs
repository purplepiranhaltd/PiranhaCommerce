using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Email.Smtp;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>An in-memory stand-in for the database store, so the sender can be tested alone.</summary>
internal sealed class MemoryTemplateStore : ISmtpTemplateStore
{
    private readonly ConcurrentDictionary<string, StoredSmtpTemplate> _rows = new();
    public Task<StoredSmtpTemplate?> FindAsync(string key, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(key));
    public Task<IReadOnlyList<StoredSmtpTemplate>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredSmtpTemplate>>(_rows.Values.ToList());
    public Task SaveAsync(StoredSmtpTemplate t, CancellationToken ct = default) { _rows[t.Key] = t; return Task.CompletedTask; }
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(_rows.TryRemove(key, out _));
}

/// <summary>The templates a test host's catalog would have, without a host.</summary>
internal sealed class TestCatalog(params EmailTemplateDescriptor[] templates) : IEmailTemplateCatalog
{
    public IReadOnlyList<EmailTemplateDescriptor> All { get; } = templates;
    public EmailTemplateDescriptor? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}

/// <summary>The SMTP sender against a tiny in-process SMTP server (no TLS, nothing leaves the machine).</summary>
public class SmtpEmailSenderTests
{
    internal sealed class FakeSmtpServer : IAsyncDisposable
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

    internal static SmtpEmailSender Sender(FakeSmtpServer? server, Action<SmtpEmailOptions>? tweak = null, ISmtpTemplateStore? store = null)
    {
        var o = new SmtpEmailOptions
        {
            Host = "127.0.0.1", Port = server?.Port ?? 1, Security = SmtpSecurity.None, FromAddress = "shop@example.com", FromName = "Purple Shop", TimeoutSeconds = 5,
        };
        tweak?.Invoke(o);
        return new SmtpEmailSender(Options.Create(o), store ?? new MemoryTemplateStore(), NullLogger<SmtpEmailSender>.Instance);
    }

    internal static MimeMessage Parse(string data) => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(data)));

    internal static Dictionary<string, object?> ResetVariables(string code = "abc.def") => new()
    {
        ["email"] = "ada@example.com", ["link"] = null, ["code"] = code, ["expiresInMinutes"] = 60,
    };

    [Fact]
    public async Task A_message_arrives_with_the_templates_words_filled_in_with_its_variables()
    {
        await using var server = new FakeSmtpServer();
        await Sender(server, o => o.ReplyToAddress = "help@example.com")
            .SendAsync(new EmailMessage("ada@example.com", "customer.set-password", ResetVariables("code-123")));

        var got = Assert.Single(server.Received);
        Assert.Contains("shop@example.com", got.From);
        Assert.Equal(["<ada@example.com>"], got.Recipients);
        var mime = Parse(got.Data);
        Assert.Equal("Set your password", mime.Subject);
        Assert.Equal("Purple Shop", mime.From.Mailboxes.Single().Name);
        Assert.Equal("help@example.com", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Contains("Your code is:", mime.TextBody);
        Assert.Contains("code-123", mime.TextBody);
        Assert.Contains("expires in 60 minutes", mime.TextBody);
    }

    [Fact]
    public async Task An_edited_template_is_used_instead_of_the_shipped_wording_and_html_is_sent_alongside_the_text()
    {
        await using var server = new FakeSmtpServer();
        var store = new MemoryTemplateStore();
        await store.SaveAsync(new StoredSmtpTemplate("customer.set-password", "New password for {{ email }}", "Use {{ code }}", "<p>Use <b>{{ code }}</b></p>", DateTimeOffset.UtcNow, "staff"));

        await Sender(server, store: store).SendAsync(new EmailMessage("ada@example.com", "customer.set-password", ResetVariables("zzz-999")));

        var mime = Parse(Assert.Single(server.Received).Data);
        Assert.Equal("New password for ada@example.com", mime.Subject);
        Assert.Equal("Use zzz-999", mime.TextBody!.Trim());
        Assert.Contains("<b>zzz-999</b>", mime.HtmlBody);
    }

    [Fact]
    public async Task Variables_are_html_encoded_in_the_html_part_and_left_alone_in_the_text_part()
    {
        await using var server = new FakeSmtpServer();
        var store = new MemoryTemplateStore();
        await store.SaveAsync(new StoredSmtpTemplate("customer.set-password", "Hi", "Code: {{ code }}", "<p>Code: {{ code }}</p>", DateTimeOffset.UtcNow, null));

        await Sender(server, store: store).SendAsync(new EmailMessage("ada@example.com", "customer.set-password", ResetVariables("<script>alert(1)</script>")));

        var mime = Parse(Assert.Single(server.Received).Data);
        Assert.Contains("<script>alert(1)</script>", mime.TextBody);   // plain text has no markup to inject into
        Assert.DoesNotContain("<script>", mime.HtmlBody!);              // HTML does
        Assert.Contains("&lt;script&gt;", mime.HtmlBody);
    }

    [Fact]
    public async Task A_line_break_in_a_variable_cannot_put_another_header_in_the_subject()
    {
        await using var server = new FakeSmtpServer();
        var store = new MemoryTemplateStore();
        await store.SaveAsync(new StoredSmtpTemplate("customer.set-password", "Hello {{ email }}", "Body", null, DateTimeOffset.UtcNow, null));
        var variables = ResetVariables();
        variables["email"] = "ada@example.com\r\nBcc: evil@example.com";

        await Sender(server, store: store).SendAsync(new EmailMessage("ada@example.com", "customer.set-password", variables));

        var got = Assert.Single(server.Received);
        Assert.Equal(["<ada@example.com>"], got.Recipients);
        var mime = Parse(got.Data);
        Assert.Empty(mime.Bcc);
        Assert.Null(mime.Headers["Bcc"]);
        Assert.StartsWith("Hello", mime.Subject);
    }

    [Theory]
    [InlineData("erased@erased.invalid")]
    [InlineData("Someone@Example.INVALID")]
    [InlineData("a@mail.example.invalid.")]
    public async Task An_address_that_can_never_deliver_is_skipped_without_connecting_to_the_server(string to)
    {
        await using var server = new FakeSmtpServer();
        await Sender(server).SendAsync(new EmailMessage(to, "customer.set-password", ResetVariables())); // no error, no connection, no cost
        Assert.Empty(server.Received);

        // Nothing is even listening here: a real attempt would throw, so quietly returning proves it never tried.
        await Sender(null).SendAsync(new EmailMessage(to, "customer.set-password", ResetVariables()));

        // Look-alikes are real addresses and are sent as normal.
        await Sender(server).SendAsync(new EmailMessage("ada@invalidity.example", "customer.set-password", ResetVariables()));
        await Sender(server).SendAsync(new EmailMessage("ada@example.com", "customer.set-password", ResetVariables()));
        Assert.Equal(2, server.Received.Count);
    }

    [Fact]
    public async Task A_key_with_no_stored_or_shipped_wording_fails_clearly_and_sends_nothing()
    {
        await using var server = new FakeSmtpServer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sender(server).SendAsync(new EmailMessage("ada@example.com", "some.other.plugin", new Dictionary<string, object?>())));
        Assert.Contains("some.other.plugin", ex.Message);
        Assert.Empty(server.Received);
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
        await Assert.ThrowsAsync<ArgumentException>(() => Sender(server).SendAsync(new EmailMessage(to, "customer.set-password", ResetVariables())));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Missing_settings_and_an_unreachable_server_are_errors_the_caller_can_catch()
    {
        var message = new EmailMessage("ada@example.com", "customer.set-password", ResetVariables());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.Host = null).SendAsync(message));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.FromAddress = "nonsense").SendAsync(message));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(null, o => o.ReplyToAddress = "nonsense").SendAsync(message));
        await Assert.ThrowsAnyAsync<Exception>(() => Sender(null).SendAsync(message)); // nothing listens on port 1
    }
}
