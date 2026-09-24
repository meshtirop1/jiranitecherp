using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Notices;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Application.Notices;

/// <summary>
/// Where this application answers from, for a link somebody outside it has to follow.
/// </summary>
/// <remarks>
/// An abstraction rather than the configuration itself, because the application layer has no
/// business reading a mail setting — and because the thing it needs is one sentence: turn a path
/// this system uses into an address an inbox can open.
/// </remarks>
public interface IWhereThisLives
{
    /// <summary>The same path, as something a browser elsewhere can follow.</summary>
    string? Reachable(string? path);
}

/// <summary>What the notice centre needs read and written.</summary>
public interface INoticeRepository
{
    Task<List<Notice>> ForAsync(
        Guid employeeId, int most, CancellationToken cancellationToken = default);

    Task<int> UnreadForAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<Notice?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Notice>> UnreadListAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    Task<List<NoticeRule>> RulesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    Task<NoticeRule?> RuleForAsync(
        Guid employeeId, NoticeKind kind, CancellationToken cancellationToken = default);

    /// <summary>Where to send a person's email, or nothing when there is nowhere.</summary>
    Task<(string Address, string Name)?> MailboxAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    void Add(Notice notice);

    void Add(NoticeRule rule);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Telling one person that one thing happened.
/// </summary>
/// <remarks>
/// Sections 32 and 59 together, because a notice and the rule about how it reaches somebody are
/// one decision made at one moment — splitting them would mean the rule is consulted somewhere
/// that does not know what it is about.
///
/// <b>The notice is always recorded and only the email is optional.</b> What a person chooses
/// is what interrupts them, not what the system remembers. A record with holes in it is worse
/// than no record: somebody who muted a kind in March cannot tell in September whether a thing
/// did not happen or merely was not written down.
///
/// <b>Nothing here is called from a page.</b> Notices are written by domain event handlers,
/// through the outbox, so one cannot exist for something that then failed to save — a notice
/// about a hire that never happened stays on a screen, which is worse than an email nobody can
/// unsend.
/// </remarks>
public sealed class NoticeService(
    INoticeRepository notices,
    IMailer mailer,
    IWhereThisLives where,
    IClock clock,
    ILogger<NoticeService> logger)
{
    /// <summary>The most notices one page shows.</summary>
    /// <remarks>
    /// Fifty, and the page says when it is showing fewer than there are. A notice centre with
    /// no bound is a page that gets slower every month for a reader who only ever looks at the
    /// top of it.
    /// </remarks>
    public const int MostShown = 50;

    /// <summary>
    /// Tell somebody something happened.
    /// </summary>
    /// <remarks>
    /// The record first and the email second, deliberately, and the two failures are handled
    /// differently: a notice that cannot be written is an error worth raising, and an email
    /// that will not send is a line in the log. Losing the email loses an interruption; losing
    /// the notice loses the fact.
    /// </remarks>
    public async Task TellAsync(
        Guid employeeId,
        NoticeKind kind,
        string subject,
        string? link = null,
        CancellationToken cancellationToken = default)
    {
        notices.Add(Notice.For(employeeId, kind, subject, clock.Now, link));
        await notices.SaveAsync(cancellationToken);

        var rule = await notices.RuleForAsync(employeeId, kind, cancellationToken);

        if (!(rule?.ByEmail ?? NoticeRule.EmailedByDefault(kind)))
        {
            return;
        }

        if (await notices.MailboxAsync(employeeId, cancellationToken) is not { } mailbox)
        {
            /*
             * Said out loud rather than swallowed. An employee and an account are separate
             * things here on purpose, so somebody with no sign-in is an ordinary state — but a
             * firm where half the staff have no mailbox is one whose notices reach nobody, and
             * nothing else would say so.
             */
            logger.LogInformation(
                "{Employee} has no mailbox, so the {Kind} notice was recorded and not sent.",
                employeeId,
                kind);

            return;
        }

        try
        {
            /*
             * The stored notice keeps a relative link, because the page that shows it is
             * already here. The email cannot: a line reading "/work/01a0d205…" in somebody's
             * inbox is a link to nothing, which is how the first version of this went out.
             */
            await mailer.SendAsync(
                Letters.SomethingHappened(
                    mailbox.Address, mailbox.Name, subject, where.Reachable(link)),
                cancellationToken);
        }
        catch (Exception exception)
        {
            /*
             * Caught, because the notice is already written and the thing that caused it has
             * already happened. Letting a mail failure escape here would roll a handler back
             * and make the outbox try the whole thing again, which would write the notice
             * twice — a duplicate on somebody's screen because a mail server was slow.
             */
            logger.LogWarning(
                exception,
                "The {Kind} notice for {Employee} was recorded, and the email did not send.",
                kind,
                employeeId);
        }
    }

    public Task<List<Notice>> ForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        notices.ForAsync(employeeId, MostShown, cancellationToken);

    public Task<int> UnreadForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        notices.UnreadForAsync(employeeId, cancellationToken);

    /// <summary>
    /// Mark one read, and only if it is this person's.
    /// </summary>
    /// <remarks>
    /// The ownership check is here rather than on the page, because a notice identifier in a
    /// URL is a thing anybody can type — and a notice is addressed to one person by definition,
    /// so somebody else marking it read is somebody else editing a record about them.
    /// </remarks>
    public async Task ReadAsync(
        Guid employeeId, Guid noticeId, CancellationToken cancellationToken = default)
    {
        if (await notices.FindAsync(noticeId, cancellationToken) is not { } notice
            || notice.ForEmployeeId != employeeId)
        {
            return;
        }

        notice.Read(clock.Now);

        await notices.SaveAsync(cancellationToken);
    }

    public async Task NotReadAsync(
        Guid employeeId, Guid noticeId, CancellationToken cancellationToken = default)
    {
        if (await notices.FindAsync(noticeId, cancellationToken) is not { } notice
            || notice.ForEmployeeId != employeeId)
        {
            return;
        }

        notice.NotRead();

        await notices.SaveAsync(cancellationToken);
    }

    public async Task ReadEverythingAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        foreach (var notice in await notices.UnreadListAsync(employeeId, cancellationToken))
        {
            notice.Read(clock.Now);
        }

        await notices.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// What this person has chosen, filled in from the defaults.
    /// </summary>
    /// <remarks>
    /// Every kind comes back whether or not a row exists, because a preference screen that only
    /// showed the ones somebody had already changed would be a screen that starts empty and
    /// teaches nothing about what it can do.
    /// </remarks>
    public async Task<Dictionary<NoticeKind, bool>> RulesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var chosen = await notices.RulesForAsync(employeeId, cancellationToken);

        return Enum.GetValues<NoticeKind>().ToDictionary(
            kind => kind,
            kind => chosen.FirstOrDefault(one => one.Kind == kind)?.ByEmail
                ?? NoticeRule.EmailedByDefault(kind));
    }

    public async Task SetRuleAsync(
        Guid employeeId,
        NoticeKind kind,
        bool byEmail,
        CancellationToken cancellationToken = default)
    {
        if (await notices.RuleForAsync(employeeId, kind, cancellationToken) is { } rule)
        {
            rule.Email(byEmail);
        }
        else
        {
            notices.Add(NoticeRule.For(employeeId, kind, byEmail));
        }

        await notices.SaveAsync(cancellationToken);
    }
}
