namespace JiranisokoTech.Infrastructure.Mail;

/// <summary>How mail leaves this system, if it does.</summary>
public sealed class MailOptions
{
    public const string Section = "Mail";

    /// <summary>
    /// Which mailer to use.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="MailTransport.File"/>, and that is deliberate.
    /// A system that defaults to sending is one that emails real people from
    /// somebody's laptop the first time a test fixture runs against a copy of
    /// production data. Sending has to be switched on, by name, by whoever
    /// meant it.
    /// </remarks>
    public MailTransport Transport { get; set; } = MailTransport.File;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The address messages come from. Must be one the server will accept.
    /// </summary>
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Jiranisoko Tech Solutions";

    /// <summary>
    /// Where the application answers, for the links inside messages.
    /// </summary>
    /// <remarks>
    /// Configured rather than read from a request, because the messages that
    /// matter most are sent by the outbox dispatcher, which has no request to
    /// read. A link that points at localhost because a background job had no
    /// better idea is a link nobody can use.
    /// </remarks>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Where a file mailer writes. Relative paths are to the content root.
    /// </summary>
    public string Directory { get; set; } = "mail";

    /// <summary>
    /// How long to wait on the mail server before giving up on one message.
    /// </summary>
    /// <remarks>
    /// Short, because this runs inside an outbox handler: a mailer that hangs
    /// holds its claim on the message until the claim goes stale, and the queue
    /// behind it stops moving. Failing quickly and retrying later is what the
    /// backoff is for.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}

public enum MailTransport
{
    /// <summary>
    /// Write each message to a file and send nothing.
    /// </summary>
    /// <remarks>
    /// The default. Development and the test suite both use it, and what it
    /// writes is a readable message somebody can open — which is more useful
    /// than a log line and does not require a mail server on a laptop.
    /// </remarks>
    File = 0,

    /// <summary>Discard everything. For a demo, or a restored copy of production.</summary>
    None = 1,

    /// <summary>Actually send, over SMTP.</summary>
    Smtp = 2,
}
