using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;

namespace JiranisokoTech.Application.Business;

/// <summary>
/// What the six business modules need from storage.
/// </summary>
/// <remarks>
/// One interface rather than six, because the rules that matter here cross
/// between them — invoicing reads time, leave reads leave, hiring an
/// interviewee reads an application — and six repositories would mean a service
/// holding four of them to answer one question.
/// </remarks>
public interface IBusinessRepository
{
    Task<Client?> FindClientAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ClientCodeTakenAsync(string code, CancellationToken cancellationToken = default);

    Task<int> LiveProjectsForAsync(Guid clientId, CancellationToken cancellationToken = default);

    Task<Contract?> FindContractAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ContractReferenceTakenAsync(
        string reference, CancellationToken cancellationToken = default);

    Task<Invoice?> FindInvoiceAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> InvoiceNumberTakenAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>The highest number issued this year, for the next one.</summary>
    /// <summary>
    /// The highest sequence number issued this year under this prefix.
    /// </summary>
    /// <remarks>
    /// The prefix is a parameter rather than a constant here because it is a
    /// setting the firm can change. Numbers issued under the old one keep it,
    /// so the sequence is per-prefix and a change starts a fresh run rather
    /// than continuing somebody else's.
    /// </remarks>
    Task<int> LastInvoiceSequenceAsync(
        string prefix, int year, CancellationToken cancellationToken = default);

    Task<ExpenseClaim?> FindClaimAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TimeEntry?> FindTimeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Minutes already logged by somebody on a day.</summary>
    Task<int> MinutesOnAsync(
        Guid employeeId, DateOnly on, Guid? except = null, CancellationToken cancellationToken = default);

    /// <summary>Approved, billable, uninvoiced time on a project.</summary>
    Task<List<TimeEntry>> BillableTimeAsync(
        Guid projectId, CancellationToken cancellationToken = default);

