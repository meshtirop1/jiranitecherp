using JiranisokoTech.Application.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace JiranisokoTech.Infrastructure.Mail;

/// <summary>
/// Sends over SMTP, and nothing else.
/// </summary>
/// <remarks>
/// A connection per message rather than one held open. This runs from the
/// outbox dispatcher, a few messages at a time with long gaps, and a pooled
/// connection kept across those gaps is one the server has already dropped —
/// which fails on the next send rather than on the idle, and so fails where it
/// is hardest to read.
///
/// MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which Microsoft
/// itself marks obsolete for new work and which does not do STARTTLS properly.
/// </remarks>
public sealed class SmtpMailer(
    IOptions<MailOptions> options,
    ILogger<SmtpMailer> logger) : IMailer
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            /*
             * Thrown rather than skipped. A mailer set to send but not told
             * where, that quietly does nothing, is the configuration mistake
             * nobody finds — every screen says the message went, and it never
             * did. Failing puts it in the outbox with an error somebody can
             * read.
             */
            throw new InvalidOperationException(
                "Mail is set to send over SMTP but no host or from-address is configured. "
                + "Set Mail__Host and Mail__FromAddress, or set Mail__Transport to File.");
        }

        var mime = new MimeMessage();

        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = message.Subject;

        mime.Body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
        }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };

        // StartTlsWhenAvailable rather than None: a server that offers TLS gets
        // it, and one that does not still works. Refusing to send in plain text
        // at all would be better, and is a decision for whoever configures the
        // server rather than one to make silently here.
        await client.ConnectAsync(
            settings.Host, settings.Port, SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        logger.LogInformation("Sent {Message}.", message);
    }
}

/// <summary>
/// Writes each message to a file and sends nothing.
/// </summary>
/// <remarks>
/// The default, for development and for the suite. What it writes is a readable
/// message somebody can open, which beats a log line and needs no mail server on
/// a laptop.
/// </remarks>
public sealed class FileMailer(
    IOptions<MailOptions> options,
    ILogger<FileMailer> logger) : IMailer
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var folder = options.Value.Directory;

        System.IO.Directory.CreateDirectory(folder);

        // Sortable, unique, and readable at a glance in a directory listing.
        var name = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmmss}-{Guid.CreateVersion7():N}.txt";

        var contents = $"""
            To: {message.ToName} <{message.ToAddress}>
            Subject: {message.Subject}

            {message.TextBody}

            --- as HTML ---

            {message.HtmlBody}
            """;

        await File.WriteAllTextAsync(
            Path.Combine(folder, name), contents, cancellationToken);

        logger.LogInformation("Wrote {Message} to {File}, and sent nothing.", message, name);
    }
}

/// <summary>Discards everything, and says so.</summary>
/// <remarks>
/// For a demo, or a restored copy of production where the last thing anybody
/// wants is real messages going to real people.
/// </remarks>
public sealed class NullMailer(ILogger<NullMailer> logger) : IMailer
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Discarded {Message}: mail is switched off.", message);

        return Task.CompletedTask;
    }
}
