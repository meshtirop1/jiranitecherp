using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Mail;

/// <summary>
/// The letters that go to somebody outside this firm.
/// </summary>
/// <remarks>
/// Every other message this system sends goes to a colleague who has an account
/// and can be told to look at a screen. These go to a member of the public who
/// has no account, will never see a screen, and for whom this correspondence is
/// the entire firm. A candidate who applies and hears nothing concludes within a
/// week that the advert was stale or the form was broken, and says so to other
/// people who might have applied.
///
/// This build sent none of them. An application arrived, a CV was stored, and
/// the candidate heard nothing at any point — not on arrival, not on rejection,
/// not on being invited to an interview, not on being offered the job.
///
/// Three rules govern everything here:
///
/// Nothing internal leaves. No scorecard, no panel reasoning, no rejection note,
/// no other candidate, nothing about how a decision was reached. These letters
/// are the one artefact certain to be forwarded.
///
/// A missing address is not a failure. Candidates are created from a public form
/// and the address is whatever they typed. Throwing would put the message
/// through eight retries and then abandon it, filling the log with the same line
/// and burying the failures that could have been recovered.
///
/// Handlers are idempotent in the only sense available to email: running twice
/// sends a duplicate somebody deletes, rather than causing a second decision.
/// </remarks>
internal static class CandidateLetters
{
    /// <summary>The candidate and the advert, or nothing if either has gone.</summary>
    /// <remarks>
    /// Both are read at dispatch rather than carried on the event, because the
    /// event deliberately carries identifiers only — a name and an address on an
    /// event are a copy of somebody's personal details in the outbox table,
    /// which is the last place anybody thinks to look for them.
    /// </remarks>
    internal static async Task<(Candidate Candidate, string JobTitle)?> FindAsync(
        AppDbContext database,
        Guid candidateId,
        Guid postingId,
        CancellationToken cancellationToken)
    {
        var candidate = await database.Candidates
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == candidateId, cancellationToken);

        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Email))
        {
            return null;
        }

        var title = await database.Postings
            .AsNoTracking()
            .Where(one => one.Id == postingId)
            .Select(one => one.Title)
            .FirstOrDefaultAsync(cancellationToken);

        // The advert can be closed and gone by the time a rejection is sent, and
        // the letter still has to name something. "the role you applied for"
        // reads badly but reads honestly, which beats an empty subject line.
        return (candidate, title ?? "the role you applied for");
    }
}

/// <summary>
/// Tells a candidate their application arrived.
/// </summary>
/// <remarks>
/// The cheapest letter this system sends and the one most worth having. It
/// costs nothing and it is the difference between a firm that looks organised
/// and one that looks like it lost the form.
/// </remarks>
public sealed class TellTheCandidateWeHaveIt(
    AppDbContext database,
    IMailer mailer,
    ILogger<TellTheCandidateWeHaveIt> logger)
    : IDomainEventHandler<ApplicationReceived>
{
    public async Task HandleAsync(
        ApplicationReceived domainEvent, CancellationToken cancellationToken = default)
    {
        var found = await CandidateLetters.FindAsync(
            database, domainEvent.CandidateId, domainEvent.PostingId, cancellationToken);

        if (found is not { } who)
        {
            logger.LogInformation(
                "No address to acknowledge application {ApplicationId}.",
                domainEvent.ApplicationId);

            return;
        }

        await mailer.SendAsync(
            Letters.ApplicationReceived(
                who.Candidate.Email, who.Candidate.FullName, who.JobTitle),
            cancellationToken);
    }
}

/// <summary>
/// Tells a candidate the answer, whichever way it went.
/// </summary>
/// <remarks>
/// Listens to the move rather than to two separate events, because rejection
/// and offer are the same transition with a different destination and splitting
/// them would mean two handlers that must agree about which statuses are final.
///
/// Every other move is silent on purpose. A candidate does not need to be told
/// they have reached "screening": it is an internal stage, it means nothing to
/// them, and a letter for every step trains people to ignore the letters that
/// matter.
/// </remarks>
public sealed class TellTheCandidateTheAnswer(
    AppDbContext database,
    IMailer mailer,
    ILogger<TellTheCandidateTheAnswer> logger)
    : IDomainEventHandler<ApplicationMoved>
{
    public async Task HandleAsync(
        ApplicationMoved domainEvent, CancellationToken cancellationToken = default)
    {
        if (domainEvent.To is not (ApplicationStatus.Rejected or ApplicationStatus.Offered))
        {
            return;
        }

        var found = await CandidateLetters.FindAsync(
            database, domainEvent.CandidateId, domainEvent.PostingId, cancellationToken);

        if (found is not { } who)
        {
            logger.LogInformation(
                "No address to write to about application {ApplicationId}.",
                domainEvent.ApplicationId);

            return;
        }

        var letter = domainEvent.To == ApplicationStatus.Offered
            ? Letters.Offer(who.Candidate.Email, who.Candidate.FullName, who.JobTitle)
            : Letters.ApplicationRefused(
                who.Candidate.Email, who.Candidate.FullName, who.JobTitle);

        await mailer.SendAsync(letter, cancellationToken);
    }
}

/// <summary>
/// Invites a candidate to an interview that has been scheduled.
/// </summary>
/// <remarks>
/// Sent when the interview is scheduled rather than when the application moves
/// to interviewing, because the two are different moments: the application can
/// be moved to interviewing days before anybody agrees a time, and an invitation
/// with no time in it is not an invitation.
///
/// Rescheduling raises its own event and is not handled here yet — a candidate
/// told the wrong time is worse than one told late, so that gap is named rather
/// than half-filled.
/// </remarks>
public sealed class InviteTheCandidate(
    AppDbContext database,
    IMailer mailer,
    ILogger<InviteTheCandidate> logger)
    : IDomainEventHandler<InterviewScheduled>
{
    public async Task HandleAsync(
        InterviewScheduled domainEvent, CancellationToken cancellationToken = default)
    {
        var application = await database.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == domainEvent.ApplicationId, cancellationToken);

        if (application is null)
        {
            return;
        }

        var found = await CandidateLetters.FindAsync(
            database, application.CandidateId, application.PostingId, cancellationToken);

        if (found is not { } who)
        {
            logger.LogInformation(
                "No address to invite for interview {InterviewId}.", domainEvent.InterviewId);

            return;
        }

        // Read from the interview rather than the event, so that a time changed
        // between the scheduling and the dispatch goes out correct.
        var interview = await database.Interviews
            .AsNoTracking()
            .FirstOrDefaultAsync(one => one.Id == domainEvent.InterviewId, cancellationToken);

        if (interview is not { Status: InterviewStatus.Scheduled })
        {
            // Cancelled before anybody was told. Sending the invitation now
            // would have somebody arrive for a conversation nobody is expecting.
            return;
        }

        await mailer.SendAsync(
            Letters.InterviewInvitation(
                who.Candidate.Email,
                who.Candidate.FullName,
                who.JobTitle,
                interview.ScheduledFor,
                interview.Where),
            cancellationToken);
    }
}
