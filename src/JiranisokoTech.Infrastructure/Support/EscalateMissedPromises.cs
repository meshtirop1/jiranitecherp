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

        var heads = await people.WhoHeadsADepartmentAsync(cancellationToken);

        /*
         * The same condition the navigation's count uses, plus "and nobody has been told".
         * Deliberately the same: a number beside a link and a notice in somebody's list that
         * disagreed about what counts as late would be two answers to one question, and the one
         * people would believe is whichever they saw first.
         *
         * AnswerOwedBy is what makes it one condition rather than three. It is null unless
         * somebody is waiting, so a resolved ticket and an answered one are both excluded without
         * a word about either — and a ticket that was answered, chased, and left is included,
         * which the first version of this sweep could not see at all.
         */
        var owed = database.Tickets
            .Where(one => one.AnswerOwedBy != null
                && one.AnswerOwedBy < now
                && one.EscalatedAt == null);

        /*
         * With nobody heading a department there is nobody to tell about an unassigned ticket, so
         * those are counted, said out loud, and left out of the batch.
         *
         * Leaving them out is not tidiness. The batch is fifty rows ordered by how late they are,
         * and fifty untellable ones at the front would push every tellable ticket out of every
         * sweep for as long as they sat there — so a firm with no department heads recorded would
         * stop being told about the tickets it COULD act on. Left out, they are picked up the
         * moment somebody records a head.
         *
         * Counting them first is the other half. Silently skipping them would be a firm whose
         * escalations reach nobody and whose job history says the work was done, which is the one
         * outcome worse than the starvation.
         */
        var unreachable = 0;

        if (heads.Count == 0)
        {
            unreachable = await owed.CountAsync(one => one.AssigneeId == null, cancellationToken);

            if (unreachable > 0)
            {
                logger.LogWarning(
                    "{Count} missed promises are on tickets nobody has picked up, and no "
                    + "department has a head recorded — so there is nobody to tell. Appointing a "
                    + "head, or assigning the tickets, is what makes them visible.",
                    unreachable);
            }

            owed = owed.Where(one => one.AssigneeId != null);
        }

        /*
         * Read as a projection and not as aggregates. A ticket owns its thread, so EF includes
         * ticket_messages in any read of the entity — and a message runs to ten thousand
         * characters, so loading fifty threads to look at two dates each would pull megabytes this
         * job never reads. The handful that are actually escalated are loaded whole below, because
         * that is where the aggregate's own rule has to run.
         */
        var late = await owed
            .OrderBy(one => one.AnswerOwedBy)
            .Take(Batch)
            .Select(one => new
            {
                one.Id,
                one.Number,
                one.Subject,
                one.AssigneeId,
                Owed = one.AnswerOwedBy!.Value,
            })
            .ToListAsync(cancellationToken);

        if (late.Count == 0)
        {
            return unreachable == 0
                ? "Every promise still standing is being kept."
                : $"{unreachable} missed promises have nobody to tell about them.";
        }

        var told = 0;

        foreach (var row in late)
        {
            var recipients = await WhoToTellAsync(row.AssigneeId, heads, cancellationToken);

            /*
             * Loaded whole only now, and only for the ones being escalated. Escalated() is the
             * aggregate's own refusal of a second escalation and it has to run on the aggregate;
             * what this avoids is paying for every thread in the batch to find the few that need
             * it, which on a busy desk is the difference between kilobytes and megabytes.
             */
            if (await database.Tickets
                    .FirstOrDefaultAsync(one => one.Id == row.Id, cancellationToken)
                is not { } ticket)
            {
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

            var howLate = Length(now - row.Owed);

            foreach (var person in recipients)
            {
                await notices.TellAsync(
                    person,
                    NoticeKind.PromiseMissed,
                    $"{Reference(row.Number)} has had no answer and is {howLate} past what we "
                    + $"promised: {row.Subject}",
                    $"/support/{row.Number}",
                    cancellationToken);
            }

            told++;
        }

        var also = unreachable == 0
            ? string.Empty
            : $" {unreachable} more have nobody to tell about them.";

        return (told == 1
            ? "One missed promise was escalated."
            : $"{told} missed promises were escalated.") + also;
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
    ///
    /// It cannot answer nothing, and the caller relies on that. An assigned ticket always yields
    /// at least the assignee; an unassigned one only reaches here when there is at least one
    /// department head, because the query above excludes them otherwise. There used to be a guard
    /// for the empty case, and it was a branch that could not run pretending the caller had a
    /// decision to make.
    /// </remarks>
    private async Task<List<Guid>> WhoToTellAsync(
        Guid? assigneeId, List<Guid> heads, CancellationToken cancellationToken)
    {
        if (assigneeId is not { } assignee)
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
    /// <summary>
    /// What a client quotes back, built here from the number.
    /// </summary>
    /// <remarks>
    /// The aggregate has a Reference property and it is <c>builder.Ignore</c>'d, so projecting it
    /// in a query is refused at run time with "translation of member 'Reference' failed". Written
    /// out here rather than by loading the ticket, because loading the ticket to read one string
    /// is what the projection above exists to avoid.
    /// </remarks>
    private static string Reference(int number) => $"S{number}";

    private static string Length(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "moments";
        }

        if (span < TimeSpan.FromHours(1))
        {
            var minutes = (int)span.TotalMinutes;

            return minutes == 1 ? "1 min" : $"{minutes} min";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;

            return minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
        }

        var days = (int)span.TotalDays;

        /*
         * Singular for one, which five other copies of this helper got wrong for months — every one
         * of them said "1 days". They are one copy in Words.HowLong now; this one stays here
         * because Infrastructure cannot reach the web project, and it is the same rule.
         */
        return days == 1 ? "1 day" : $"{days} days";
    }
}
