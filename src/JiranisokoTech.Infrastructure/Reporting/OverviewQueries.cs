using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Reporting;

/// <summary>
/// One figure on the company overview, and where to go for the detail behind it.
/// </summary>
/// <remarks>
/// Every figure carries its own link rather than the page deciding, because the page that
/// knows the number is the page that knows which screen it came off — and a tile linking
/// somewhere that does not show the same figure is how a reader stops believing the tile.
///
/// <see cref="Worrying"/> rather than a severity. There are exactly two states worth having
/// here: a figure that is simply the state of the firm, and one somebody should look at today.
/// A five-level scale on a dashboard is a scale nobody agrees about.
/// </remarks>
public sealed record Figure(
    string What, string Reading, string? Detail, string Where, bool Worrying = false);

/// <summary>A group of figures that came from one permission.</summary>
public sealed record Panel(string Heading, IReadOnlyList<Figure> Figures);

/// <summary>
/// How the firm is, for whoever may be told.
/// </summary>
/// <remarks>
/// Section 63. The brief names eleven figures and adds one sentence that decides the whole
/// shape of this class: <em>make all widgets permission-aware</em>.
///
/// <b>Every panel is gated by the permission that guards the screen its figures came off, and
/// a panel somebody may not see is not queried at all</b> — not queried and hidden. That is the
/// same discipline <c>SearchQueries</c> states for the same reason: this is one of two places
/// in the application that reaches into a dozen tables at once, and the difference between
/// "not shown" and "not read" is the difference that matters when the thing being withheld is
/// what the firm earned last month.
///
/// <b>It is a page of its own and not a section of the home page.</b> Home argues against a
/// wall of tiles and the argument is good — but it is an argument about the page somebody
/// opens every morning to find out what to do. This answers a different question, asked at a
/// different time by a different person: not "what should I do now" but "how is the firm". Put
/// the second on the first and the first gets longer while the second gets skipped.
///
/// <b>Nothing here is computed twice.</b> Every figure is read from the tables the existing
/// screens read, and two of them come from <c>ReportingQueries</c> and <c>ProjectMoneyQueries</c>
/// unchanged. A dashboard that recomputes a number a screen already shows is a dashboard that
/// will eventually disagree with that screen, and the reader will believe whichever they saw
/// last.
/// </remarks>
public sealed class OverviewQueries(AppDbContext database, ProjectMoneyQueries money)
{
    /// <summary>How soon a certificate or a domain counts as an alert.</summary>
    /// <remarks>
    /// Thirty days, which is what section 14 of the brief names and what the expiry job
    /// already uses. Stated once here rather than typed again, so the overview and the job
    /// cannot come to disagree about what "expiring" means.
    /// </remarks>
    public const int ExpiringWithin = 30;

    /// <summary>How far ahead a deadline counts as upcoming.</summary>
    public const int DeadlineWithin = 14;

    public async Task<List<Panel>> ForAsync(
        IReadOnlySet<string> permissions,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        var panels = new List<Panel>();

        if (permissions.Contains(Permissions.InvoicesView))
        {
            panels.Add(new Panel("Money", await MoneyAsync(today, cancellationToken)));
        }

        if (permissions.Contains(Permissions.ProjectsViewAll))
        {
            panels.Add(new Panel("Delivery", await DeliveryAsync(today, cancellationToken)));
        }

        if (permissions.Contains(Permissions.IncidentsView)
            || permissions.Contains(Permissions.PlatformView))
        {
            panels.Add(new Panel(
                "What is running", await RunningAsync(permissions, today, cancellationToken)));
        }

        if (permissions.Contains(Permissions.EmployeesViewAll)
            || permissions.Contains(Permissions.CandidatesView))
        {
            panels.Add(new Panel("People", await PeopleAsync(permissions, cancellationToken)));
        }

        /*
         * A panel with nothing in it is dropped rather than shown empty. It happens when
         * somebody holds the permission but the firm has none of the thing — no open
         * positions, no incidents — and "0 open incidents" is a line that teaches a reader to
         * skim, which is the one thing a page like this cannot afford.
         */
        return [.. panels.Where(one => one.Figures.Count > 0)];
    }

