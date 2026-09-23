using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
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
public sealed class BusinessQueries(AppDbContext database, IClock clock)
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

    // --- the pipeline ------------------------------------------------------

    /// <summary>
    /// What is in the pipeline, quietest first.
    /// </summary>
    /// <remarks>
    /// Ordered by silence and not by value. A list ordered by value shows what somebody
    /// hopes for; a list ordered by silence shows what they have stopped doing, and only one
    /// of those changes what anybody does on the afternoon they read it.
    ///
    /// Won and lost are excluded unless asked for. They are answers, and a pipeline that
    /// carries every answer ever given is a list nobody scrolls to the bottom of.
    /// </remarks>
    public async Task<List<OpportunityRow>> PipelineAsync(
        Stage? stage = null,
        bool includeClosed = false,
        CancellationToken cancellationToken = default)
    {
        var query = database.Opportunities.AsNoTracking();

        if (stage is { } only)
        {
            query = query.Where(opportunity => opportunity.Stage == only);
        }
        else if (!includeClosed)
        {
            query = query.Where(opportunity =>
                opportunity.Stage != Stage.Won && opportunity.Stage != Stage.Lost);
        }

        var rows = await query
            .OrderBy(opportunity => opportunity.MovedAt)
            .Select(opportunity => new
            {
                opportunity.Id,
                opportunity.Title,
                opportunity.About,
                opportunity.Stage,
                opportunity.ClientId,
                opportunity.OwnerId,
                opportunity.ValueMinorUnits,
                opportunity.ValueCurrency,
                opportunity.ExpectedOn,
                opportunity.MovedAt,
                opportunity.Outcome,
                Activities = opportunity.Activities.Count,
            })
            .ToListAsync(cancellationToken);

        var clientIds = rows.Where(row => row.ClientId != null)
            .Select(row => row.ClientId!.Value)
            .Distinct()
            .ToList();

        var clients = await database.Clients
            .AsNoTracking()
            .Where(client => clientIds.Contains(client.Id))
            .ToDictionaryAsync(client => client.Id, client => client.Name, cancellationToken);

        var ownerIds = rows.Where(row => row.OwnerId != null)
            .Select(row => row.OwnerId!.Value)
            .Distinct()
            .ToList();

        var owners = await database.Employees
            .AsNoTracking()
            .Where(employee => ownerIds.Contains(employee.Id))
            .ToDictionaryAsync(
                employee => employee.Id, employee => employee.FullName, cancellationToken);

        var now = clock.Now;

        return rows.Select(row => new OpportunityRow(
            row.Id,
            row.Title,
            row.About,
            row.Stage,
            row.ClientId,
            row.ClientId is { } clientId && clients.TryGetValue(clientId, out var name)
                ? name
                : null,
            row.OwnerId is { } ownerId && owners.TryGetValue(ownerId, out var owner)
                ? owner
                : null,
            row.ValueMinorUnits is { } minor && row.ValueCurrency is { } currency
                ? Money.Of(minor, currency)
                : null,
            row.ExpectedOn,
            (int)(now - row.MovedAt).TotalDays,
            row.Outcome,
            row.Activities))
            .ToList();
    }

    /// <summary>What has happened to one opportunity, most recent first.</summary>
    public async Task<List<Activity>> ActivitiesAsync(
        Guid opportunityId, CancellationToken cancellationToken = default)
    {
        var opportunity = await database.Opportunities
            .AsNoTracking()
            .Include(one => one.Activities)
            .FirstOrDefaultAsync(one => one.Id == opportunityId, cancellationToken);

        return opportunity is null
            ? []
            : [.. opportunity.Activities.OrderByDescending(activity => activity.At)];
    }

    /// <summary>
    /// The people at a client, the one to call first at the top.
    /// </summary>
    /// <remarks>
    /// Leavers are kept at the bottom rather than hidden, because "the person we dealt with
    /// left in March" is the answer to a question somebody is actually asking when they open
    /// this — and a screen that hid them would send them looking through the audit trail.
    /// </remarks>
    public async Task<List<ContactRow>> ContactsAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        await database.Contacts
            .AsNoTracking()
            .Where(contact => contact.ClientId == clientId)
            .OrderBy(contact => contact.GoneAt != null)
            .ThenByDescending(contact => contact.IsMain)
            .ThenBy(contact => contact.Name)
            .Select(contact => new ContactRow(
                contact.Id,
                contact.ClientId,
                contact.Name,
                contact.JobTitle,
                contact.Email,
                contact.Phone,
                contact.IsMain,
                contact.GoneAt == null))
            .ToListAsync(cancellationToken);

    // --- contracts ----------------------------------------------------------

    public async Task<List<ContractRow>> ContractsAsync(
        Guid? clientId = null,
        ContractState? state = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Contracts.AsNoTracking();

        if (clientId is { } client)
        {
            query = query.Where(contract => contract.ClientId == client);
        }

        if (state is { } only)
        {
            query = query.Where(contract => contract.State == only);
        }

        /*
         * Newest first, by identifier rather than by start date. The dates are
         * nullable while a contract is still a draft, and the two providers
         * disagree about where nulls go in a descending sort — PostgreSQL puts
         * them first, SQLite last — so ordering by the date would put drafts at
         * opposite ends of the list in the tests and in production. A GUIDv7
         * sorts by the moment it was created, which is what "newest" means here
         * anyway.
         */
        var contracts = await query
            .OrderByDescending(contract => contract.Id)
            .Select(contract => new
            {
                contract.Id,
                contract.ClientId,
                contract.Reference,
                contract.Title,
                contract.State,
                contract.StartsOn,
                contract.EndsOn,

                // The two columns rather than Value: Money is built by the
                // aggregate from these, and EF has no way to call that.
                contract.MinorUnits,
                contract.Currency,

                contract.Outcome,
            })
            .ToListAsync(cancellationToken);

        var clients = await database.Clients
            .AsNoTracking()
            .ToDictionaryAsync(one => one.Id, one => one.Name, cancellationToken);

        return contracts.Select(contract => new ContractRow(
            contract.Id,
            contract.ClientId,
            clients.GetValueOrDefault(contract.ClientId) ?? "A client since removed",
            contract.Reference,
            contract.Title,
            contract.State,
            contract.StartsOn,
            contract.EndsOn,
            contract.MinorUnits is { } units ? Money.Of(units, contract.Currency) : null,
            contract.Outcome)).ToList();
    }

    /// <summary>One contract, for the contract page.</summary>
    public Task<Contract?> ContractAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Contracts
            .AsNoTracking()
            .FirstOrDefaultAsync(contract => contract.Id == id, cancellationToken);

    // --- time ---------------------------------------------------------------

    /// <param name="take">
    /// How many at most, or null for all of them. The approval queue passes one; a
    /// person's own timesheet for one week does not need to.
    /// </param>
    public async Task<List<TimeRow>> TimeAsync(
        Guid? employeeId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        bool awaitingApprovalOnly = false,
        Guid? projectId = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.TimeEntries.AsNoTracking();

        if (employeeId is { } person)
        {
            query = query.Where(entry => entry.EmployeeId == person);
        }

        if (projectId is { } project)
        {
            query = query.Where(entry => entry.ProjectId == project);
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

        /*
         * Newest day first: a timesheet is read to check what was just logged, not to
         * browse the year.
         *
         * Except the approval queue, which is read oldest first, and that is not a
         * preference. A queue is worked until it is empty and the oldest entry is the one
         * somebody is waiting on — and once this is capped, the order decides which
         * entries the cap hides. Newest-first with a cap would have hidden the oldest
         * two thousand: exactly the ones that needed approving.
         */
        var ordered = awaitingApprovalOnly
            ? query.OrderBy(entry => entry.On).ThenBy(entry => entry.Id)
            : query.OrderByDescending(entry => entry.On).ThenBy(entry => entry.Id);

        var entries = await ordered
            .Take(take ?? int.MaxValue)
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

                // Selected rather than recomputed. This is the column the
                // aggregate agreed when the request was made, against the
                // holiday calendar as it stood.
                leave.Days,
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
            leave.Days,
            leave.Reason,
            leave.Status,
            leave.DecidedAt,
            leave.Outcome)).ToList();
    }

    // --- public holidays ----------------------------------------------------

    /// <summary>
    /// The calendar for one year, earliest first.
    /// </summary>
    /// <remarks>
    /// By year because that is how somebody checks it. The question the screen
    /// exists to answer is "is next year's calendar in yet, and is it right",
    /// which is asked one year at a time against a printed gazette notice — and a
    /// single list of every holiday the firm has ever recorded answers it
    /// considerably less well.
    /// </remarks>
    public async Task<List<HolidayRow>> HolidaysAsync(
        int year, CancellationToken cancellationToken = default) =>
        await database.Holidays
            .AsNoTracking()
            .Where(holiday => holiday.On.Year == year)
            .OrderBy(holiday => holiday.On)
            .Select(holiday => new HolidayRow(holiday.Id, holiday.On, holiday.Name))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The years the calendar has anything in, newest first.
    /// </summary>
    /// <remarks>
    /// So the year picker offers the years that exist rather than a fixed range
    /// somebody has to keep widening. Distinct over a table of a dozen rows a
    /// year costs nothing.
    /// </remarks>
    public async Task<List<int>> HolidayYearsAsync(CancellationToken cancellationToken = default) =>
        await database.Holidays
            .AsNoTracking()
            .Select(holiday => holiday.On.Year)
            .Distinct()
            .OrderByDescending(year => year)
            .ToListAsync(cancellationToken);

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

    /// <summary>How many invoices match, for a screen or an endpoint that pages them.</summary>
    public Task<int> CountInvoicesAsync(
        Guid? clientId = null,
        InvoiceStatus? status = null,
        CancellationToken cancellationToken = default) =>
        NarrowInvoices(database.Invoices.AsNoTracking(), clientId, status)
            .CountAsync(cancellationToken);

    /// <param name="take">
    /// How many at most, or null for all of them.
    /// </param>
    /// <remarks>
    /// A parameter rather than a limit applied here, because two of the four callers are
    /// asking about one client and want the twenty rows there are, while the list screen and
    /// the public API are asking about the firm and would otherwise get forty thousand.
    ///
    /// This was measured rather than guessed. The scale check found this method returning
    /// every invoice in 1.8 seconds, and the API endpoint above it applying its page
    /// <em>after</em> the rows had already been read — so the cap the API documents as
    /// protecting the firm was protecting the response and nothing else.
    /// </remarks>
    public async Task<List<InvoiceRow>> InvoicesAsync(
        Guid? clientId = null,
        InvoiceStatus? status = null,
        int skip = 0,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var query = NarrowInvoices(database.Invoices.AsNoTracking(), clientId, status);

        // The totals below are summed by the aggregate from these, so an invoice
        // loaded without them reports zero — not an error anywhere, just a wrong
        // number on a screen about money.
        var invoices = await query
            .OrderByDescending(invoice => invoice.Number)
            .Skip(skip)
            .Take(take ?? int.MaxValue)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .ToListAsync(cancellationToken);

        /*
         * Only the clients these invoices are for. It used to be every client in the
         * system on every call, which is the pattern the rest of this file uses and is
         * right when the list is the whole table anyway — but a screen showing fifty
         * invoices has no business reading two thousand clients to label them.
         */
        var wanted = invoices.Select(invoice => invoice.ClientId).Distinct().ToList();

        var clients = await database.Clients
            .AsNoTracking()
            .Where(one => wanted.Contains(one.Id))
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

    /// <summary>
    /// What is owed across the firm, and how much of it is late.
    /// </summary>
    /// <remarks>
    /// Its own query so the invoices screen can show a page of invoices and still say a
    /// true total. It used to add up whichever invoices the screen happened to be holding,
    /// which was all of them — so the moment the list was paged the header would have
    /// started quietly reporting the outstanding balance of the first fifty.
    ///
    /// Only the unsettled ones are read, and that is the whole of why this is affordable.
    /// A firm's unpaid pile is small if the firm is well; if it is not small, a slow page
    /// is not the problem being solved.
    ///
    /// Summed in memory for the same reason as OwedByClientAsync: a total and what is
    /// outstanding are computed by the aggregate, not stored, and a second definition of
    /// "what is owed" written in SQL is a number that will one day disagree with the one
    /// on the invoice.
    /// </remarks>
    public async Task<(Money? Owed, int Overdue)> OutstandingAsync(
        DateOnly today, CancellationToken cancellationToken = default)
    {
        var unsettled = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .Where(invoice => invoice.Status == InvoiceStatus.Sent
                || invoice.Status == InvoiceStatus.PartlyPaid)
            .ToListAsync(cancellationToken);

        if (unsettled.Count == 0)
        {
            return (null, 0);
        }

        var owed = unsettled.Aggregate(
            Money.Zero(unsettled[0].Outstanding.Currency),
            (running, invoice) => running + invoice.Outstanding);

        /*
         * The same rule the row record states, written once here against the aggregate.
         * Being overdue is what is true of an invoice at the moment somebody looks, not
         * something that happens to it — which is why there is no Overdue status and no
         * nightly job that would be wrong between midnight and whenever it ran.
         */
        return (owed, unsettled.Count(invoice =>
            invoice.DueOn < today
            && invoice.Status is InvoiceStatus.Sent or InvoiceStatus.PartlyPaid));
    }

    /// <summary>
    /// The filtering, in one place, because a count and a page have to agree.
    /// </summary>
    private static IQueryable<Invoice> NarrowInvoices(
        IQueryable<Invoice> query, Guid? clientId, InvoiceStatus? status)
    {
        if (clientId is { } client)
        {
            query = query.Where(invoice => invoice.ClientId == client);
        }

        if (status is { } only)
        {
            query = query.Where(invoice => invoice.Status == only);
        }

        return query;
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

/// <summary>
/// One opportunity as the pipeline shows it.
/// </summary>
/// <remarks>
/// Carries <c>Quiet</c> — days since it last moved — rather than the date, because the
/// question the screen exists to answer is which of these nobody has touched, and a column
/// of dates makes a reader do that subtraction thirty times.
/// </remarks>
public sealed record OpportunityRow(
    Guid Id,
    string Title,
    string About,
    Stage Stage,
    Guid? ClientId,
    string? ClientName,
    string? Owner,
    Money? Value,
    DateOnly? ExpectedOn,
    int Quiet,
    string? Outcome,
    int Activities);

public sealed record ContactRow(
    Guid Id,
    Guid ClientId,
    string Name,
    string? JobTitle,
    string? Email,
    string? Phone,
    bool IsMain,
    bool IsHere);

public sealed record ContractRow(
    Guid Id,
    Guid ClientId,
    string ClientName,
    string Reference,
    string Title,
    ContractState State,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    Money? Value,
    string? Outcome)
{
    /// <summary>
    /// Active, and past the date it was agreed to run to.
    /// </summary>
    /// <remarks>
    /// The same two lines the aggregate computes, repeated here rather than
    /// loaded. A list of thirty contracts would otherwise have to be materialised
    /// as thirty aggregates to display one word each.
    /// <see cref="Contract.HasExpiredOn"/> remains the definition, and the domain
    /// test for it is what keeps this honest — which is the same trade
    /// <see cref="LeaveRow.Days"/> makes.
    ///
    /// There is no Expired state to read instead, deliberately: being expired is
    /// what is true of a contract at the moment somebody looks, not something
    /// that happened to it, exactly as with an overdue invoice below.
    /// </remarks>
    public bool HasExpiredOn(DateOnly today) =>
        State == ContractState.Active && EndsOn is { } ends && ends < today;

    /// <inheritdoc cref="Contract.CoversOn"/>
    public bool CoversOn(DateOnly day) =>
        State == ContractState.Active
        && StartsOn is { } starts
        && EndsOn is { } ends
        && starts <= day
        && day <= ends;
}

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

public sealed record HolidayRow(Guid Id, DateOnly On, string Name);

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
