using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Reporting;

/// <summary>
/// What is waiting on the person reading the screen.
/// </summary>
/// <remarks>
/// Section 35 had one home page and one standing report, and asked for role-specific
/// dashboards. This is that, arrived at from the other direction.
///
/// The obvious reading of "role-specific dashboard" is a different page per role, each with
/// its own tiles. That is what the home page's own comment already argues against: a
/// dashboard of counts nobody acts on is read twice and then skipped, and the figures on it
/// are the first thing to go stale. Five such pages would be five things to keep true.
///
/// So instead of a page per role, one list whose contents depend on what the reader can
/// actually do. Every entry answers three things — how many, what they are, and where to go —
/// and nothing appears unless there is something to act on. A head of department sees leave
/// to decide; a delivery manager sees timesheets and unbilled hours; an administrator sees
/// failing deliveries. Somebody holding all three roles sees all three, in one list, which is
/// what they would want and what five pages could not give them.
///
/// <b>Nothing here is a count of things that merely exist.</b> "Twelve open projects" is a
/// fact about the firm and not a thing to do; "three timesheets waiting on you" is. The
/// difference is whether the number goes down when somebody does something.
/// </remarks>
public sealed class WaitingQueries(AppDbContext database)
{
    public async Task<List<WaitingOnYou>> ForAsync(
        IReadOnlySet<string> permissions,
        Guid? employeeId,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var waiting = new List<WaitingOnYou>();

        /*
         * Approvals first, and only the ones actually addressed to this person. A count of
         * everything anybody is waiting on would be a number a reader cannot reduce, which
         * is the definition of a tile nobody acts on.
         */
        if (permissions.Contains(Permissions.ApprovalsDecide) && employeeId is { } decider)
        {
            var steps = await database.Approvals
                .AsNoTracking()
                .SelectMany(request => request.Steps)
                .CountAsync(
                    step => step.DeciderId == decider && step.DecidedAt == null,
                    cancellationToken);

            Add(waiting, steps, "approval", "approvals", "waiting on your decision", "/approvals");
        }

        if (permissions.Contains(Permissions.LeaveApprove))
        {
            var leave = await database.Leave
                .AsNoTracking()
                .CountAsync(request => request.Status == LeaveStatus.AwaitingApproval,
                    cancellationToken);

            Add(waiting, leave, "leave request", "leave requests", "to decide", "/leave/decisions");
        }

        if (permissions.Contains(Permissions.TimeApprove))
        {
            var entries = await database.TimeEntries
                .AsNoTracking()
                .CountAsync(entry => entry.ApprovedAt == null, cancellationToken);

            Add(waiting, entries, "hour", "hours", "logged and not approved", "/time/approvals");
        }

        if (permissions.Contains(Permissions.ExpensesApprove))
        {
            var claims = await database.Expenses
                .AsNoTracking()
                .CountAsync(claim => claim.Status == ClaimStatus.AwaitingApproval,
                    cancellationToken);

            Add(waiting, claims, "expense claim", "expense claims", "to decide", "/expenses/claims");
        }

        /*
         * Overdue money, which is the one entry on this list that is somebody else's fault
         * and still this reader's job.
         */
        if (permissions.Contains(Permissions.InvoicesView))
        {
            var overdue = await database.Invoices
                .AsNoTracking()
                .CountAsync(
                    invoice => invoice.Status == InvoiceStatus.Sent && invoice.DueOn < today,
                    cancellationToken);

            Add(waiting, overdue, "invoice", "invoices", "overdue and unpaid", "/invoices");
        }

        if (permissions.Contains(Permissions.ClientsView))
        {
            var lapsing = await database.Contracts
                .AsNoTracking()
                .CountAsync(
                    contract => contract.State == Domain.Contracts.ContractState.Active
                        && contract.EndsOn != null
                        && contract.EndsOn <= today.AddDays(45),
                    cancellationToken);

            Add(
                waiting,
                lapsing,
                "client contract",
                "client contracts",
                "running out within six weeks",
                "/clients");
        }

        /*
         * The machinery. Only shown to somebody who could act on it, and only when something
         * has actually given up — a queue with a backlog is working, and telling somebody
         * about it would train them to ignore the line that matters.
         */
        if (permissions.Contains(Permissions.ReposDeliveries))
        {
            var dead = await database.Deliveries
                .AsNoTracking()
                .CountAsync(delivery => delivery.Status == DeliveryStatus.DeadLettered,
                    cancellationToken);

            Add(
                waiting,
                dead,
                "delivery",
                "deliveries",
                "gave up, so the board is incomplete",
                "/repositories/deliveries");
        }

        if (permissions.Contains(Permissions.SettingsManage))
        {
            var notifications = await database.OutboundDeliveries
                .AsNoTracking()
                .CountAsync(one => one.Status == OutboundStatus.DeadLettered, cancellationToken);

            Add(
                waiting,
                notifications,
                "notification",
                "notifications",
                "gave up, so somebody outside is missing something",
                "/settings/webhooks");

            var abandoned = await database.Outbox
                .AsNoTracking()
                .CountAsync(message => message.AbandonedAt != null, cancellationToken);

            Add(
                waiting,
                abandoned,
                "event",
                "events",
                "gave up and will not be tried again",
                "/settings/machinery");
        }

        if (permissions.Contains(Permissions.EmployeesManage))
        {
            var leavers = await database.Offboardings
                .AsNoTracking()
                .CountAsync(one => one.CompletedAt == null, cancellationToken);

            Add(
                waiting,
                leavers,
                "departure",
                "departures",
                "not finished — access, or something not returned",
                "/people/leavers");
        }

        return waiting;
    }

    /// <summary>
    /// Add an entry, unless there is nothing to do.
    /// </summary>
    /// <remarks>
    /// The whole discipline of this class in one place. A zero is not shown: a list that said
    /// "0 timesheets waiting" every day would teach the reader to stop reading it, and the
    /// day it said three they would not notice.
    /// </remarks>
    private static void Add(
        List<WaitingOnYou> waiting, int count, string one, string many, string what, string where)
    {
        if (count > 0)
        {
            waiting.Add(new WaitingOnYou(count, count == 1 ? one : many, what, where));
        }
    }
}

/// <summary>
/// One thing waiting, and where to deal with it.
/// </summary>
/// <remarks>
/// The noun is carried already pluralised, because "1 invoices" is the kind of detail that
/// makes a reader trust a screen slightly less without being able to say why.
/// </remarks>
public sealed record WaitingOnYou(int Count, string Noun, string What, string Where);
