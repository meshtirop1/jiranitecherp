using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Notices;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JiranisokoTech.Infrastructure.Support;

/// <summary>
/// Tell somebody senior when the firm has not answered a person it promised to answer.
/// </summary>
/// <remarks>
/// Section 26's escalation, and the whole section's only outward act. Everything else on the help
/// desk waits for a person to open a page; a missed promise is the one thing that must arrive
/// unasked, because the person waiting is outside the firm and cannot ring the bell twice.
///
/// <b>What this does not do is change the ticket's state.</b> No "Escalated" status, no bumped
/// priority. Both are the same mistake in different clothes: they would make the record say the
/// firm had acted when all that happened was that a clock ran out. The state says where the
/// ticket is and the priority says what the requester cannot do, and neither of those is altered
/// by the firm being late. All that is recorded is that somebody was told, once.
///
/// Every half hour. The shortest promise this firm makes is four hours, so half an hour is an
/// eighth of the tightest deadline and the notice is still worth acting on when it arrives — and
/// it is an interval rather than a poll, because the thing being looked for changes on the hour
/// scale and not the minute one.
/// </remarks>
public sealed class EscalateMissedPromises(
    AppDbContext database,
    PeopleQueries people,
    NoticeService notices,
    IClock clock,
    ILogger<EscalateMissedPromises> logger) : IRecurringJob
{
    /// <summary>
    /// Enough for any real backlog, and a bound instead of a page.
    /// </summary>
    /// <remarks>
    /// A firm of twenty with fifty unanswered promises has a problem this job cannot fix, and
    /// the run after this one picks up whatever is left. What the bound prevents is the case that
    /// actually happens: somebody restores a database of old tickets and the first sweep writes a
    /// notice for every one of them.
    /// </remarks>
    private const int Batch = 50;

    public string Name => "support.escalate";

    public string Description =>
        "Tells whoever answers for the desk when a promised reply has not been written.";

    public TimeSpan Every => TimeSpan.FromMinutes(30);

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.Now;

        /*
         * The same three conditions the navigation's count uses, plus "and nobody has been told".
         * Deliberately the same three: a number beside a link and a notice in somebody's list
         * that disagreed about what counts as late would be two answers to one question, and the
         * one people would believe is whichever they saw first.
         */
        var late = await database.Tickets
            .Where(one => one.FirstRespondedAt == null
                && one.Status != TicketStatus.Resolved
                && one.RespondBy < now
                && one.EscalatedAt == null)
            .OrderBy(one => one.RespondBy)
            .Take(Batch)
            .ToListAsync(cancellationToken);

        if (late.Count == 0)
        {
            return "Every promise still standing is being kept.";
        }

        var heads = await people.WhoHeadsADepartmentAsync(cancellationToken);
        var told = 0;

        foreach (var ticket in late)
        {
            var recipients = await WhoToTellAsync(ticket, heads, cancellationToken);

            if (recipients.Count == 0)
            {
                /*
                 * Said out loud rather than swallowed, and the ticket is deliberately NOT marked.
                 * A firm with no department heads and no reporting lines is a firm whose
                 * escalations reach nobody, and marking the ticket would turn that into silence
                 * that looks like success — the sweep would report "told 1" for ever afterwards.
                 * Leaving it unmarked means it escalates the moment somebody sets a head.
                 */
                logger.LogWarning(
                    "{Reference} missed its promise and there is nobody to tell: it has no "
                    + "assignee, and no department has a head recorded.",
                    ticket.Reference);

                continue;
            }

            /*
             * Marked and saved BEFORE anybody is told, which is the deliberate direction.
             *
             * NoticeService saves once per recipient, so there is no transaction that can hold
             * the mark and every notice together. One of the two orders fails quietly and the
             * other fails loudly: mark first and a crash in the middle means one escalation was
             * never sent, which is a single missed notice; tell first and a crash means the mark
             * is never written, so the next sweep tells everybody again, and the one after that,
             * for ever. The quiet failure is the right one to choose, because the loud one
             * destroys the thing this column exists to protect.
             */
            ticket.Escalated(now);
            await database.SaveChangesAsync(cancellationToken);

            var howLate = Length(now - ticket.RespondBy);

            foreach (var person in recipients)
            {
                await notices.TellAsync(
                    person,
                    NoticeKind.PromiseMissed,
                    $"{ticket.Reference} has had no answer and is {howLate} past what we "
                    + $"promised: {ticket.Subject}",
                    $"/support/{ticket.Number}",
                    cancellationToken);
            }

            told++;
        }

        return told == 0
            ? $"{late.Count} promises were missed and there was nobody to tell about any of them."
            : told == 1
                ? "One missed promise was escalated."
                : $"{told} missed promises were escalated.";
    }

    /// <summary>
    /// Whoever should hear about this one.
    /// </summary>
    /// <remarks>
    /// The assignee as well as the person above them, because those are two different messages
    /// arriving at the same moment: the assignee may simply not have looked at the desk today,
    /// and the manager needs to know before the client tells them. Telling only the manager would
    /// make an escalation a complaint about somebody who had not yet been asked.
    ///
    /// With nobody assigned there is no person to go above, so it goes to the heads of the firm's
    /// departments — see <c>PeopleQueries.WhoHeadsADepartmentAsync</c> for why that is the
    /// fallback rather than nobody.
    /// </remarks>
    private async Task<List<Guid>> WhoToTellAsync(
        Ticket ticket, List<Guid> heads, CancellationToken cancellationToken)
    {
        if (ticket.AssigneeId is not { } assignee)
        {
            return heads;
        }

        var above = await people.WhoAnswersForAsync(assignee, cancellationToken);

        return [assignee, .. above.Where(one => one != assignee)];
    }

    /// <summary>
    /// How late, said the way a person would say it.
    /// </summary>
    /// <remarks>
    /// The same shape the help desk and the incidents pages use. It goes into a subject line
    /// somebody reads on a telephone, where "4.25 hours" is harder to act on than "4 hr".
    /// </remarks>
    private static string Length(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "moments";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;

            return minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
        }

        return $"{(int)span.TotalDays} days";
    }
}
