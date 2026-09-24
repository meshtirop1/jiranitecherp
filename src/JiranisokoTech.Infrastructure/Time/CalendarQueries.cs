using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Authorization;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Time;

/// <summary>What sort of dated thing an entry is, which decides how it reads and where it links.</summary>
public enum DatedKind
{
    Holiday = 1,
    Occasion = 2,

    /// <summary>The reader's own leave.</summary>
    MyLeave = 3,

    /// <summary>Somebody else's leave, named and nothing more.</summary>
    Absence = 4,

    Sprint = 5,
    ReviewCycle = 6,
    ContractEnds = 7,
    AgreementEnds = 8,
    WorkDue = 9,
    GoalDue = 10,
    Interview = 11,
}

/// <summary>
/// One dated thing, as the calendar shows it.
/// </summary>
/// <remarks>
/// A date, the minimum noun that makes it findable, and a link to the page that already owns it.
/// Nothing more, and that restraint is what makes twenty-odd sources safe with one argument
/// instead of twenty-odd: the <i>existence</i> of an entry is one leak and its <i>content</i> is
/// a second, and they need separate answers. Existence is decided by the source's permission and
/// reach; content is decided here, by carrying almost none.
///
/// Concretely: somebody else's leave reads "Amina Wekesa — away". It never carries the kind of
/// leave, because compassionate and sick are facts about a person's life, and never the reason.
/// Whoever may see more opens the page behind the link, which guards itself.
/// </remarks>
public sealed record Dated(DateOnly On, DatedKind Kind, string Title, string? Detail, string? Href);

/// <summary>
/// The calendar, assembled from the dates the firm already keeps.
/// </summary>
/// <remarks>
/// Section 33. <b>A read model computed per request, not a projection table.</b> A
/// <c>calendar_entries</c> table would have to be kept current on two axes, and the second one
/// kills it: the date axis is tractable, because every source could raise an event when a date
/// moved — but the <i>audience</i> axis is not, because who may see a project's due date changes
/// when the project's lead changes, and who may see somebody's leave changes when they move
/// department. A projection would have to be rebuilt on events that have nothing to do with
/// dates, and the day one was missed the calendar would show somebody something they may not see
/// with nothing to indicate it.
///
/// <b>One query per source, never one per day and never one per row.</b> The performance note
/// already settled what the real fault looks like here: the failure that kills an EF application
/// at volume is not a slow query, it is a fast one issued four hundred times.
///
/// <b>A source the reader may not see is not queried at all</b>, rather than queried and
/// filtered. The same discipline the search box follows, and for the same reason: a filter is
/// one <c>if</c> away from being forgotten, and a query never issued cannot leak.
/// </remarks>
public sealed class CalendarQueries(AppDbContext database, Reaches reaches)
{
    /// <summary>
    /// At most this many of any one kind in a window.
    /// </summary>
    /// <remarks>
    /// A cap rather than paging, because the window is already the limit somebody chose. What
    /// this guards is the pathological month — a bulk import giving four hundred work items the
    /// same due date — where the honest answer is that the calendar is the wrong screen for that
    /// question.
    /// </remarks>
    private const int Most = 200;

    public async Task<List<Dated>> BetweenAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<Dated>();

        // Everybody sees these two. A holiday is the firm's working calendar and an occasion is
        // the firm telling everybody something is happening; neither is about a person.
        entries.AddRange(await HolidaysAsync(from, to, cancellationToken));
        entries.AddRange(await OccasionsAsync(from, to, cancellationToken));

        if (employeeId is { } me)
        {
            entries.AddRange(await MyLeaveAsync(me, from, to, cancellationToken));
            entries.AddRange(await MyWorkAsync(me, from, to, cancellationToken));
            entries.AddRange(await MyGoalsAsync(me, from, to, cancellationToken));
        }

        /*
         * Other people's absence, only where the reader holds leave.view_all and the person sits
         * in a department they reach. Not queried at all otherwise — see the class remarks.
         */
        var absence = await reaches.AbsenceAsync(permissions, employeeId, cancellationToken);

        if (!absence.IsNothing)
        {
            entries.AddRange(
                await AbsenceAsync(absence, employeeId, from, to, cancellationToken));
        }

        if (permissions.Contains(Permissions.TasksViewAll))
        {
            entries.AddRange(await SprintsAsync(from, to, cancellationToken));
        }

        if (permissions.Contains(Permissions.GoalsManage)
            || permissions.Contains(Permissions.GoalsViewAll))
        {
            entries.AddRange(await CyclesAsync(from, to, cancellationToken));
        }

        if (permissions.Contains(Permissions.ContractsView))
        {
            entries.AddRange(await ContractsAsync(from, to, cancellationToken));
            entries.AddRange(await AgreementsAsync(from, to, cancellationToken));
        }

        if (permissions.Contains(Permissions.InterviewsView))
        {
            entries.AddRange(await InterviewsAsync(from, to, cancellationToken));
        }

