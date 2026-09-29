using JiranisokoTech.Application.Assets;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Application.Incidents;
using JiranisokoTech.Application.Knowledge;
using JiranisokoTech.Application.Notices;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Platform;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Application.Support;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Assets;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Platform;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Domain.Work;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.DemoData;

/// <summary>What the seed wrote, so the tool and a test can both check it.</summary>
public sealed record DemoReport(
    int Departments,
    int People,
    int Clients,
    int Projects,
    int WorkItems,
    int Invoices,
    int Candidates,
    int Assets,
    int Resources,
    int Incidents,
    int Tickets,
    int Repositories,
    int Articles);

/// <summary>
/// Eighteen months of a small software firm, written through the application's own services.
/// </summary>
/// <remarks>
/// Section 83. Every row goes through the service a person would use, which is the decision this
/// file rests on and it is worth naming the alternative: raw SQL, the way
/// <c>tools/JiranisokoTech.ScaleCheck/volume.sql</c> does it. That tool is right to — it needs a
/// million rows and it is measuring reads, so the rules would cost an hour of machine time for no
/// benefit.
///
/// Here the rules are the point. A demonstration database is for looking at pages, and a page
/// shows what a record means rather than what is in its columns: an invoice has to have been
/// drafted, had lines added, been sent and then part-paid for the ageing report to have anything
/// to age; a work item has to have moved through its transitions for a burndown to have a shape;
/// a requisition has to have been approved before an advert can be published, because the service
/// refuses otherwise. Writing the rows directly would produce a database that satisfies the schema
/// and contradicts the domain — and every screen reading it would be showing something that could
/// not have happened.
///
/// It also means this file is the widest compile-time check in the repository that the application
/// services still fit together, and the only thing anywhere that exercises a year of them in
/// order.
///
/// <b>The clock travels.</b> See <see cref="TravellingClock"/>: without it every date would be the
/// same three seconds and every trend on every screen would be a flat line.
/// </remarks>
public sealed class DemoFirm(
    IServiceProvider services, TravellingClock clock, TextWriter say)
{
    /// <summary>How far back the history starts.</summary>
    /// <remarks>
    /// Eighteen months, because the longest thing any screen looks back over is a year — the
    /// reporting page's months, the ageing buckets, a review cycle — and a history exactly as long
    /// as the window makes the earliest bucket look empty rather than early.
    /// </remarks>
    private const int MonthsOfHistory = 18;

    /// <summary>
    /// How many batches of queued announcements to settle before giving up on the queue.
    /// </summary>
    /// <remarks>
    /// The seed queues a few hundred and the dispatcher takes one batch per pass, so fifty passes
    /// is several times more than it produces. Reaching it means messages are failing rather than
    /// waiting, which is worth reporting rather than looping over.
    /// </remarks>
    private const int DrainPasses = 50;

    private readonly List<string> _steps = [];

    public async Task<DemoReport> FillAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-MonthsOfHistory);

        var firm = await TheFirmAsync(start, cancellationToken);
        var people = await ThePeopleAsync(firm, start, today, cancellationToken);
        var clients = await TheClientsAsync(start, cancellationToken);
        var projects = await TheProjectsAsync(people, clients, start, today, cancellationToken);
        var repositories = await TheRepositoriesAsync(projects, start, cancellationToken);
        var work = await TheWorkAsync(people, projects, start, today, cancellationToken);
        var invoices = await TheInvoicesAsync(clients, projects, today, cancellationToken);
        var hiring = await TheHiringAsync(people, firm, today, cancellationToken);
        var assets = await TheAssetsAsync(people, start, cancellationToken);
        var estate = await TheEstateAsync(people, repositories, today, cancellationToken);
        var incidents = await TheIncidentsAsync(people, estate, today, cancellationToken);
        var tickets = await TheHelpDeskAsync(people, clients, today, cancellationToken);
        var articles = await TheKnowledgeAsync(people, today, cancellationToken);

        await TheNoticeAsync(people, today, cancellationToken);

        clock.MoveTo(today);

        return new DemoReport(
            firm.Departments.Count,
            people.Everybody.Count,
            clients.Everybody.Count,
            projects.Everybody.Count,
            work,
            invoices,
            hiring,
            assets,
            estate.Resources,
            incidents,
            tickets,
            repositories.Everybody.Count,
            articles);
    }

    /// <summary>
    /// Settle everything the seed queued, and say how much was left over.
    /// </summary>
    /// <remarks>
    /// Part of the seed's job rather than the caller's, because a demonstration database whose
    /// queue is full is one where the application processes eighteen months of announcements on its
    /// next start — writing a year of letters and notices to whoever signs in first.
    ///
    /// <b>Until it is empty, not once.</b> The first version of this was a single
    /// <c>RunOnceAsync</c> in the tool's Program, and it settled fifty of two hundred and
    /// twenty-nine: one call takes one batch, which is what its name says and what the background
    /// processor relies on. The rest were processed by the application on its next start, exactly
    /// as the comment there said would not happen — found by seeding a real database, starting the
    /// container, and reading six acknowledgement letters being written in its log.
    ///
    /// Bounded, because a message that fails every attempt is retried rather than dropped and an
    /// unbounded loop over one would never end. Reaching the bound means something is failing, and
    /// the count this returns is how the caller says so.
    /// </remarks>
    public async Task<(int Settled, int LeftOver)> DrainAsync(
        CancellationToken cancellationToken = default)
    {
        var dispatcher = Get<Infrastructure.Messaging.OutboxDispatcher>();
        var settled = 0;

        for (var pass = 0; pass < DrainPasses; pass++)
        {
            var took = await dispatcher.RunOnceAsync(cancellationToken);

            if (took == 0)
            {
                break;
            }

            settled += took;
        }

        var database = Get<Infrastructure.Persistence.AppDbContext>();

        var waiting = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(
                database.Outbox.Where(one => one.DispatchedAt == null),
                cancellationToken);

        return (settled, waiting);
    }

    public IReadOnlyList<string> Steps => _steps;

    // ---------------------------------------------------------------- the firm ---------------

    private async Task<Firm> TheFirmAsync(DateOnly start, CancellationToken ct)
    {
        clock.MoveTo(start);

        var settings = Get<SettingsService>();

        /*
         * The trading name carries the marker, and it is the first thing written because it is the
         * one that reaches paper: it is on every invoice and every offer letter this seed then
         * produces, so writing it afterwards would leave documents that do not say what they are.
         */
        await settings.IdentifyAsync(
            Marker.TradingName,
            Marker.LegalName,
            "P000000000X",
            "+254 700 000 000",
            Marker.Email("hello"),
            "https://jiranisokotech.co.ke",
            ct);

        await settings.MoveToAsync("Riverside Drive", "Nairobi", "00100", "Kenya", ct);
        await settings.BillAsAsync(Marker.InvoicePrefix, Marker.Currency, 30, ct);

        // What an hour of the firm's time costs it, which is what makes a project's margin a
        // number rather than a guess.
        await settings.CostAnHourAtAsync(1_20000, ct);

        var people = Get<PeopleService>();
        var departments = new List<Guid>();

        foreach (var (name, about) in new[]
        {
            ("Delivery", "Client projects, from the first conversation to the last invoice."),
            ("Engineering", "The code, the pipelines and everything that runs."),
            ("Operations", "Money, people, paper and the office."),
        })
        {
            var department = await people.OpenDepartmentAsync(
                name, Marker.Code(name.ToLowerInvariant()), about, ct);

            departments.Add(department.Id);
        }

        Said($"the firm and {departments.Count} departments");

        return new Firm(departments);
    }

    // ---------------------------------------------------------------- the people -------------

    private async Task<Staff> ThePeopleAsync(
        Firm firm, DateOnly start, DateOnly today, CancellationToken ct)
    {
        var people = Get<PeopleService>();
        var hired = new List<Guid>();

        /*
         * Hired across the whole timeline rather than all on day one, because half the screens in
         * the people module are about tenure: who is new, who is due a review, what the headcount
         * was in March. A firm where everybody started on the same morning answers none of them.
         */
        var joiners = new (string Name, string Title, int Department, int MonthsIn)[]
        {
            ("Maria Njeri", "Managing director", 2, 0),
            ("Daniel Ochieng", "Head of delivery", 0, 0),
            ("Grace Mwangi", "Head of engineering", 1, 1),
            ("Brian Kimani", "Senior engineer", 1, 2),
            ("Aisha Hassan", "Engineer", 1, 5),
            ("Peter Otieno", "Engineer", 1, 8),
            ("Faith Wanjiru", "Delivery manager", 0, 4),
            ("Samuel Kariuki", "Designer", 0, 9),
            ("Esther Njoroge", "Accountant", 2, 3),
            ("Kevin Mutua", "Support engineer", 1, 12),
        };

        foreach (var joiner in joiners)
        {
            clock.MoveTo(start.AddMonths(joiner.MonthsIn));

            var person = await people.HireAsync(
                joiner.Name,
                start.AddMonths(joiner.MonthsIn),
                firm.Departments[joiner.Department],
                joiner.Title,
                cancellationToken: ct);

            await people.StartAsync(person.Id, ct);

            /*
             * Salary terms, because payroll skips anybody without them and a payroll screen with
             * nothing on it is the commonest way a demonstration looks broken. The figures are
             * round and obviously invented, which is the right kind of obvious for this data.
             */
            await people.AgreeTermsAsync(
                person.Id,
                EmploymentTerms.Empty with
                {
                    Contract = ContractType.Permanent,
                    SalaryMinorUnits = 25_000_00L + (joiners.Length - hired.Count) * 3_000_00L,
                    SalaryCurrency = Marker.Currency,
                },
                ct);

            hired.Add(person.Id);
        }

        // The three heads, and a reporting line for everybody else.
        await people.AppointHeadAsync(firm.Departments[0], hired[1], ct);
        await people.AppointHeadAsync(firm.Departments[1], hired[2], ct);
        await people.AppointHeadAsync(firm.Departments[2], hired[0], ct);

        var above = new Dictionary<int, int>
        {
            [1] = 0, [2] = 0, [3] = 2, [4] = 2, [5] = 2, [6] = 1, [7] = 1, [8] = 0, [9] = 2,
        };

        foreach (var (person, manager) in above)
        {
            await people.SetReportingLineAsync(hired[person], hired[manager], ct);
        }

        Said($"{hired.Count} people, three heads and a reporting line for each");

        return new Staff(hired);
    }

    // ---------------------------------------------------------------- the clients ------------

    private async Task<Clients> TheClientsAsync(DateOnly start, CancellationToken ct)
    {
        var clients = Get<ClientService>();
        var contacts = Get<ContactService>();
        var taken = new List<Guid>();

        var accounts = new (string Name, string Code, string Contact, int MonthsIn, ClientStatus Standing)[]
        {
            ("Mombasa Freight", "mombasa-freight", "Halima Yusuf", 1, ClientStatus.Active),
            ("Rift Valley Dairies", "rift-valley", "Joseph Kipchoge", 3, ClientStatus.Active),
            ("Lakeside Microfinance", "lakeside", "Ruth Adhiambo", 7, ClientStatus.Active),
            ("Karen Veterinary Group", "karen-vet", "Alex Mbugua", 15, ClientStatus.Prospect),
        };

        foreach (var account in accounts)
        {
            clock.MoveTo(start.AddMonths(account.MonthsIn));

            var client = await clients.TakeOnAsync(
                account.Name,
                Marker.Code(account.Code),
                account.Contact,
                Marker.Email(account.Code),
                ct);

            if (account.Standing != ClientStatus.Prospect)
            {
                await clients.MoveToAsync(client.Id, account.Standing, ct);
            }

            await contacts.AddAsync(
                client.Id,
                account.Contact,
                "Operations lead",
                Marker.Email(account.Code),
                isMain: true,
                cancellationToken: ct);

            taken.Add(client.Id);
        }

        Said($"{taken.Count} clients, one of them still a prospect");

        return new Clients(taken);
    }

    // ---------------------------------------------------------------- the projects -----------

    private async Task<Projects> TheProjectsAsync(
        Staff people, Clients clients, DateOnly start, DateOnly today, CancellationToken ct)
    {
        var work = Get<WorkService>();
        var begun = new List<Guid>();

        var planned = new (string Name, string Code, int Client, int Lead, int MonthsIn, long Budget)[]
        {
            ("Freight tracking portal", "freight-portal", 0, 6, 2, 4_800_000_00L),
            ("Dairy collection app", "dairy-app", 1, 6, 4, 2_400_000_00L),
            ("Loan book migration", "loan-book", 2, 1, 8, 6_000_000_00L),
        };

        foreach (var one in planned)
        {
            clock.MoveTo(start.AddMonths(one.MonthsIn));

            var project = await work.BeginProjectAsync(
                one.Name,
                Marker.Code(one.Code),
                dueOn: start.AddMonths(one.MonthsIn + 9),
                cancellationToken: ct);

            await work.ForClientAsync(project.Id, clients.Everybody[one.Client], ct);
            await work.LeadProjectAsync(project.Id, people.Everybody[one.Lead], ct);
            await work.BudgetAsync(project.Id, one.Budget, Marker.Currency, ct);
            await work.ActivateProjectAsync(project.Id, ct);

            begun.Add(project.Id);
        }

        /*
         * And one that is the firm's own, with no client. A repository for a shared library
         * legitimately belongs to nobody, and so does the work on the ERP itself — a demonstration
         * where every project bills somebody hides the case the reporting screens have to handle.
         */
        clock.MoveTo(start.AddMonths(6));

        var internalWork = await work.BeginProjectAsync(
            "Our own systems", Marker.Code("our-systems"), cancellationToken: ct);

        await work.LeadProjectAsync(internalWork.Id, people.Everybody[2], ct);
        await work.ActivateProjectAsync(internalWork.Id, ct);
        begun.Add(internalWork.Id);

        Said($"{begun.Count} projects, three for clients and one the firm's own");

        return new Projects(begun);
    }

    // ---------------------------------------------------------------- the repositories -------

    private async Task<Repositories> TheRepositoriesAsync(
        Projects projects, DateOnly start, CancellationToken ct)
    {
        clock.MoveTo(start.AddMonths(3));

        var engineering = Get<EngineeringService>();
        var connected = new List<Guid>();

        /*
         * Two hosts rather than two repositories on one, because the whole point of section 39's
         * four adapters is that a firm can watch more than one — and until section 51 the screen
         * could only reach GitHub, so a demonstration showing one host would be showing the bug.
         *
         * The secret is the one this tool configures for every host. Connecting refuses a mismatch
         * in fixed time, which is correct and is why the tool has to supply configuration at all.
         */
        foreach (var (host, name, project) in new[]
        {
            (GitProvider.GitHub, "freight-portal", 0),
            (GitProvider.GitLab, "dairy-app", 1),
        })
        {
            var repository = await engineering.ConnectAsync(
                host,
                "jiranisokotech",
                Marker.Code(name),
                Marker.GitSecret,
                projects.Everybody[project],
                ct);

            connected.Add(repository.Id);
        }

        Said($"{connected.Count} repositories, on two different hosts");

        return new Repositories(connected);
    }

    // ---------------------------------------------------------------- the work ---------------

    private async Task<int> TheWorkAsync(
        Staff people, Projects projects, DateOnly start, DateOnly today, CancellationToken ct)
    {
        var work = Get<WorkService>();
        var planning = Get<PlanningService>();
        var hours = Get<TimesheetService>();

        var titles = new[]
        {
            "Vehicle list is slow with more than two hundred lorries",
            "Driver cannot sign in on the depot tablet",
            "Consignment numbers repeat after a restart",
            "Collection route does not save its last stop",
            "Milk volumes round to the wrong litre",
            "Farmer statement shows last month's total",
            "Import the old loan ledger",
            "Reconcile balances against the bank export",
            "Arrears report takes four minutes",
            "Add two-factor sign-in for the office",
            "Nightly backup is not verified",
            "Upgrade the container host",
        };

        var raised = 0;
        var items = new List<Guid>();

        for (var i = 0; i < titles.Length; i++)
        {
            clock.MoveTo(start.AddMonths(6 + (i / 3)));

            var item = await work.RaiseAsync(
                titles[i],
                people.Everybody[i % 3 == 0 ? 1 : 2],
                projects.Everybody[i / 3],
                people.Everybody[3 + (i % 4)],
                i % 5 == 0 ? Priority.High : Priority.Normal,
                i % 4 == 0 ? WorkItemKind.Feature : WorkItemKind.Task,
                ct);

            items.Add(item.Id);
            raised++;

            /*
             * Moved to different places on purpose, and the proportions matter more than the
             * numbers. A board that is all Todo has no history and a board that is all Done has no
             * present; the interesting screens — the board's columns, a burndown, "what is
             * blocked" — need both, and one blocked item because that column is the one nobody
             * looks at until it is empty for the wrong reason.
             */
            clock.Advance(TimeSpan.FromDays(2));

            if (i % 6 == 5)
            {
                await work.MoveAsync(item.Id, WorkItemStatus.Blocked, "Waiting on the client", ct);
            }
            else if (i < titles.Length - 4)
            {
                await work.MoveAsync(item.Id, WorkItemStatus.InProgress, cancellationToken: ct);

                if (i % 3 != 2)
                {
                    await work.MoveAsync(item.Id, WorkItemStatus.InReview, cancellationToken: ct);
                    await work.MoveAsync(item.Id, WorkItemStatus.Done, cancellationToken: ct);
                }
            }

            /*
             * Hours against the work, some approved and some not, because the unapproved figure is
             * on the dashboard and the billable one is what an invoice bills. Logged on the day the
             * work moved rather than today, so the week view and the reporting months have shape.
             */
            for (var day = 0; day < 3; day++)
            {
                var entry = await hours.LogAsync(
                    people.Everybody[3 + (i % 4)],
                    clock.Today.AddDays(-day),
                    120 + (day * 30),
                    item.Id,
                    projects.Everybody[i / 3],
                    billable: i / 3 < 3,
                    cancellationToken: ct);

                if (day > 0)
                {
                    await hours.ApproveAsync(entry.Id, people.Everybody[1], ct);
                }
            }
        }

        /*
         * Four sprints, the last of them running. StartSprintAsync refuses a second running one,
         * which is the rule that makes this loop the shape it is: each is started and finished in
         * turn, and only the last is left open.
         */
        var sprintStart = today.AddDays(-56);

        for (var s = 0; s < 4; s++)
        {
            clock.MoveTo(sprintStart.AddDays(s * 14));

            var sprint = await planning.PlanAsync(
                $"Sprint {s + 1}",
                sprintStart.AddDays(s * 14),
                sprintStart.AddDays((s * 14) + 13),
                s == 3 ? "Finish the arrears report" : null,
                ct);

            await planning.StartSprintAsync(sprint.Id, ct);

            foreach (var item in items.Skip(s * 3).Take(3))
            {
                try
                {
                    await planning.ScheduleAsync(item, sprint.Id, ct);
                }
                catch (InvalidOperationException)
                {
                    /*
                     * Scheduling refuses finished work, and some of these are finished — which is
                     * the honest shape of a real sprint board and not something to work around by
                     * leaving the work unfinished. Swallowed here, and only here, because the
                     * refusal is the expected answer rather than a failure of the seed.
                     */
                }
            }

            if (s < 3)
            {
                await planning.FinishSprintAsync(sprint.Id, ct);
            }
        }

        Said($"{raised} pieces of work across four sprints, with hours logged against them");

        return raised;
    }

    // ---------------------------------------------------------------- the money --------------

    private async Task<int> TheInvoicesAsync(
        Clients clients, Projects projects, DateOnly today, CancellationToken ct)
    {
        var invoices = Get<InvoiceService>();
        var written = 0;

        /*
         * Five invoices in four states, and the states are the point. The ageing report has
         * buckets, the dashboard has an oldest-overdue and an outstanding total, and the reminder
         * ladder has rungs — all of which need an invoice that is late, one that is nearly late,
         * one part-paid and one settled. A demonstration where everything is paid shows a firm with
         * no receivables and a page with nothing on it.
         */
        var shape = new (int Client, int Project, int DaysAgo, string What, long Unit, int Quantity, long Paid)[]
        {
            (0, 0, 120, "Freight portal — discovery and design", 450_000_00L, 1, 450_000_00L),
            (0, 0, 75, "Freight portal — first delivery", 1_200_000_00L, 1, 1_200_000_00L),
            (1, 1, 62, "Dairy app — collection module", 780_000_00L, 1, 0L),
            (2, 2, 20, "Loan book migration — stage one", 1_500_000_00L, 1, 500_000_00L),
            (2, 2, 0, "Loan book migration — stage two", 1_500_000_00L, 1, 0L),
        };

        foreach (var one in shape)
        {
            clock.MoveTo(today.AddDays(-one.DaysAgo));

            var invoice = await invoices.DraftAsync(clients.Everybody[one.Client], Marker.Currency, ct);

            await invoices.BillForAsync(invoice.Id, projects.Everybody[one.Project], ct);

            await invoices.AddLineAsync(
                invoice.Id, one.What, one.Quantity, Money.Of(one.Unit, Marker.Currency), ct);

            written++;

            // The last one stays a draft, because a screen that can only show sent invoices hides
            // the one state somebody is in the middle of.
            if (one.DaysAgo == 0)
            {
                continue;
            }

            await invoices.SendAsync(invoice.Id, ct);

            if (one.Paid > 0)
            {
                clock.MoveTo(today.AddDays(-one.DaysAgo + 14));

                await invoices.RecordPaymentAsync(
                    invoice.Id,
                    Money.Of(one.Paid, Marker.Currency),
                    clock.Today,
                    Marker.Code("mpesa-" + written),
                    ct);
            }
        }

        Said($"{written} invoices: settled, part-paid, overdue and one still a draft");

        return written;
    }

    // ---------------------------------------------------------------- hiring -----------------

    private async Task<int> TheHiringAsync(
        Staff people, Firm firm, DateOnly today, CancellationToken ct)
    {
        clock.MoveTo(today.AddDays(-45));

        var recruitment = Get<RecruitmentService>();

        var requisition = await recruitment.RaiseRequisitionAsync(
            "Engineer",
            firm.Departments[1],
            2,
            "Two clients have work waiting and the team is at capacity.",
            people.Everybody[2],
            ct);

        await recruitment.SubmitAsync(requisition.Id, ct);

        /*
         * Approved by recording the decision rather than by walking the approval chain, and it is
         * a deliberate shortcut with a reason. Submitting opens a chain up the reporting line, and
         * the line here ends at the managing director — so a seed that answered every step would
         * be answering as somebody it also invented, which is the one thing about this data that
         * would be a lie rather than a simplification. Publishing refuses unless the requisition
         * is approved, so the record still has to be honest about the state.
         */
        await recruitment.RecordDecisionAsync(requisition.Id, true, "Agreed at the Monday meeting.", ct);

        clock.MoveTo(today.AddDays(-38));

        var posting = await recruitment.DraftPostingAsync(
            requisition.Id,
            "Engineer",
            "Build the things this firm's clients run on.",
            "You will work on client delivery in a team of five, mostly in .NET and PostgreSQL, "
            + "with a say in what gets built and how.",
            "Nairobi, or remote in East Africa",
            Marker.Code("engineer"),
            ct);

        await recruitment.PublishAsync(posting.Id, ct);

        var applicants = new[]
        {
            "Naomi Chebet", "Victor Omondi", "Sarah Wairimu",
            "Collins Barasa", "Mercy Atieno", "Tom Kilonzo",
        };

        for (var i = 0; i < applicants.Length; i++)
        {
            clock.MoveTo(today.AddDays(-36 + (i * 3)));

            await recruitment.ApplyAsync(
                posting.Id,
                applicants[i],
                Marker.Email(applicants[i].Split(' ')[0].ToLowerInvariant()),
                note: "Applied through the careers page.",
                cancellationToken: ct);
        }

        Said($"a job advert with {applicants.Length} applications against it");

        return applicants.Length;
    }

    // ---------------------------------------------------------------- the equipment ----------

    private async Task<int> TheAssetsAsync(Staff people, DateOnly start, CancellationToken ct)
    {
        var assets = Get<AssetService>();
        var bought = 0;

        for (var i = 0; i < 8; i++)
        {
            clock.MoveTo(start.AddMonths(i));

            var asset = await assets.BuyAsync(
                Marker.Code($"asset-{i + 1:00}").ToUpperInvariant(),
                i < 5 ? AssetKind.Laptop : i < 7 ? AssetKind.Phone : AssetKind.Monitor,
                i < 5 ? "ThinkPad T14" : i < 7 ? "Pixel 7a" : "Dell 27 inch",
                start.AddMonths(i),
                cost: Money.Of(i < 5 ? 145_000_00L : i < 7 ? 38_000_00L : 32_000_00L, Marker.Currency),
                cancellationToken: ct);

            bought++;

            /*
             * Six of the eight issued, so the register shows both halves: what somebody has and
             * what is in the cupboard. A register where everything is out is one nobody can use to
             * find a spare laptop.
             */
            if (i < 6)
            {
                await assets.IssueAsync(
                    asset.Id, people.Everybody[i + 2], start.AddMonths(i), "New starter", ct);
            }
        }

        Said($"{bought} pieces of equipment, six of them issued");

        return bought;
    }

    // ---------------------------------------------------------------- what is running --------

    private async Task<Estate> TheEstateAsync(
        Staff people, Repositories repositories, DateOnly today, CancellationToken ct)
    {
        clock.MoveTo(today.AddMonths(-12));

        var estate = Get<EstateService>();
        var services = new List<Guid>();

        foreach (var (name, about, matters, repository) in new[]
        {
            ("Freight portal", "What Mombasa Freight's depots sign in to.", HowCritical.Critical, 0),
            ("Dairy collection API", "What the collection app talks to.", HowCritical.Important, 1),
            ("Internal ERP", "This.", HowCritical.Important, -1),
        })
        {
            var service = await estate.AddServiceAsync(
                name,
                about,
                matters,
                people.Everybody[2],
                repository >= 0 ? repositories.Everybody[repository] : null,
                ct);

            services.Add(service.Id);
        }

        var resources = 0;

        /*
         * A certificate that expires in three weeks, which is the whole reason section 14 has a
         * scheduled job. A demonstration estate where nothing is near its expiry date shows a
         * register with no purpose.
         */
        foreach (var (name, kind, where, provider, service, expires) in new[]
        {
            ("freight-web-01", ResourceKind.Server, DeploymentEnvironment.Production, "Hetzner", 0, (DateOnly?)null),
            ("freight-db-01", ResourceKind.Database, DeploymentEnvironment.Production, "Hetzner", 0, null),
            ("freight-web-staging", ResourceKind.Server, DeploymentEnvironment.Staging, "Hetzner", 0, null),
            ("dairy-api-01", ResourceKind.ContainerHost, DeploymentEnvironment.Production, "DigitalOcean", 1, null),
            ("jiranisokotech.co.ke", ResourceKind.Domain, DeploymentEnvironment.Production, "Safaricom", 2, today.AddMonths(7)),
            ("*.jiranisokotech.co.ke", ResourceKind.Certificate, DeploymentEnvironment.Production, "Let's Encrypt", 2, today.AddDays(21)),
        })
        {
            await estate.RecordAsync(
                Marker.Code(name), kind, where, provider, services[service], expires, ct);

            resources++;
        }

        Said($"{services.Count} services and {resources} resources, one certificate expiring soon");

        return new Estate(services, resources);
    }

    // ---------------------------------------------------------------- what went wrong --------

    private async Task<int> TheIncidentsAsync(
        Staff people, Estate estate, DateOnly today, CancellationToken ct)
    {
        var incidents = Get<IncidentService>();
        var raised = 0;

        /*
         * Two that are over and one that is not, because every number on the incidents page is a
         * subtraction between two timestamps — how long it was wrong before anybody knew, how long
         * it then lasted — and one open incident is what makes the count in the navigation a
         * number rather than a blank.
         */
        var history = new (string What, IncidentSeverity How, int DaysAgo, int StartedHoursBefore, int MitigatedAfter, int? ResolvedAfter)[]
        {
            ("Depot sign-in refused every driver", IncidentSeverity.Critical, 96, 2, 3, 26),
            ("Farmer statements showed the wrong month", IncidentSeverity.Major, 40, 9, 5, 30),
            ("Arrears report times out", IncidentSeverity.Minor, 2, 4, 0, null),
        };

        foreach (var one in history)
        {
            clock.MoveTo(today.AddDays(-one.DaysAgo));

            var noticed = clock.Now;

            var incident = await incidents.ReportAsync(
                one.What,
                one.How,
                noticed.AddHours(-one.StartedHoursBefore),
                people.Everybody[3],
                serviceId: estate.Services[raised % estate.Services.Count],
                cancellationToken: ct);

            await incidents.LeadAsync(incident.Id, people.Everybody[2], people.Everybody[3], ct);

            raised++;

            if (one.MitigatedAfter > 0)
            {
                await incidents.MitigateAsync(
                    incident.Id,
                    "Rolled back the release and turned the flag off.",
                    noticed.AddHours(one.MitigatedAfter),
                    people.Everybody[2],
                    ct);
            }

            if (one.ResolvedAfter is { } after)
            {
                await incidents.ResolveAsync(
                    incident.Id,
                    "A cache key that did not include the month, so the first reader of the day "
                    + "decided what everybody else saw.",
                    noticed.AddHours(after),
                    people.Everybody[2],
                    ct);
            }
        }

        Said($"{raised} incidents, one of them still open");

        return raised;
    }

    // ---------------------------------------------------------------- the help desk ----------

    private async Task<int> TheHelpDeskAsync(
        Staff people, Clients clients, DateOnly today, CancellationToken ct)
    {
        var support = Get<SupportService>();
        var raised = 0;

        /*
         * Not in the brief's list for section 83, and here anyway. Sections 25 and 26 were the last
         * two modules built, so a seeded database without them is one where somebody opens the help
         * desk, finds nothing, and concludes the module is unbuilt — which is exactly the
         * impression section 82 exists to prevent. Four tickets: one late and unanswered, so the
         * count in the navigation is a number; one waiting on the client; one settled; one from a
         * colleague, because an internal request is half of what this desk is for.
         */
        var asks = new (string Subject, string Said, TicketPriority How, int HoursAgo, bool Answer, bool Settle)[]
        {
            ("Depot tablet will not accept the driver PIN", "It has said the PIN is wrong since this morning.",
                TicketPriority.Blocking, 9, false, false),
            ("Can we export the collection list to Excel?", "Every Friday somebody retypes it.",
                TicketPriority.Asking, 30, true, false),
            ("Statement totals looked wrong last month", "They are right again now but we want to know why.",
                TicketPriority.Slowing, 200, true, true),
            ("My laptop will not wake from sleep", "It needs holding down every morning.",
                TicketPriority.Slowing, 50, true, false),
        };

        for (var i = 0; i < asks.Length; i++)
        {
            var one = asks[i];

            clock.MoveTo(today).Advance(TimeSpan.FromHours(-one.HoursAgo));

            var inside = i == asks.Length - 1;

            var ticket = await support.RaiseAsync(
                one.Subject,
                one.Said,
                one.How,
                inside ? Requester.Colleague : Requester.ClientContact,
                inside ? people.Everybody[7] : await AContactAsync(clients.Everybody[i % 3], ct),
                inside ? null : clients.Everybody[i % 3],
                ct);

            raised++;

            await support.AssignAsync(ticket.Id, people.Everybody[9], ct);

            if (!one.Answer)
            {
                continue;
            }

            clock.Advance(TimeSpan.FromHours(2));

            if (one.Settle)
            {
                await support.ResolveAsync(
                    ticket.Id,
                    "A cache that did not know about the month. Fixed and released on Tuesday.",
                    people.Everybody[9],
                    ct);
            }
            else
            {
                await support.ReplyAsync(
                    ticket.Id, "Looking at it now — which depot is it?", people.Everybody[9],
                    waitingOnThem: true, cancellationToken: ct);
            }
        }

        Said($"{raised} help desk tickets, one of them already past its promise");

        return raised;
    }

    // ---------------------------------------------------------------- what the firm knows ----

    private async Task<int> TheKnowledgeAsync(Staff people, DateOnly today, CancellationToken ct)
    {
        clock.MoveTo(today.AddMonths(-5));

        var knowledge = Get<KnowledgeService>();
        var written = 0;

        var articles = new (string Title, string Summary, string Body, string Labels, int ReviewInMonths)[]
        {
            ("Deploying by hand",
                "The steps to follow when the pipeline is unavailable.",
                "Take the last green build, copy it across, and run the migrations before you "
                + "switch the proxy over. Check the health endpoint answers before you tell "
                + "anybody.",
                "deployment, runbooks", 7),
            ("Taking a support call",
                "What to write down while somebody is still on the telephone.",
                "Their words, not your summary. The reference goes back to them before they hang "
                + "up. An internal note is not an answer and does not stop their clock.",
                "support, onboarding", -1),
        };

        foreach (var one in articles)
        {
            var article = await knowledge.StartAsync(
                one.Title, one.Summary, one.Body, people.Everybody[2], one.Labels, ct);

            /*
             * One of them is overdue a check, which is the whole point of section 25 and the one
             * thing its page counts. A knowledge base where everything is within its review date
             * shows a screen with nothing to act on.
             */
            await knowledge.PublishAsync(
                article.Id,
                people.Everybody[2],
                one.ReviewInMonths > 0
                    ? today.AddMonths(one.ReviewInMonths)
                    : today.AddDays(-20),
                "First version.",
                ct);

            written++;
        }

        Said($"{written} articles, one of them overdue a check");

        return written;
    }

    // ---------------------------------------------------------------- the sign on the door ---

    private async Task TheNoticeAsync(Staff people, DateOnly today, CancellationToken ct)
    {
        clock.MoveTo(today);

        var announcements = Get<AnnouncementService>();

        /*
         * The marker somebody actually reads. Four of the five markers are in values a person has
         * to be looking at the right column to notice; this one is on the notice board, at the top,
         * on the first screen anybody opens.
         */
        var notice = await announcements.WriteAsync(
            "This is demonstration data",
            "Every client, project, invoice, person and ticket in this copy was written by the "
            + "demonstration seed. Nothing here is real, nobody named here works here, and the "
            + "firm's own trading name says so on every invoice. Do not use any figure on any "
            + "screen for anything.",
            people.Everybody[0],
            needsAcknowledgement: false,
            cancellationToken: ct);

        await announcements.PostAsync(notice.Id, ct);

        Said("an announcement saying what this data is");
    }

    // ---------------------------------------------------------------- the plumbing -----------

    /// <summary>
    /// The main contact on a client, which the help desk needs and only the database knows.
    /// </summary>
    /// <remarks>
    /// Read back rather than remembered from when it was written, because a ticket's requester is
    /// a contact's identifier and <c>ContactService.AddAsync</c> is called inside a loop that keeps
    /// only the client. Reading it is one query and cannot disagree with the row.
    /// </remarks>
    private async Task<Guid> AContactAsync(Guid client, CancellationToken ct)
    {
        var database = Get<Infrastructure.Persistence.AppDbContext>();

        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(
                database.Contacts
                    .Where(one => one.ClientId == client)
                    .Select(one => one.Id),
                ct);
    }

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private void Said(string what)
    {
        _steps.Add(what);
        say.WriteLine("  wrote " + what);
    }

    private sealed record Firm(List<Guid> Departments);

    private sealed record Staff(List<Guid> Everybody);

    private sealed record Clients(List<Guid> Everybody);

    private sealed record Projects(List<Guid> Everybody);

    private sealed record Repositories(List<Guid> Everybody);

    private sealed record Estate(List<Guid> Services, int Resources);
}
