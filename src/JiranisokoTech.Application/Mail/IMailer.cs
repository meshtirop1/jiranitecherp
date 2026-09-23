namespace JiranisokoTech.Application.Mail;

/// <summary>
/// Putting a message on its way.
/// </summary>
/// <remarks>
/// One method, and it either works or throws. Nothing here retries, batches or
/// schedules — that is the outbox's job, and a mailer that also retried would
/// mean two policies arguing about how many times an email gets sent.
/// </remarks>
public interface IMailer
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// One message, ready to send.
/// </summary>
/// <remarks>
/// Both a plain-text and an HTML body, always. A message with only HTML is one
/// that reads as a wall of markup in a client that will not render it, and a
/// message with only text looks broken next to everything else in an inbox. The
/// text part is written first and the HTML made from it, rather than the other
/// way round, because a stripped-out HTML body reads like a stripped-out HTML
/// body.
/// </remarks>
public sealed record EmailMessage(
    string ToAddress,
    string ToName,
    string Subject,
    string TextBody,
    string HtmlBody)
{
    /// <summary>
    /// A short line naming what this is, for the log.
    /// </summary>
    /// <remarks>
    /// The subject and the address, and deliberately not the body. A log that
    /// carries message bodies is a second copy of everybody's correspondence,
    /// kept somewhere with quite different access rules from the mailbox.
    /// </remarks>
    public override string ToString() => $"\"{Subject}\" to {ToAddress}";
}