        return [.. entries.OrderBy(one => one.On).ThenBy(one => one.Kind).ThenBy(one => one.Title)];
    }

    private async Task<List<Dated>> HolidaysAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Holidays
            .AsNoTracking()
            .Where(one => one.On >= from && one.On <= to)
            .OrderBy(one => one.On)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.On, DatedKind.Holiday, one.Name, "Public holiday", "/holidays"))];

    /// <remarks>
    /// An occasion running over several days appears on its first day with the range said in
    /// words, rather than repeated on each day it covers. Repeating it is what turns a week-long
    /// office closure into five rows that push everything else off the screen.
    /// </remarks>
    private async Task<List<Dated>> OccasionsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Occasions
            .AsNoTracking()
            .Where(one => one.On <= to && (one.Until ?? one.On) >= from)
            .OrderBy(one => one.On)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.On < from ? from : one.On,
                DatedKind.Occasion,
                one.Name,
                one.Until is { } ends && ends != one.On
                    ? $"until {ends:d MMM}"
                    : one.Note,
                "/settings/occasions"))];

    private async Task<List<Dated>> MyLeaveAsync(
        Guid me, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Leave
            .AsNoTracking()
            .Where(one => one.EmployeeId == me)
            .Where(one => one.Status != LeaveStatus.Refused
                && one.Status != LeaveStatus.Cancelled)
            .Where(one => one.From <= to && one.To >= from)
            .OrderBy(one => one.From)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.From < from ? from : one.From,
                DatedKind.MyLeave,
                "You are away",
                one.To != one.From ? $"until {one.To:d MMM}" : null,
                "/leave"))];

    /// <remarks>
    /// The name and nothing else. Not the kind of leave — compassionate and sick are facts about
    /// a person's life — and not the reason. Whoever may see more opens the decisions page, which
    /// guards itself.
    /// </remarks>
    private async Task<List<Dated>> AbsenceAsync(
        Reach within,
        Guid? me,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var query = database.Leave
            .AsNoTracking()
            .Where(one => one.Status != LeaveStatus.Refused
                && one.Status != LeaveStatus.Cancelled)
            .Where(one => one.From <= to && one.To >= from)
            .Where(one => one.EmployeeId != me);

        var rows = await query
            .Join(
                database.Employees.AsNoTracking(),
                request => request.EmployeeId,
                person => person.Id,
                (request, person) => new
                {
                    request.From,
                    request.To,
                    person.FullName,
                    person.DepartmentId,
                })
            .Where(row => within.IsEverything
                || (row.DepartmentId != null
                    && within.Only.Contains(row.DepartmentId.Value)))
            .OrderBy(row => row.From)
            .Take(Most)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new Dated(
            row.From < from ? from : row.From,
            DatedKind.Absence,
            $"{row.FullName} — away",
            row.To != row.From ? $"until {row.To:d MMM}" : null,
            "/leave/decisions"))];
    }

    private async Task<List<Dated>> MyWorkAsync(
        Guid me, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.WorkItems
            .AsNoTracking()
            .Where(one => one.AssigneeId == me && one.DueOn != null)
            .Where(one => one.DueOn >= from && one.DueOn <= to)
            .Where(one => one.Status != WorkItemStatus.Done
                && one.Status != WorkItemStatus.Cancelled
                && one.Status != WorkItemStatus.Deployed)
            .OrderBy(one => one.DueOn)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.DueOn!.Value,
                DatedKind.WorkDue,
                one.Title,
                $"#{one.Number} due",
                $"/work/{one.Id}"))];

    private async Task<List<Dated>> MyGoalsAsync(
        Guid me, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Goals
            .AsNoTracking()
            .Where(one => one.ForEmployeeId == me && one.Outcome == null)
            .Where(one => one.To >= from && one.To <= to)
            .OrderBy(one => one.To)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.To, DatedKind.GoalDue, one.Title, "Goal", $"/performance/goals/{one.Id}"))];

    private async Task<List<Dated>> SprintsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Sprints
            .AsNoTracking()
            .Where(one => one.Ends >= from && one.Ends <= to)
            .OrderBy(one => one.Ends)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.Ends, DatedKind.Sprint, $"{one.Name} ends", one.Goal, "/planning"))];

    private async Task<List<Dated>> CyclesAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.ReviewCycles
            .AsNoTracking()
            .Where(one => !one.IsClosed && one.To >= from && one.To <= to)
            .OrderBy(one => one.To)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.To,
                DatedKind.ReviewCycle,
                $"{one.Name} closes",
                "Review cycle",
                $"/performance/cycles/{one.Id}"))];

    private async Task<List<Dated>> ContractsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Contracts
            .AsNoTracking()
            .Where(one => one.EndsOn != null && one.EndsOn >= from && one.EndsOn <= to)
            .OrderBy(one => one.EndsOn)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.EndsOn!.Value,
                DatedKind.ContractEnds,
                $"{one.Reference} ends",
                one.Title,
                $"/contracts/{one.Id}"))];

    private async Task<List<Dated>> AgreementsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        [.. (await database.Agreements
            .AsNoTracking()
            .Where(one => one.EndsOn != null && one.EndsOn >= from && one.EndsOn <= to)
            .Where(one => one.State == AgreementState.Draft
                || one.State == AgreementState.Signed)
            .OrderBy(one => one.EndsOn)
            .Take(Most)
            .ToListAsync(cancellationToken))
            .Select(one => new Dated(
                one.EndsOn!.Value,
                DatedKind.AgreementEnds,
                $"{one.Reference} runs out",
                one.Party,
                $"/agreements/{one.Id}"))];

    private async Task<List<Dated>> InterviewsAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        var rows = await database.Interviews
            .AsNoTracking()
            .Where(one => one.ScheduledFor >= start && one.ScheduledFor <= end)
            .OrderBy(one => one.ScheduledFor)
            .Take(Most)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(one => new Dated(
            DateOnly.FromDateTime(one.ScheduledFor.UtcDateTime),
            DatedKind.Interview,
            "Interview",
            one.ScheduledFor.ToLocalTime().ToString("HH:mm"),
            $"/interviews/{one.Id}"))];
    }
}