    Task<LeaveRequest?> FindLeaveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Live leave for somebody that touches these dates.</summary>
    Task<List<LeaveRequest>> LeaveOverlappingAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        Guid? except = null,
        CancellationToken cancellationToken = default);

    Task<Holiday?> FindHolidayAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> HolidayTakenAsync(DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every public holiday there is, as a set of dates.
    /// </summary>
    /// <remarks>
    /// All of them rather than the ones a particular request spans, which would
    /// be the careful thing to do if the table were large. It is a dozen rows a
    /// year: a firm that has been running for twenty years has two hundred and
    /// forty dates here, and reading them all costs less than getting the
    /// boundaries of a range query right. A range query is also the shape that
    /// invites an off-by-one, and an off-by-one here is a day of somebody's
    /// leave.
    /// </remarks>
    Task<IReadOnlySet<DateOnly>> HolidaysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Live leave requests that contain a date, whoever they belong to.
    /// </summary>
    /// <remarks>
    /// The one query the holiday calendar needs, and it is here rather than in
    /// the read-side queries because these come back as aggregates meant to be
    /// changed — a holiday going onto the calendar has to recount them.
    /// </remarks>
    Task<List<LeaveRequest>> LiveLeaveSpanningAsync(
        DateOnly on, CancellationToken cancellationToken = default);

    Task<Interview?> FindInterviewAsync(Guid id, CancellationToken cancellationToken = default);

    Task<JobApplication?> FindApplicationAsync(
        Guid id, CancellationToken cancellationToken = default);

    void Add(Client client);

    void Add(Contract contract);

    void Add(Invoice invoice);

    void Add(ExpenseClaim claim);

    void Add(TimeEntry entry);

    void Add(LeaveRequest leave);

    void Add(Interview interview);

    void Add(Holiday holiday);

    /// <summary>
    /// Take a day off the calendar.
    /// </summary>
    /// <remarks>
    /// The only genuine delete in this interface, and it is right for this one
    /// thing. Everything else here is a record of something that happened and is
    /// archived rather than removed; a holiday is a statement about a date, and a
    /// wrong statement typed by mistake should leave nothing behind but the audit
    /// entry saying it was there and is not.
    /// </remarks>
    void Remove(Holiday holiday);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Clients, and what stops one being archived.</summary>
public sealed class ClientService(IBusinessRepository business)
{
    public async Task<Client> TakeOnAsync(
        string name,
        string? code = null,
        string? contactName = null,
        string? contactEmail = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Slug.From(code ?? name);

        if (await business.ClientCodeTakenAsync(handle.Value, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another client already uses the code '{handle.Value}'. It goes on invoices, so "
                + "it has to be unique.");
        }

        var client = Client.TakeOn(name, handle.Value, contactName, contactEmail);

        business.Add(client);
        await business.SaveAsync(cancellationToken);

        return client;
    }

    /// <summary>
    /// Change where a client stands.
    /// </summary>
    /// <remarks>
    /// Marking somebody a former client while projects are still running is
    /// refused. It is almost always a mistake, and the one time it is not, the
    /// projects should be closed first — which is the conversation this refusal
    /// starts.
    /// </remarks>
    public async Task MoveToAsync(
        Guid clientId, ClientStatus status, CancellationToken cancellationToken = default)
    {
        var client = await Required(clientId, cancellationToken);

        if (status == ClientStatus.Former)
        {
            var running = await business.LiveProjectsForAsync(clientId, cancellationToken);

            if (running > 0)
            {
                throw new InvalidOperationException(
                    $"{client.Name} still has {running} project(s) running. Close those first.");
            }
        }

        client.MoveTo(status);
        await business.SaveAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        Guid clientId,
        string name,
        string? contactName,
        string? contactEmail,
        string? phone,
        string? billingEmail,
        string? address,
        int paymentTermDays,
        CancellationToken cancellationToken = default)
    {
        var client = await Required(clientId, cancellationToken);

        client.Rename(name);
        client.ContactIs(contactName, contactEmail, phone);
        client.BillTo(billingEmail, address);
        client.PaysWithin(paymentTermDays);

        await business.SaveAsync(cancellationToken);
    }

    private async Task<Client> Required(Guid id, CancellationToken cancellationToken) =>
        await business.FindClientAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no client with that identifier.");
}

/// <summary>Contracts, and what says a client may be billed at all.</summary>
/// <remarks>
/// The rules that live here rather than on the aggregate are the ones that need
/// another row to answer: whether the reference is already somebody else's, and
/// whether the client is still a client. A contract cannot see either from
/// inside itself.
/// </remarks>
public sealed class ContractService(
    IBusinessRepository business, Settings.SettingsService settings, IClock clock)
{
    /// <summary>
    /// Open a contract record. Nothing is agreed yet.
    /// </summary>
    /// <remarks>
    /// The currency comes from the firm's settings rather than from a field on
    /// the form. A contract is in one currency and every figure entered against
    /// it has to be in that one; offering a choice per contract would mean the
    /// day somebody picked the wrong one, the value could never be typed at all.
    /// </remarks>
    public async Task<Contract> DraftAsync(
        Guid clientId,
        string reference,
        string title,
        CancellationToken cancellationToken = default)
    {
        var client = await business.FindClientAsync(clientId, cancellationToken)
            ?? throw new InvalidOperationException("There is no client with that identifier.");

        if (!client.IsCurrent)
        {
            throw new InvalidOperationException(
                $"{client.Name} is a former client. Agreeing new terms with them means taking "
                + "them back on first.");
        }

        var trimmed = (reference ?? string.Empty).Trim();

        if (await business.ContractReferenceTakenAsync(trimmed, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another contract already uses the reference '{trimmed}'. It is what both sides "
                + "quote at each other, so it has to be unique.");
        }

        var firm = await settings.CurrentAsync(cancellationToken);

        var contract = Contract.Draft(clientId, trimmed, title, firm.Currency);

        business.Add(contract);
        await business.SaveAsync(cancellationToken);

        return contract;
    }

    /// <summary>What was agreed: the figure, the span, and what it is called.</summary>
    public async Task AgreeAsync(
        Guid contractId,
        string title,
        Domain.Common.Money value,
        DateOnly startsOn,
        DateOnly endsOn,
        CancellationToken cancellationToken = default)
    {
        var contract = await Required(contractId, cancellationToken);

        /*
         * Value and dates before the title, so that a contract whose terms are
         * frozen is refused with nothing applied. The other order would rename
         * an active contract and then throw over the figure, leaving a change
         * nobody asked for and no record of what was attempted.
         */
        contract.WorthUpTo(value);
        contract.Runs(startsOn, endsOn);
        contract.Retitle(title);

        await business.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Signed.
    /// </summary>
    /// <remarks>
    /// Refused for a former client, which the contract cannot check for itself.
    /// Activating terms with somebody the firm has stopped working for is either
    /// a mistake or the client should be taken back on — and that is the
    /// conversation this refusal starts, the same one invoicing them starts.
    /// </remarks>
    public async Task ActivateAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var contract = await Required(contractId, cancellationToken);

        var client = await business.FindClientAsync(contract.ClientId, cancellationToken);

        if (client is { IsCurrent: false })
        {
            throw new InvalidOperationException(
                $"{client.Name} is a former client, so terms cannot be brought into force with "
                + "them.");
        }

        contract.Activate(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task ExtendAsync(
        Guid contractId, DateOnly endsOn, CancellationToken cancellationToken = default)
    {
        var contract = await Required(contractId, cancellationToken);

        contract.Extend(endsOn, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task TerminateAsync(
        Guid contractId, string reason, CancellationToken cancellationToken = default)
    {
        var contract = await Required(contractId, cancellationToken);

        contract.Terminate(reason, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    private async Task<Contract> Required(Guid id, CancellationToken cancellationToken) =>
        await business.FindContractAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no contract with that identifier.");
}

/// <summary>Timesheets, and the day that will not hold more hours.</summary>
public sealed class TimesheetService(
    IBusinessRepository business, IPeopleRepository people, IClock clock)
{
    /// <summary>Nobody works more than this in a day, and a day that says so is wrong.</summary>
    public const int LongestDayMinutes = 16 * 60;

    public async Task<TimeEntry> LogAsync(
        Guid employeeId,
        DateOnly on,
        int minutes,
        Guid? workItemId = null,
        Guid? projectId = null,
        string? note = null,
        bool billable = true,
        CancellationToken cancellationToken = default)
    {
        if (await people.FindAsync(employeeId, cancellationToken) is not { } person)
        {
            throw new InvalidOperationException("That person is not on the staff list.");
        }

        if (on > clock.Today)
        {
            throw new InvalidOperationException(
                "That is in the future. Log the hours once you have worked them.");
        }

        var already = await business.MinutesOnAsync(employeeId, on, null, cancellationToken);

        if (already + minutes > LongestDayMinutes)
        {
            /*
             * The rule this service exists for. A day is only so long, and a
             * timesheet that says otherwise is either a typo or somebody
             * logging the same work twice — both of which reach a client
             * invoice if nothing stops them here.
             */
            throw new InvalidOperationException(
                $"{person.FullName} already has {already / 60}h on {on:d MMM}. "
                + $"That would take the day past {LongestDayMinutes / 60} hours.");
        }

        var entry = TimeEntry.Log(employeeId, on, minutes, workItemId, projectId, note, billable);

        business.Add(entry);
        await business.SaveAsync(cancellationToken);

        return entry;
    }

    public async Task AmendAsync(
        Guid entryId,
        int minutes,
        string? note,
        bool billable,
        CancellationToken cancellationToken = default)
    {
        var entry = await RequiredTime(entryId, cancellationToken);

        var already = await business.MinutesOnAsync(
            entry.EmployeeId, entry.On, entryId, cancellationToken);

        if (already + minutes > LongestDayMinutes)
        {
            throw new InvalidOperationException(
                $"That would take {entry.On:d MMM} past {LongestDayMinutes / 60} hours.");
        }

        entry.Amend(minutes, note, billable);
        await business.SaveAsync(cancellationToken);
    }

    public async Task ApproveAsync(
        Guid entryId, Guid byEmployeeId, CancellationToken cancellationToken = default)
    {
        var entry = await RequiredTime(entryId, cancellationToken);

        entry.Approve(byEmployeeId, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    private async Task<TimeEntry> RequiredTime(Guid id, CancellationToken cancellationToken) =>
        await business.FindTimeAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no time entry with that identifier.");
}

/// <summary>Leave, and the fortnight somebody already booked.</summary>
public sealed class LeaveService(IBusinessRepository business, IClock clock)
{
    public async Task<LeaveRequest> AskForAsync(
        Guid employeeId,
        LeaveKind kind,
        DateOnly from,
        DateOnly to,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var clashes = await business.LeaveOverlappingAsync(
            employeeId, from, to, null, cancellationToken);

        if (clashes.Count > 0)
        {
            /*
             * Two overlapping requests are how somebody comes to be marked away
             * twice for the same week — and, in a system that counts days,
             * charged twice for it.
             */
            var clash = clashes[0];

            throw new InvalidOperationException(
                $"That overlaps leave already asked for from {clash.From:d MMM} to "
                + $"{clash.To:d MMM}. Cancel that first if the dates have changed.");
        }

        /*
         * The calendar is read here and handed to the aggregate, the same way
         * the firm's settings hand it whether any invoice exists. Which days are
         * working days is a rule and belongs in the domain; which days the
         * government has declared holidays is a fact in a table, and the domain
         * has no business asking for it.
         */
        var holidays = await business.HolidaysAsync(cancellationToken);

        var leave = LeaveRequest.For(employeeId, kind, from, to, reason, clock.Today, holidays);

        business.Add(leave);
        await business.SaveAsync(cancellationToken);

        return leave;
    }

    public async Task SubmitAsync(Guid leaveId, CancellationToken cancellationToken = default)
    {
        var leave = await RequiredLeave(leaveId, cancellationToken);

        leave.Submit(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task RecordDecisionAsync(
        Guid leaveId,
        bool approved,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var leave = await RequiredLeave(leaveId, cancellationToken);

        if (approved)
        {
            leave.Approved(clock.Now);
        }
        else
        {
            leave.Refused(reason ?? "No reason was recorded.", clock.Now);
        }

        await business.SaveAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid leaveId, CancellationToken cancellationToken = default)
    {
        var leave = await RequiredLeave(leaveId, cancellationToken);

        leave.Cancel(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    private async Task<LeaveRequest> RequiredLeave(Guid id, CancellationToken cancellationToken) =>
        await business.FindLeaveAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no leave request with that identifier.");
}

/// <summary>
/// The public holiday calendar, and the leave it shortens.
/// </summary>
/// <remarks>
/// Next to <see cref="LeaveService"/> rather than in a file of its own, because
/// this class is the only thing keeping the day count stored on a leave request
/// honest. <see cref="LeaveRequest.Days"/> is worked out once against the
/// calendar and kept, which means it goes stale the moment the calendar moves;
/// the recount below is the whole answer to that, and a reader of leave who
/// cannot see it from where leave is written has no way of knowing it exists.
///
/// Managing the calendar sits behind settings.manage rather than a permission of
/// its own. It is the same kind of act as setting the payment terms — a
/// firm-wide fact that a handful of people maintain and everybody else is
/// affected by — and the roles that already hold that are exactly the ones who
/// would be given this.
/// </remarks>
public sealed class HolidayService(IBusinessRepository business)
{
    public Task<IReadOnlySet<DateOnly>> CalendarAsync(
        CancellationToken cancellationToken = default) =>
        business.HolidaysAsync(cancellationToken);

    /// <summary>
    /// Put a day on the calendar, and shorten the leave across it.
    /// </summary>
    /// <remarks>
    /// Returns how many requests were recounted, because whoever typed the date
    /// in should be told that it moved somebody's leave rather than left to
    /// discover it from a balance later. It is also the number that says the
    /// recount happened at all, which is worth having on the screen when the
    /// alternative is trusting that it did.
    /// </remarks>
    public async Task<int> DeclareAsync(
        DateOnly on, string name, CancellationToken cancellationToken = default)
    {
        if (await business.HolidayTakenAsync(on, cancellationToken))
        {
            /*
             * Two rows for one date would not break the count — the calendar is
             * a set of dates and a set swallows the duplicate — but it makes the
             * screen lie about what is on the calendar, and it makes withdrawing
             * the day a thing somebody has to do twice without being told so.
             */
            throw new InvalidOperationException(
                $"{on:d MMMM yyyy} is already on the calendar. Rename it if it is called "
                + "something else, or withdraw it and declare the right day.");
        }

        var calendar = new HashSet<DateOnly>(await business.HolidaysAsync(cancellationToken))
        {
            on,
        };

        business.Add(Holiday.Declared(on, name));

        var recounted = await RecountAsync(on, calendar, cancellationToken);

        // One save for the holiday and every recount it caused. Two saves would
        // leave a window in which the day is a public holiday and the leave
        // across it is still charged in full, and a failure in that window would
        // make the window permanent.
        await business.SaveAsync(cancellationToken);

        return recounted;
    }

    public async Task RenameAsync(
        Guid holidayId, string name, CancellationToken cancellationToken = default)
    {
        var holiday = await Required(holidayId, cancellationToken);

        holiday.Rename(name);
        await business.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Take a day off the calendar, and put the leave across it back.
    /// </summary>
    /// <remarks>
    /// The requests across it are recounted against a calendar this date has
    /// been taken out of, which is what puts the day back onto the ones it
    /// shortened. Removing the row and leaving the counts alone is the direction
    /// that quietly favours the firm, so it is the one worth being deliberate
    /// about.
    /// </remarks>
    public async Task<int> WithdrawAsync(
        Guid holidayId, CancellationToken cancellationToken = default)
    {
        var holiday = await Required(holidayId, cancellationToken);
        var on = holiday.On;

        var calendar = new HashSet<DateOnly>(await business.HolidaysAsync(cancellationToken));
        calendar.Remove(on);

        business.Remove(holiday);

        var recounted = await RecountAsync(on, calendar, cancellationToken);

        await business.SaveAsync(cancellationToken);

        return recounted;
    }

    /// <summary>
    /// Count every live request across a date again, against the calendar as it
    /// is about to be.
    /// </summary>
    /// <remarks>
    /// The calendar is handed in with the one date already added or taken away,
    /// rather than read back from the database here, and that is forced rather
    /// than chosen: the row has been added to the unit of work and not saved, so
    /// a query would not see it and every request would be recounted against the
    /// calendar as it was. Saving first to make it visible is the thing the one
    /// transaction exists to avoid.
    ///
    /// Adjusting a set of dates by one date is not a second implementation of
    /// anything — the set is what the table means, and this is the same set with
    /// the change in it. The rule that turns dates into a day count stays in
    /// <see cref="WorkingDays"/>, which is the part there must only ever be one
    /// of.
    /// </remarks>
    private async Task<int> RecountAsync(
        DateOnly on, IReadOnlySet<DateOnly> calendar, CancellationToken cancellationToken)
    {
        var affected = await business.LiveLeaveSpanningAsync(on, cancellationToken);

        foreach (var leave in affected)
        {
            leave.Recount(calendar);
        }

        return affected.Count;
    }

    private async Task<Holiday> Required(Guid id, CancellationToken cancellationToken) =>
        await business.FindHolidayAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no public holiday with that identifier.");
}

/// <summary>Expenses, and the claim nobody approves for themselves.</summary>
public sealed class ExpenseService(IBusinessRepository business, IClock clock)
{
    public async Task<ExpenseClaim> ClaimAsync(
        Guid employeeId,
        Domain.Common.Money amount,
        ExpenseCategory category,
        DateOnly spentOn,
        string description,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var claim = ExpenseClaim.For(
            employeeId, amount, category, spentOn, description, clock.Today);

        /*
         * Charged to a project by whoever claims it rather than by whoever approves it.
         * A taxi to a client site is a cost of that project and a new keyboard is a cost
         * of the firm, and the person who was there is the one who knows which.
         */
        claim.ChargeTo(projectId);

        business.Add(claim);
        await business.SaveAsync(cancellationToken);

        return claim;
    }

    public async Task SubmitAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        var claim = await RequiredClaim(claimId, cancellationToken);

        claim.Submit(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task RecordDecisionAsync(
        Guid claimId,
        bool approved,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var claim = await RequiredClaim(claimId, cancellationToken);

        if (approved)
        {
            claim.Approved(clock.Now);
        }
        else
        {
            claim.Refused(reason ?? "No reason was recorded.", clock.Now);
        }

        await business.SaveAsync(cancellationToken);
    }

    public async Task PayAsync(
        Guid claimId, string? reference, CancellationToken cancellationToken = default)
    {
        var claim = await RequiredClaim(claimId, cancellationToken);

        claim.Paid(clock.Now, reference);
        await business.SaveAsync(cancellationToken);
    }

    private async Task<ExpenseClaim> RequiredClaim(Guid id, CancellationToken cancellationToken) =>
        await business.FindClaimAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no claim with that identifier.");
}

/// <summary>Invoices, and the hours that become one.</summary>
public sealed class InvoiceService(
    IBusinessRepository business, Settings.SettingsService settings, IClock clock)
{
    /// <summary>
    /// Start a bill for a client.
    /// </summary>
    /// <remarks>
    /// An active contract is deliberately not required here, and the decision is
    /// worth writing down because the opposite one looks more rigorous.
    ///
    /// Every client in this database predates contracts existing, so the check
    /// would refuse the first invoice raised after it shipped — including for
    /// work already delivered — and go on refusing until somebody had typed up
    /// the paper for all of them. A system that will not invoice does not make
    /// the firm careful; it stops the firm being paid, and what it actually
    /// produces is a contract row typed in a hurry to get past the refusal,
    /// which is worse evidence than no row at all.
    ///
    /// Small jobs are also genuinely done on an email or a purchase order, and
    /// the written agreement arrives afterwards. So the gap is surfaced rather
    /// than blocked: the client page says when nothing covers today, and the
    /// contract page says what has been billed against it. If that turns out to
    /// be ignored, the refusal becomes a defensible next step — with a backfill
    /// behind it, which is the part that has to exist first.
    /// </remarks>
    /// <summary>
    /// Say which project an invoice bills for, or that it bills for none.
    /// </summary>
    /// <remarks>
    /// Without this, a project could never show what it earned: an invoice knew its
    /// client, and a client has four projects running. Nullable because a retainer or a
    /// licence renewal is billed to a client and not to a project.
    /// </remarks>
    public async Task BillForAsync(
        Guid invoiceId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        var invoice = await business.FindInvoiceAsync(invoiceId, cancellationToken)
            ?? throw new InvalidOperationException("There is no invoice with that identifier.");

        invoice.BillsFor(projectId);
        await business.SaveAsync(cancellationToken);
    }

    public async Task<Invoice> DraftAsync(
        Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await business.FindClientAsync(clientId, cancellationToken)
            ?? throw new InvalidOperationException("There is no client with that identifier.");

        if (!client.IsCurrent)
        {
            throw new InvalidOperationException(
                $"{client.Name} is a former client. Invoicing them would be a surprise to "
                + "everybody.");
        }

        var firm = await settings.CurrentAsync(cancellationToken);

        var invoice = Invoice.Draft(
            clientId,
            await NextNumberAsync(firm.InvoicePrefix, cancellationToken),
            firm.Currency,
            clock.Today,
            client.PaymentTermDays);

        business.Add(invoice);
        await business.SaveAsync(cancellationToken);

        return invoice;
    }

    public async Task AddLineAsync(
        Guid invoiceId,
        string description,
        int quantity,
        Domain.Common.Money unitPrice,
        CancellationToken cancellationToken = default)
    {
        var invoice = await RequiredInvoice(invoiceId, cancellationToken);

        invoice.AddLine(description, quantity, unitPrice);
        await business.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Turn a project's approved, billable, uninvoiced hours into lines.
    /// </summary>
    /// <remarks>
    /// The point of having timesheets at all. Each entry is marked with the
    /// invoice it went on, in the same transaction, so the same hour cannot be
    /// billed twice — which is the mistake that costs a client relationship
    /// rather than an afternoon.
    /// </remarks>
    public async Task<int> BillTimeAsync(
        Guid invoiceId,
        Guid projectId,
        Domain.Common.Money hourlyRate,
        CancellationToken cancellationToken = default)
    {
        var invoice = await RequiredInvoice(invoiceId, cancellationToken);
        var entries = await business.BillableTimeAsync(projectId, cancellationToken);

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "There are no approved, billable, uninvoiced hours on that project.");
        }

        var minutes = entries.Sum(entry => entry.Minutes);
        var hours = minutes / 60m;

        invoice.AddLine(
            $"Delivery work, {minutes / 60}h {minutes % 60}m",
            1,
            hourlyRate.Multiply(hours));

        foreach (var entry in entries)
        {
            entry.PutOnInvoice(invoiceId);
        }

        await business.SaveAsync(cancellationToken);

        return entries.Count;
    }

    public async Task SendAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await RequiredInvoice(invoiceId, cancellationToken);

        invoice.Send(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task RecordPaymentAsync(
        Guid invoiceId,
        Domain.Common.Money amount,
        DateOnly on,
        string? reference,
        CancellationToken cancellationToken = default)
    {
        var invoice = await RequiredInvoice(invoiceId, cancellationToken);

        invoice.RecordPayment(amount, on, reference, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task VoidAsync(
        Guid invoiceId, string reason, CancellationToken cancellationToken = default)
    {
        var invoice = await RequiredInvoice(invoiceId, cancellationToken);

        invoice.Void(reason, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The next number, as JTS-2026-0007.
    /// </summary>
    /// <remarks>
    /// Sequential within a year and never reused, because that is what an
    /// accountant expects and what a client quotes back. A gap in the sequence
    /// is a question somebody has to answer, so a voided invoice keeps its
    /// number rather than freeing it.
    /// </remarks>
    private async Task<string> NextNumberAsync(string prefix, CancellationToken cancellationToken)
    {
        var year = clock.Today.Year;
        var next = await business.LastInvoiceSequenceAsync(prefix, year, cancellationToken) + 1;

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var number = $"{prefix}-{year}-{next + attempt:0000}";

            if (!await business.InvoiceNumberTakenAsync(number, cancellationToken))
            {
                return number;
            }
        }

        throw new InvalidOperationException(
            "Could not find a free invoice number. Something is wrong with the sequence.");
    }

    private async Task<Invoice> RequiredInvoice(Guid id, CancellationToken cancellationToken) =>
        await business.FindInvoiceAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no invoice with that identifier.");
}

/// <summary>Interviews, and the scorecards that follow them.</summary>
public sealed class InterviewService(
    IBusinessRepository business, IPeopleRepository people, IClock clock)
{
    public async Task<Interview> ScheduleAsync(
        Guid applicationId,
        InterviewKind kind,
        DateTimeOffset at,
        IReadOnlyList<Guid> panel,
        string? where = null,
        CancellationToken cancellationToken = default)
    {
        var application = await business.FindApplicationAsync(applicationId, cancellationToken)
            ?? throw new InvalidOperationException("There is no application with that identifier.");

        if (!application.IsLive)
        {
            throw new InvalidOperationException(
                $"That application is {application.Status.ToString().ToLowerInvariant()}. "
                + "Interviewing somebody who is out of the process would waste everybody time.");
        }

        foreach (var interviewer in panel)
        {
            var person = await people.FindAsync(interviewer, cancellationToken)
                ?? throw new InvalidOperationException(
                    "One of those interviewers is not on the staff list.");

            if (!person.IsAssignable)
            {
                throw new InvalidOperationException(
                    $"{person.FullName} is {person.Status.ToString().ToLowerInvariant()} and "
                    + "cannot be put on a panel.");
            }
        }

        var interview = Interview.Schedule(applicationId, kind, at, panel, where);

        business.Add(interview);
        await business.SaveAsync(cancellationToken);

        return interview;
    }

    public async Task HeldAsync(Guid interviewId, CancellationToken cancellationToken = default)
    {
        var interview = await RequiredInterview(interviewId, cancellationToken);

        interview.Held(clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task ScoreAsync(
        Guid interviewId,
        Guid interviewerId,
        Recommendation recommendation,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var interview = await RequiredInterview(interviewId, cancellationToken);

        interview.Score(interviewerId, recommendation, notes, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    public async Task CancelAsync(
        Guid interviewId, string reason, CancellationToken cancellationToken = default)
    {
        var interview = await RequiredInterview(interviewId, cancellationToken);

        interview.Cancel(reason, clock.Now);
        await business.SaveAsync(cancellationToken);
    }

    private async Task<Interview> RequiredInterview(Guid id, CancellationToken cancellationToken) =>
        await business.FindInterviewAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no interview with that identifier.");
}
