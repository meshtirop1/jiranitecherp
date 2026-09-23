using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Infrastructure.Business;

/// <summary>The reads the six business screens do, kept out of the screens.</summary>
/// <remarks>
/// Every method here is <c>AsNoTracking</c> and returns a row record rather than
/// an entity. A screen holding a tracked aggregate is one save away from
/// persisting whatever a binding happened to touch, and a row record cannot be
/// accidentally mutated into a database write.
///
/// Names are resolved by a second query into a dictionary rather than by joining
/// in the projection. Two small queries read plainly and cost one extra
/// round-trip; the join costs a reader five minutes working out what is being
/// selected.
/// </remarks>
public sealed class BusinessQueries(AppDbContext database)
{
    // --- clients -----------------------------------------------------------

    public async Task<List<ClientRow>> ClientsAsync(
        ClientStatus? status = null, CancellationToken cancellationToken = default)
    {
        var query = database.Clients.AsNoTracking();

        if (status is { } only)
        {
            query = query.Where(client => client.Status == only);
        }

        var clients = await query
            .OrderBy(client => client.Name)
            .Select(client => new
            {
                client.Id,
                client.Name,
                client.Code,
                client.Status,
                client.ContactName,
                client.ContactEmail,
                client.PaymentTermDays,
            })
            .ToListAsync(cancellationToken);

        var projects = await database.Projects
            .AsNoTracking()
            .Where(project => project.ClientId != null)
            .GroupBy(project => project.ClientId!.Value)
            .Select(group => new { ClientId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ClientId, row => row.Count, cancellationToken);

        var owed = await OwedByClientAsync(cancellationToken);

        return clients.Select(client => new ClientRow(
            client.Id,
            client.Name,
            client.Code,
            client.Status,
            client.ContactName,
            client.ContactEmail,
            client.PaymentTermDays,
            projects.GetValueOrDefault(client.Id),
            owed.GetValueOrDefault(client.Id))).ToList();
    }

    /// <summary>
    /// What each client still owes, across every invoice sent to them.
    /// </summary>
    /// <remarks>
    /// Summed in memory on purpose. An invoice total is the sum of its lines and
    /// what is outstanding is that less its payments, and neither figure is a
    /// column — they are computed by the aggregate, which is where the rule about
    /// them lives. Pushing the arithmetic into SQL would put a second, separate
    /// definition of "what is owed" in the database, and the day the two
    /// disagree is the day somebody chases a client for the wrong amount.
    /// </remarks>
    private async Task<Dictionary<Guid, Money>> OwedByClientAsync(
        CancellationToken cancellationToken)
    {
        var unpaid = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .Where(invoice => invoice.Status == InvoiceStatus.Sent
                || invoice.Status == InvoiceStatus.PartlyPaid)
            .ToListAsync(cancellationToken);

        return unpaid
            .GroupBy(invoice => invoice.ClientId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    Money.Zero(group.First().Currency),
                    (running, invoice) => running + invoice.Outstanding));
    }

    // --- time ---------------------------------------------------------------

