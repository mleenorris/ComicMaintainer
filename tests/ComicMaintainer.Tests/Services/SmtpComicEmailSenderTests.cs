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
    public async Task SendAsync_WhenTheServerIsUnreachable_Throws()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _settings.SmtpPort = closedPort;
        _settings.SmtpAllowInsecure = true;

        await Assert.ThrowsAnyAsync<SocketException>(() => _sender.SendAsync(
            new ComicEmailMessage("kindle@example.com", "Subject", "Body"),
            _guard.Token));

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("Sent comic email"));
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
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteAsync("250-localhost\r\n250 AUTH PLAIN\r\n");
                        break;
                    case "AUTH":
                        var payload = parts.Length > 2 ? parts[2] : null;
                        if (payload is null)
                        {
                            await writer.WriteLineAsync("334 ");
                            payload = await reader.ReadLineAsync(token) ?? string.Empty;
                        }

                        _authPayload = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
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
