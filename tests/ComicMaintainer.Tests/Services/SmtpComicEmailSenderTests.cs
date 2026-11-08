using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ComicMaintainer.Core.Configuration;
using ComicMaintainer.Core.Interfaces;
using ComicMaintainer.Core.Services;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;

namespace ComicMaintainer.Tests.Services;

/// <summary>
/// Exercises <see cref="SmtpComicEmailSender"/> against an in-process fake SMTP
/// server on the loopback interface, so the real MailKit conversation runs
/// without any external mail server.
/// </summary>
public class SmtpComicEmailSenderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _workDir;
    private readonly AppSettings _settings;
    private readonly CapturingLogger _logger = new();
    private readonly SmtpComicEmailSender _sender;
    private readonly CancellationTokenSource _guard = new(Timeout);

    public SmtpComicEmailSenderTests()
    {
        _workDir = Path.Join(Path.GetTempPath(), $"smtp-sender-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        _settings = new AppSettings
        {
            SmtpHost = "127.0.0.1",
            SmtpPort = 25,
            EmailFromAddress = "comics@example.com"
        };

        var options = new Mock<IOptionsMonitor<AppSettings>>();
        options.Setup(o => o.CurrentValue).Returns(() => _settings);
        _sender = new SmtpComicEmailSender(options.Object, _logger);
    }

    [Theory]
    [InlineData("smtp.example.com", "comics@example.com", true)]
    [InlineData(null, "comics@example.com", false)]
    [InlineData("", "comics@example.com", false)]
    [InlineData("   ", "comics@example.com", false)]
    [InlineData("smtp.example.com", null, false)]
    [InlineData("smtp.example.com", "not-an-address", false)]
    public void IsConfigured_RequiresAHostAndAValidFromAddress(string? host, string? from, bool expected)
    {
        _settings.SmtpHost = host;
        _settings.EmailFromAddress = from;

        Assert.Equal(expected, _sender.IsConfigured);
    }

    [Fact]
    public async Task SendAsync_RejectsANullMessage()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sender.SendAsync(null!));
    }

    [Fact]
    public async Task SendAsync_WhenNotConfigured_ThrowsWithoutConnecting()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.EmailFromAddress = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sender.SendAsync(new ComicEmailMessage("kindle@example.com", "Subject", "Body"), _guard.Token));

        Assert.Contains("not configured", ex.Message);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task SendAsync_OverAnAllowedInsecureRelay_AuthenticatesAndSendsTheAttachment()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;
        _settings.SmtpUsername = "reader";
        _settings.SmtpPassword = null;
        _settings.EmailFromName = null!;

        var attachment = Path.Join(_workDir, "Series 001.cbz");
        var content = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(attachment, content);

        await _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Series 001", "Enjoy!", attachment),
            _guard.Token);

        // PLAIN carries "authzid NUL user NUL password"; a missing password is sent as empty.
        Assert.Equal("\0reader\0", server.AuthPayload);

        var mime = await server.Message.WaitAsync(Timeout);
        var from = Assert.Single(mime.From.Mailboxes);
        Assert.Equal("ComicMaintainer", from.Name);
        Assert.Equal("comics@example.com", from.Address);
        Assert.Equal("kindle@example.com", Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal("Series 001", mime.Subject);
        Assert.Equal("Enjoy!", mime.TextBody?.Trim());

        var part = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("Series 001.cbz", part.FileName);
        Assert.Equal("application/octet-stream", part.ContentType.MimeType);
        Assert.Equal(content, Decode(part));

        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("is not encrypted because SmtpAllowInsecure is enabled"));
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Sent comic email to kindle@example.com"));
        Assert.Contains(server.Commands, c => c.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendAsync_UsesTheGivenAttachmentNameContentTypeAndPassword()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;
        _settings.SmtpUsername = "reader";
        _settings.SmtpPassword = "s3cret";
        _settings.EmailFromName = "My Library";

        var attachment = Path.Join(_workDir, "work-file.tmp");
        await File.WriteAllTextAsync(attachment, "epub payload");

        await _sender.SendAsync(
            new ComicEmailMessage(
                "kobo@example.com",
                "Book",
                "Body",
                attachment,
                "Series 001-010.epub",
                "application/epub+zip"),
            _guard.Token);

        Assert.Equal("\0reader\0s3cret", server.AuthPayload);

        var mime = await server.Message.WaitAsync(Timeout);
        Assert.Equal("My Library", Assert.Single(mime.From.Mailboxes).Name);
        var part = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("Series 001-010.epub", part.FileName);
        Assert.Equal("application/epub+zip", part.ContentType.MimeType);
        Assert.Equal("epub payload", Encoding.UTF8.GetString(Decode(part)));
    }

    [Fact]
    public async Task SendAsync_WithoutCredentialsOrAttachment_SendsAPlainMessage()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;
        _settings.SmtpUsername = "   ";
        _settings.SmtpPassword = "unused";

        await _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Test email", "It works", "  "),
            _guard.Token);

        var mime = await server.Message.WaitAsync(Timeout);
        Assert.Empty(mime.Attachments);
        Assert.Equal("It works", mime.TextBody?.Trim());
        Assert.Null(server.AuthPayload);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendAsync_WhenStartTlsIsRequiredButNotOffered_RefusesToSendInTheClear()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = false;
        _settings.SmtpUsername = "reader";
        _settings.SmtpPassword = "s3cret";

        await Assert.ThrowsAsync<NotSupportedException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        Assert.Null(server.AuthPayload);
        Assert.DoesNotContain(server.Commands, c =>
            c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
        Assert.False(server.Message.IsCompleted);
    }

    [Fact]
    public async Task SendAsync_WithImplicitTls_FailsAgainstAPlainTextServer()
    {
        using var server = new FakeSmtpServer();
        _settings.SmtpPort = server.Port;
        _settings.SmtpUseSsl = true;
        _settings.SmtpAllowInsecure = true;

        await Assert.ThrowsAsync<SslHandshakeException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        Assert.False(server.Message.IsCompleted);
    }

    [Fact]
    public async Task SendAsync_WhenTheServerIsUnreachable_ThrowsATransientFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _settings.SmtpPort = closedPort;
        _settings.SmtpAllowInsecure = true;

        var ex = await Assert.ThrowsAsync<TransientEmailSendException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        Assert.IsAssignableFrom<SocketException>(ex.InnerException);

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("Sent comic email"));
    }

    [Theory]
    [InlineData("MAIL", "421 4.7.0 Try again later, closing connection.", 421)]
    [InlineData("MAIL", "451 4.3.0 Mail server temporarily rejected message.", 451)]
    [InlineData("RCPT", "450 4.2.1 The user you are trying to contact is receiving mail too quickly.", 450)]
    [InlineData("RCPT", "452 4.2.2 Mailbox full.", 452)]
    public async Task SendAsync_WhenTheServerRepliesWithATemporaryError_ThrowsATransientFailure(
        string verb, string reply, int statusCode)
    {
        using var server = new FakeSmtpServer();
        server.Replies[verb] = reply;
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        var ex = await Assert.ThrowsAsync<TransientEmailSendException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        var command = Assert.IsType<MailKit.Net.Smtp.SmtpCommandException>(ex.InnerException);
        Assert.Equal(statusCode, (int)command.StatusCode);
        Assert.Contains(statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
        Assert.False(server.Message.IsCompleted);
    }

    [Theory]
    [InlineData("MAIL", "552 5.3.4 Message size exceeds fixed maximum message size")]
    [InlineData("RCPT", "550 5.1.1 The email account that you tried to reach does not exist.")]
    public async Task SendAsync_WhenTheServerRejectsTheMessagePermanently_DoesNotReportItAsTransient(string verb, string reply)
    {
        using var server = new FakeSmtpServer();
        server.Replies[verb] = reply;
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        await Assert.ThrowsAsync<MailKit.Net.Smtp.SmtpCommandException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));
    }

    [Fact]
    public async Task SendAsync_WhenTheCredentialsAreRejected_DoesNotReportItAsTransient()
    {
        using var server = new FakeSmtpServer();
        server.Replies["AUTH"] = "535 5.7.8 Username and Password not accepted.";
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;
        _settings.SmtpUsername = "reader";
        _settings.SmtpPassword = "wrong";

        await Assert.ThrowsAsync<AuthenticationException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));
    }

    [Theory]
    [InlineData("454 4.7.0 Too many login attempts, please try again later.", 454)]
    [InlineData("421 4.7.0 Temporary System Problem. Try again later.", 421)]
    public async Task SendAsync_WhenTheLoginIsRefusedTemporarily_ThrowsATransientFailure(string reply, int status)
    {
        using var server = new FakeSmtpServer();
        server.Replies["AUTH"] = reply;
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;
        _settings.SmtpUsername = "reader";
        _settings.SmtpPassword = "s3cret";

        var ex = await Assert.ThrowsAsync<TransientEmailSendException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        Assert.Contains("temporarily refused the login", ex.Message);
        Assert.Contains($"({status})", ex.Message);
        Assert.False(server.Message.IsCompleted);
    }

    [Fact]
    public async Task SendAsync_WhenTheConnectionDropsMidConversation_ThrowsATransientFailure()
    {
        using var server = new FakeSmtpServer();
        server.DropConnectionOn = "RCPT";
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        await Assert.ThrowsAsync<TransientEmailSendException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));
    }

    [Fact]
    public async Task SendAsync_WhenClosingTheConnectionFailsAfterTheMessageWasAccepted_StillSucceeds()
    {
        using var server = new FakeSmtpServer();
        server.DropConnectionOn = "QUIT";
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        await _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token);

        Assert.True(server.Message.IsCompletedSuccessfully);
        Assert.Contains(_logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains("Sent comic email to kindle@example.com"));
    }

    [Fact]
    public async Task SendAsync_WhenTheEncodedEmailExceedsTheServersSizeLimit_FailsBeforeUploading()
    {
        using var server = new FakeSmtpServer { MaxMessageSize = 100_000 };
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        // 90 KB fits the advertised 100 KB on disk, but not once base64 encoded.
        var attachment = Path.Join(_workDir, "Series 001.epub");
        await File.WriteAllBytesAsync(attachment, new byte[90_000]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Series 001", "Body", attachment),
            _guard.Token));

        Assert.Contains("once encoded", ex.Message);
        Assert.Contains("accepts at most", ex.Message);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase));
        Assert.False(server.Message.IsCompleted);
    }

    [Fact]
    public async Task SendAsync_WhenTheEmailFitsTheServersSizeLimit_SendsTheWholeAttachment()
    {
        using var server = new FakeSmtpServer { MaxMessageSize = 10 * 1024 * 1024 };
        _settings.SmtpPort = server.Port;
        _settings.SmtpAllowInsecure = true;

        var attachment = Path.Join(_workDir, "Series 001.epub");
        var content = new byte[300_000];
        new Random(42).NextBytes(content);
        await File.WriteAllBytesAsync(attachment, content);

        await _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Series 001", "Body", attachment, "Series 001.epub", "application/epub+zip"),
            _guard.Token);

        var mime = await server.Message.WaitAsync(Timeout);
        var part = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal(ContentEncoding.Base64, part.ContentTransferEncoding);
        Assert.Equal(content, Decode(part));
        Assert.Contains(server.Commands, c => c.StartsWith("MAIL", StringComparison.OrdinalIgnoreCase) && c.Contains("SIZE="));
    }

    public static TheoryData<Exception, bool> TransientClassificationCases() => new()
    {
        { new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted, MailKit.Net.Smtp.SmtpStatusCode.ServiceNotAvailable, "busy"), true },
        { new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted, MailKit.Net.Smtp.SmtpStatusCode.MailboxUnavailable, "no such user"), false },
        { new MailKit.Net.Smtp.SmtpProtocolException("unexpected disconnect"), true },
        { new IOException("connection reset"), true },
        { new SocketException((int)SocketError.ConnectionRefused), true },
        { new TimeoutException(), true },
        { new OperationCanceledException(), true },
        { new AuthenticationException("bad password"), false },
        { new AuthenticationException("throttled", new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode, (MailKit.Net.Smtp.SmtpStatusCode)454, "try later")), true },
        { new AuthenticationException("rejected", new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode, (MailKit.Net.Smtp.SmtpStatusCode)535, "bad password")), false },
        { new AuthenticationException("other", new IOException("not an SMTP reply")), false },
        { new SslHandshakeException("handshake failed"), false },
        { new System.Security.Authentication.AuthenticationException("tls"), false },
        { new NotSupportedException("STARTTLS required"), false },
        { new InvalidOperationException("not configured"), false },
    };

    [Theory]
    [MemberData(nameof(TransientClassificationCases))]
    public void IsTransient_RetriesTemporaryFailuresOnly(Exception exception, bool expected)
    {
        Assert.Equal(expected, SmtpComicEmailSender.IsTransient(exception, CancellationToken.None));
    }

    public static TheoryData<Exception, string?> FailureDescriptionCases() => new()
    {
        { new TransientEmailSendException("x", new TimeoutException("secret")), "The mail server did not respond in time." },
        { new TransientEmailSendException("x", new OperationCanceledException("secret")), "The mail server did not respond in time." },
        { new TransientEmailSendException("x", new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode, (MailKit.Net.Smtp.SmtpStatusCode)451, "secret reply")), "The mail server temporarily refused the message (SMTP 451). Try again later." },
        { new TransientEmailSendException("x", new AuthenticationException("secret", new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode, (MailKit.Net.Smtp.SmtpStatusCode)454, "secret reply"))), "The mail server temporarily refused the login (SMTP 454). Try again later." },
        { new TransientEmailSendException("x", new SocketException((int)SocketError.ConnectionRefused)), "Could not reach the mail server. Check the SMTP host and port in Settings." },
        { new MailKit.Net.Smtp.SmtpCommandException(MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted, (MailKit.Net.Smtp.SmtpStatusCode)550, "secret reply"), "The mail server refused the message (SMTP 550)." },
        { new AuthenticationException("secret"), "The mail server rejected the login. Check the SMTP username and password in Settings." },
        { new SslHandshakeException("secret"), "A secure connection to the mail server could not be established. Check the SMTP port and TLS settings." },
        { new System.Security.Authentication.AuthenticationException("secret"), "A secure connection to the mail server could not be established. Check the SMTP port and TLS settings." },
        { new NotSupportedException("secret"), "The mail server does not offer a secure connection. Check the SMTP port and TLS settings." },
        { new MimeKit.ParseException("secret", 0, 0), "The device's email address is not valid." },
        { new InvalidOperationException("secret"), null },
        { new OperationCanceledException("secret"), null },
    };

    [Theory]
    [MemberData(nameof(FailureDescriptionCases))]
    public void DescribeFailureForUser_NamesTheFailureWithoutItsText(Exception exception, string? expected)
    {
        var description = SmtpComicEmailSender.DescribeFailureForUser(exception);

        Assert.Equal(expected, description);
        Assert.DoesNotContain("secret", description ?? string.Empty);
    }

    [Fact]
    public void IsTransient_DoesNotRetryACancellationTheCallerAskedFor()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(SmtpComicEmailSender.IsTransient(new OperationCanceledException(cts.Token), cts.Token));
    }

    private static byte[] Decode(MimePart part)
    {
        using var buffer = new MemoryStream();
        Assert.NotNull(part.Content);
        part.Content.DecodeTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _guard.Dispose();
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<SmtpComicEmailSender>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// Minimal plain-text SMTP server: advertises AUTH PLAIN only (no STARTTLS),
    /// accepts every command and captures the authentication payload and the
    /// message submitted with DATA.
    /// </summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<MimeMessage> _message =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly Task _loop;
        private volatile string? _authPayload;
        private int _connectionCount;

        public FakeSmtpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public int Port { get; }

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public string? AuthPayload => _authPayload;

        public IReadOnlyCollection<string> Commands => _commands.ToArray();

        public Task<MimeMessage> Message => _message.Task;

        /// <summary>Replies sent instead of the usual one, by command verb (e.g. <c>MAIL</c>).</summary>
        public ConcurrentDictionary<string, string> Replies { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Command verb on which the server hangs up without replying.</summary>
        public string? DropConnectionOn { get; set; }

        /// <summary>Message size limit advertised with the SIZE extension; none when null.</summary>
        public long? MaxMessageSize { get; set; }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _connectionCount);
                using (client)
                {
                    try
                    {
                        await HandleAsync(client.GetStream(), token);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or FormatException)
                    {
                        // The client dropped the connection (e.g. after a failed TLS handshake).
                    }
                }
            }
        }

        private async Task HandleAsync(NetworkStream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            await writer.WriteLineAsync("220 localhost ESMTP fake");
            while (await reader.ReadLineAsync(token) is { } line)
            {
                _commands.Enqueue(line);
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var verb = parts.Length > 0 ? parts[0].ToUpperInvariant() : string.Empty;
                if (string.Equals(verb, DropConnectionOn, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteAsync(MaxMessageSize is long maxSize
                            ? $"250-localhost\r\n250-SIZE {maxSize}\r\n250 AUTH PLAIN\r\n"
                            : "250-localhost\r\n250 AUTH PLAIN\r\n");
                        break;
                    case "AUTH":
                        var payload = parts.Length > 2 ? parts[2] : null;
                        if (payload is null)
                        {
                            await writer.WriteLineAsync("334 ");
                            payload = await reader.ReadLineAsync(token) ?? string.Empty;
                        }

                        _authPayload = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        await writer.WriteLineAsync(Replies.TryGetValue(verb, out var authReply)
                            ? authReply
                            : "235 2.7.0 Authentication successful");
                        break;
                    case var scripted when Replies.TryGetValue(scripted, out var reply):
                        await writer.WriteLineAsync(reply);
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(token) is { } dataLine && dataLine != ".")
                        {
                            data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine)
                                .Append("\r\n");
                        }

                        using (var raw = new MemoryStream(Encoding.Latin1.GetBytes(data.ToString())))
                        {
                            _message.TrySetResult(await MimeMessage.LoadAsync(raw, token));
                        }

                        await writer.WriteLineAsync("250 2.0.0 Queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 2.0.0 OK");
                        break;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The loop only ends with an exception the test already observed.
            }

            _cts.Dispose();
        }
    }
}