    public async Task<List<TimeRow>> TimeAsync(
        Guid? employeeId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool awaitingApprovalOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = database.TimeEntries.AsNoTracking();

        if (employeeId is { } person)
        {
            query = query.Where(entry => entry.EmployeeId == person);
        }

        if (from is { } start)
        {
            query = query.Where(entry => entry.On >= start);
        }

        if (to is { } end)
        {
            query = query.Where(entry => entry.On <= end);
        }

        if (awaitingApprovalOnly)
        {
            query = query.Where(entry => entry.ApprovedAt == null);
        }

        var entries = await query
            // Newest day first: a timesheet is read to check what was just
            // logged, not to browse the year.
            .OrderByDescending(entry => entry.On)
            .ThenBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.Id,
                entry.EmployeeId,
                entry.On,
                entry.Minutes,
                entry.Note,
                entry.IsBillable,
                entry.ProjectId,
                entry.ApprovedAt,
                entry.ApprovedById,
                entry.InvoiceId,
            })
            .ToListAsync(cancellationToken);

        var people = await PeopleAsync(cancellationToken);
        var projects = await ProjectNamesAsync(cancellationToken);

        return entries.Select(entry => new TimeRow(
            entry.Id,
            entry.EmployeeId,
            people.GetValueOrDefault(entry.EmployeeId) ?? "Somebody who has left",
            entry.On,
            entry.Minutes,
            entry.Note,
            entry.IsBillable,
            entry.ProjectId is { } project ? projects.GetValueOrDefault(project) : null,
            entry.ApprovedAt is not null,
            entry.ApprovedById is { } approver ? people.GetValueOrDefault(approver) : null,
            entry.InvoiceId is not null)).ToList();
    }

    /// <summary>Minutes logged per day over a span, for the week strip.</summary>
    public async Task<Dictionary<DateOnly, int>> DailyMinutesAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default) =>
        await database.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.EmployeeId == employeeId
                && entry.On >= from
                && entry.On <= to)
            .GroupBy(entry => entry.On)
            .Select(group => new { Day = group.Key, Minutes = group.Sum(entry => entry.Minutes) })
            .ToDictionaryAsync(row => row.Day, row => row.Minutes, cancellationToken);

    // --- leave --------------------------------------------------------------

    public async Task<List<LeaveRow>> LeaveAsync(
        Guid? employeeId = null,
        LeaveStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Leave.AsNoTracking();

        if (employeeId is { } person)
        {
            query = query.Where(leave => leave.EmployeeId == person);
        }

        if (status is { } only)
        {
            query = query.Where(leave => leave.Status == only);
        }

        var requests = await query
            .OrderByDescending(leave => leave.From)
            .Select(leave => new
            {
                leave.Id,
                leave.EmployeeId,
                leave.Kind,
                leave.From,
                leave.To,
                leave.Reason,
                leave.Status,
                leave.DecidedAt,
                leave.Outcome,
            })
            .ToListAsync(cancellationToken);

        var people = await PeopleAsync(cancellationToken);

        return requests.Select(leave => new LeaveRow(
            leave.Id,
            leave.EmployeeId,
            people.GetValueOrDefault(leave.EmployeeId) ?? "Somebody who has left",
            leave.Kind,
            leave.From,
            leave.To,
            WorkingDaysBetween(leave.From, leave.To),
            leave.Reason,
            leave.Status,
            leave.DecidedAt,
            leave.Outcome)).ToList();
    }

    /// <summary>
    /// The same count the aggregate computes, repeated here rather than loaded.
    /// </summary>
    /// <remarks>
    /// A list of thirty requests would otherwise have to be materialised as
    /// thirty aggregates to display one number each. The duplication is small
    /// and the rule is one line; <see cref="LeaveRequest.Days"/> remains the
    /// definition, and the domain test for it is what keeps this honest.
    /// </remarks>
    private static int WorkingDaysBetween(DateOnly from, DateOnly to)
    {
        var days = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days++;
            }
        }

        return days;
    }

    // --- expenses -----------------------------------------------------------

    public async Task<List<ClaimRow>> ClaimsAsync(
        Guid? employeeId = null,
        ClaimStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Expenses.AsNoTracking();

        if (employeeId is { } person)
        {
            query = query.Where(claim => claim.EmployeeId == person);
        }

        if (status is { } only)
        {
            query = query.Where(claim => claim.Status == only);
        }

        var claims = await query
            .OrderByDescending(claim => claim.SpentOn)
            .Select(claim => new
            {
                claim.Id,
                claim.EmployeeId,
                claim.Description,

                // The two columns rather than Amount: Money is built by the
                // aggregate from these, and EF has no way to call that.
                claim.MinorUnits,
                claim.Currency,

                claim.SpentOn,
                claim.Category,
                claim.Status,
                claim.PaidAt,
                claim.Outcome,
                claim.ReceiptFileName,
            })
            .ToListAsync(cancellationToken);

        var people = await PeopleAsync(cancellationToken);

        return claims.Select(claim => new ClaimRow(
            claim.Id,
            claim.EmployeeId,
            people.GetValueOrDefault(claim.EmployeeId) ?? "Somebody who has left",
            claim.Description,
            Money.Of(claim.MinorUnits, claim.Currency),
            claim.SpentOn,
            claim.Category,
            claim.Status,
            claim.PaidAt,
            claim.Outcome,
            claim.ReceiptFileName)).ToList();
    }

    // --- invoices -----------------------------------------------------------

    public async Task<List<InvoiceRow>> InvoicesAsync(
        Guid? clientId = null,
        InvoiceStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Invoices.AsNoTracking();

        if (clientId is { } client)
        {
            query = query.Where(invoice => invoice.ClientId == client);
        }

        if (status is { } only)
        {
            query = query.Where(invoice => invoice.Status == only);
        }

        // The totals below are summed by the aggregate from these, so an invoice
        // loaded without them reports zero — not an error anywhere, just a wrong
        // number on a screen about money.
        var invoices = await query
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .OrderByDescending(invoice => invoice.Number)
            .ToListAsync(cancellationToken);

        var clients = await database.Clients
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.Name, cancellationToken);

        return invoices.Select(invoice => new InvoiceRow(
            invoice.Id,
            invoice.Number,
            invoice.ClientId,
            clients.GetValueOrDefault(invoice.ClientId) ?? "A client since removed",
            invoice.Status,
            invoice.IssuedOn,
            invoice.DueOn,
            invoice.Total,
            invoice.Paid,
            invoice.Outstanding,
            invoice.Lines.Count)).ToList();
    }

    /// <summary>One invoice, with its lines and payments, for the invoice page.</summary>
    public Task<Invoice?> InvoiceAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .FirstOrDefaultAsync(invoice => invoice.Id == id, cancellationToken);

    public Task<Client?> ClientAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(client => client.Id == id, cancellationToken);

    // --- interviews ---------------------------------------------------------

    /// <summary>
    /// Interviews with enough around them to run one.
    /// </summary>
    /// <remarks>
    /// Loaded as aggregates rather than projected, because everything worth
    /// showing — who is on the panel, who has scored, what the panel concluded
    /// — is computed by the interview from its own owned collections. A
    /// projection would have to restate those rules in SQL, and the one that
    /// matters is the rule that a single strong no carries the panel.
    /// </remarks>
    public async Task<List<InterviewRow>> InterviewsAsync(
        bool upcomingOnly = false, CancellationToken cancellationToken = default)
    {
        var query = database.Interviews
            .AsNoTracking()
            .Include(interview => interview.Panel)
            .Include(interview => interview.Scorecards)
            .AsQueryable();

        if (upcomingOnly)
        {
            query = query.Where(interview => interview.Status == InterviewStatus.Scheduled);
        }

        var interviews = await query
            .OrderBy(interview => interview.ScheduledFor)
            .ToListAsync(cancellationToken);

        var applications = await database.Applications
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.CandidateId, cancellationToken);

        var candidates = await database.Candidates
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.FullName, cancellationToken);

        var people = await PeopleAsync(cancellationToken);

        return [.. interviews.Select(interview =>
        {
            var candidate = applications.TryGetValue(interview.ApplicationId, out var candidateId)
                ? candidates.GetValueOrDefault(candidateId)
                : null;

            return new InterviewRow(
                interview.Id,
                interview.ApplicationId,
                candidate ?? "A candidate since removed",
                interview.Kind,
                interview.ScheduledFor,
                interview.Where,
                interview.Status,
                [.. interview.Panel.Select(one =>
                    people.GetValueOrDefault(one.EmployeeId) ?? "Somebody who has left")],
                [.. interview.Panel.Select(one => one.EmployeeId)],
                [.. interview.Scorecards.Select(card => new ScorecardRow(
                    people.GetValueOrDefault(card.InterviewerId) ?? "Somebody who has left",
                    card.InterviewerId,
                    card.Recommendation,
                    card.Notes))],
                interview.IsScored,
                interview.Scorecards.Count > 0 ? interview.Verdict() : null);
        })];
    }

    // --- shared lookups -----------------------------------------------------

    private Task<Dictionary<Guid, string>> PeopleAsync(CancellationToken cancellationToken) =>
        database.Employees
            .AsNoTracking()
            .ToDictionaryAsync(person => person.Id, person => person.FullName, cancellationToken);

    private Task<Dictionary<Guid, string>> ProjectNamesAsync(CancellationToken cancellationToken) =>
        database.Projects
            .AsNoTracking()
            .ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);
}