    /// <summary>
    /// What the firm has been paid, and what it is still owed.
    /// </summary>
    /// <remarks>
    /// Totalled through <see cref="Tally"/>, which this application already uses for exactly
    /// this problem: a total, or the reason there is not one. Money refuses to add across
    /// currencies, and a single figure on an overview silently adding shillings to dollars
    /// would be the most expensive wrong number this application could print — so where the
    /// invoices are in more than one currency the reading says so instead of inventing a sum.
    ///
    /// The invoices are loaded rather than summed in SQL because what is being added is
    /// <c>Outstanding</c>, which is the total less the payments against it, and that is a
    /// property on the entity. It is the same read <c>ReportingQueries</c> already does for the
    /// same reason, and it is bounded by the number of unsettled invoices rather than by the
    /// table.
    /// </remarks>
    private async Task<List<Figure>> MoneyAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        var figures = new List<Figure>();
        var yearOpened = new DateOnly(today.Year, 1, 1);

        var settled = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .Where(invoice => invoice.Status == InvoiceStatus.Paid
                && invoice.IssuedOn >= yearOpened)
            .ToListAsync(cancellationToken);

        figures.Add(new Figure(
            "Invoiced and paid",
            Reading(Across(settled.Select(invoice => invoice.Total))),
            $"since {yearOpened:d MMMM}",
            "/reports"));

        var unsettled = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .Where(invoice => invoice.Status == InvoiceStatus.Sent
                || invoice.Status == InvoiceStatus.PartlyPaid)
            .ToListAsync(cancellationToken);

        if (unsettled.Count > 0)
        {
            figures.Add(new Figure(
                "Owed to us",
                Reading(Across(unsettled.Select(invoice => invoice.Outstanding))),
                unsettled.Count == 1 ? "on one invoice" : $"on {unsettled.Count} invoices",
                "/invoices"));
        }

        var overdue = unsettled.Where(invoice => invoice.DueOn < today).ToList();

        if (overdue.Count > 0)
        {
            figures.Add(new Figure(
                "Past its due date",
                Reading(Across(overdue.Select(invoice => invoice.Outstanding))),
                overdue.Count == 1
                    ? "on one invoice, and somebody has to chase it"
                    : $"across {overdue.Count} invoices, and somebody has to chase them",
                "/invoices",
                Worrying: true));
        }