public sealed record ClientRow(
    Guid Id,
    string Name,
    string Code,
    ClientStatus Status,
    string? ContactName,
    string? ContactEmail,
    int PaymentTermDays,
    int Projects,
    Money? Owed);

public sealed record TimeRow(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly On,
    int Minutes,
    string? Note,
    bool IsBillable,
    string? ProjectName,
    bool IsApproved,
    string? ApprovedBy,
    bool IsInvoiced)
{
    /// <summary>The hours, as somebody would say them.</summary>
    public string Duration => Minutes % 60 == 0
        ? $"{Minutes / 60}h"
        : Minutes < 60 ? $"{Minutes}m" : $"{Minutes / 60}h {Minutes % 60}m";
}

public sealed record LeaveRow(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    LeaveKind Kind,
    DateOnly From,
    DateOnly To,
    int Days,
    string Reason,
    LeaveStatus Status,
    DateTimeOffset? DecidedAt,
    string? Outcome);

public sealed record ClaimRow(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    string Description,
    Money Amount,
    DateOnly SpentOn,
    ExpenseCategory Category,
    ClaimStatus Status,
    DateTimeOffset? PaidAt,
    string? Outcome,
    string? ReceiptFileName);

public sealed record InvoiceRow(
    Guid Id,
    string Number,
    Guid ClientId,
    string ClientName,
    InvoiceStatus Status,
    DateOnly IssuedOn,
    DateOnly DueOn,
    Money Total,
    Money Paid,
    Money Outstanding,
    int Lines)
{
    /// <summary>
    /// Past its due date with money still on it.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored, and there is no Overdue status for the same
    /// reason. Being overdue is not something that happens to an invoice; it is
    /// what is true of it at the moment somebody looks. A status would need a
    /// nightly job to stay true, and would be wrong between midnight and
    /// whenever that job ran.
    /// </remarks>
    public bool IsOverdueOn(DateOnly today) =>
        DueOn < today && Status is InvoiceStatus.Sent or InvoiceStatus.PartlyPaid;
}

/// <summary>One interview, and what the panel has said so far.</summary>
public sealed record InterviewRow(
    Guid Id,
    Guid ApplicationId,
    string CandidateName,
    InterviewKind Kind,
    DateTimeOffset ScheduledFor,
    string? Where,
    InterviewStatus Status,
    IReadOnlyList<string> Panel,
    IReadOnlyList<Guid> PanelIds,
    IReadOnlyList<ScorecardRow> Scorecards,
    bool IsScored,
    Recommendation? Verdict);

public sealed record ScorecardRow(
    string InterviewerName,
    Guid InterviewerId,
    Recommendation Recommendation,
    string Notes);