        return figures;
    }

    private async Task<List<Figure>> DeliveryAsync(
        DateOnly today, CancellationToken cancellationToken)
    {
        var figures = new List<Figure>();

        var running = await database.Projects
            .AsNoTracking()
            .CountAsync(one => one.Status == ProjectStatus.Active, cancellationToken);

        figures.Add(new Figure(
            "Projects running",
            running == 1 ? "1 project" : $"{running} projects",
            null,
            "/projects"));

        /*
         * At risk is asked of ProjectMoneyQueries rather than recomputed, because that screen
         * already decides what over budget means — in the presence of mixed currencies and of
         * projects with no cost rate — and two answers to "is this project in trouble" would
         * eventually differ. The overview's job is to say how many, and to point at the screen
         * that says which.
         */
        var overBudget = (await money.AllAsync(cancellationToken))
            .Count(one => one.Status == ProjectStatus.Active
                && one.Budget is { } budget
                && one.Cost is { } cost
                && cost > budget);

        var late = await database.Projects
            .AsNoTracking()
            .CountAsync(
                one => one.Status == ProjectStatus.Active
                    && one.DueOn != null
                    && one.DueOn < today,
                cancellationToken);

        if (overBudget + late > 0)
        {
            figures.Add(new Figure(
                "Projects in trouble",
                Said(overBudget, late),
                "over budget, or past the date they were due",
                "/projects/money",
                Worrying: true));
        }

        var due = await database.WorkItems
            .AsNoTracking()
            .CountAsync(
                one => one.Status != WorkItemStatus.Todo
                    && one.DueOn != null
                    && one.DueOn >= today
                    && one.DueOn <= today.AddDays(DeadlineWithin),
                cancellationToken);

        if (due > 0)
        {
            figures.Add(new Figure(
                "Due in the next fortnight",
                due == 1 ? "1 piece of work" : $"{due} pieces of work",
                null,
                "/work"));
        }

        return figures;
    }

    private async Task<List<Figure>> RunningAsync(
        IReadOnlySet<string> permissions, DateOnly today, CancellationToken cancellationToken)
    {
        var figures = new List<Figure>();

        if (permissions.Contains(Permissions.IncidentsView))
        {
            var open = await database.Incidents
                .AsNoTracking()
                .CountAsync(one => one.Status != IncidentStatus.Resolved, cancellationToken);

            if (open > 0)
            {
                figures.Add(new Figure(
                    "Incidents not resolved",
                    open == 1 ? "1 incident" : $"{open} incidents",
                    null,
                    "/incidents",
                    Worrying: true));
            }
        }

        if (permissions.Contains(Permissions.PlatformView))
        {
            /*
             * Certificates and domains that run out soon. The job of section 14 already warns
             * about these and raises work for them; this says how many are outstanding, which
             * is the number that tells somebody whether the job is being acted on or ignored.
             */
            var expiring = await database.Resources
                .AsNoTracking()
                .CountAsync(
                    one => one.ExpiresOn != null
                        && one.ExpiresOn >= today
                        && one.ExpiresOn <= today.AddDays(ExpiringWithin),
                    cancellationToken);

            var expired = await database.Resources
                .AsNoTracking()
                .CountAsync(one => one.ExpiresOn != null && one.ExpiresOn < today,
                    cancellationToken);

            if (expiring + expired > 0)
            {
                figures.Add(new Figure(
                    "Certificates and domains",
                    expired > 0
                        ? $"{expired} already expired"
                        : $"{expiring} expiring within {ExpiringWithin} days",
                    expired > 0 && expiring > 0 ? $"and {expiring} expiring" : null,
                    "/platform",
                    Worrying: expired > 0));
            }
        }

        return figures;
    }

    private async Task<List<Figure>> PeopleAsync(
        IReadOnlySet<string> permissions, CancellationToken cancellationToken)
    {
        var figures = new List<Figure>();

        if (permissions.Contains(Permissions.EmployeesViewAll))
        {
            var active = await database.Employees
                .AsNoTracking()
                .CountAsync(one => one.Status == EmploymentStatus.Active, cancellationToken);

            var joining = await database.Employees
                .AsNoTracking()
                .CountAsync(one => one.Status == EmploymentStatus.Invited, cancellationToken);

            figures.Add(new Figure(
                "People",
                active == 1 ? "1 person" : $"{active} people",
                joining > 0 ? $"and {joining} joining" : null,
                "/people"));
        }

        if (permissions.Contains(Permissions.CandidatesView))
        {
            var open = await database.Postings
                .AsNoTracking()
                .CountAsync(one => one.Status == PostingStatus.Published, cancellationToken);

            if (open > 0)
            {
                figures.Add(new Figure(
                    "Positions advertised",
                    open == 1 ? "1 opening" : $"{open} openings",
                    null,
                    "/hiring/postings"));
            }

            /*
             * Somewhere in the pipeline, which is everything that is neither finished nor
             * abandoned. Counting applications rather than candidates, because one person may
             * apply for two jobs and the number a recruiter acts on is the number of things
             * waiting for an answer.
             */
            var moving = await database.Applications
                .AsNoTracking()
                .CountAsync(
                    one => one.Status != ApplicationStatus.Hired
                        && one.Status != ApplicationStatus.Rejected
                        && one.Status != ApplicationStatus.Withdrawn,
                    cancellationToken);

            if (moving > 0)
            {
                figures.Add(new Figure(
                    "Applications in the pipeline",
                    moving == 1 ? "1 application" : $"{moving} applications",
                    null,
                    "/hiring/applications"));
            }
        }

        return figures;
    }

    /// <summary>
    /// A total, or the reason there is not one.
    /// </summary>
    /// <remarks>
    /// The same three states <c>ReportingQueries.Total</c> produces, and deliberately the same
    /// type: nothing to add and cannot be added are different facts, and only one of them is
    /// reassuring. A page handed a bare null cannot tell them apart and picks the wrong
    /// sentence.
    /// </remarks>
    private static Tally Across(IEnumerable<Money> amounts)
    {
        var list = amounts.ToList();

        if (list.Count == 0)
        {
            return Tally.Nothing;
        }

        var currency = list[0].Currency;

        return list.Any(amount => amount.Currency != currency)
            ? Tally.AcrossCurrencies
            : new Tally(list.Aggregate(
                Money.Zero(currency), (running, amount) => running + amount));
    }

    /// <summary>What a tally reads as on the screen.</summary>
    private static string Reading(Tally tally) =>
        tally switch
        {
            { HasFigure: true, Amount: { } amount } => amount.ToString(),
            { Mixed: true } => "in more than one currency",
            _ => "nothing yet",
        };

    private static string Said(int overBudget, int late) =>
        (overBudget, late) switch
        {
            (0, var l) => l == 1 ? "1 late" : $"{l} late",
            (var b, 0) => b == 1 ? "1 over budget" : $"{b} over budget",
            var (b, l) => $"{b} over budget, {l} late",
        };
}
