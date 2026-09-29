using System.Text.Json;
using System.Text.Json.Serialization;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Search;
using JiranisokoTech.Infrastructure.Work;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Ai;

/// <summary>What a lookup returned to the model, and whether it was refused.</summary>
/// <param name="Proposal">A task the model has proposed, for the person to confirm or not.</param>
public sealed record ToolOutcome(string Content, bool Refused, TaskProposal? Proposal = null);

/// <summary>
/// A task the assistant suggests. Nothing has been created; the person presses a button or does not.
/// </summary>
public sealed record TaskProposal(string Title, Guid? ProjectId, string? ProjectName);

/// <summary>
/// The lookups the assistant may ask for, each made as the person asking.
/// </summary>
/// <remarks>
/// This is where section 36's rule — a user must never receive information they are not
/// authorised to access — is enforced, and the way it is enforced is by not having a second set
/// of rules. Each lookup checks the permission the page showing the same records checks, narrows
/// by the same reach, and then calls the same query the page calls. A lookup the person could not
/// make on a screen is refused here before the query runs, and the refusal is what the model is
/// sent: it never sees the records and so cannot repeat them, however it is asked.
///
/// The model chooses which lookups to make and with what arguments. It cannot choose who they are
/// made as — the <see cref="Asker"/> comes from the signed-in principal, not from anything the
/// model wrote — and it cannot name a record by identifier, only by the name or code a person
/// would use, resolved within what that person may see. A project out of reach and a project that
/// does not exist get the same answer, so asking cannot be used to discover what exists.
///
/// Every lookup reads; none writes. The one that sounds like a write, <c>propose_task</c>,
/// records nothing: it hands the page a proposal, and the person decides whether to press the
/// button that creates it — section 36's "require confirmation", applied to everything rather
/// than only to what is destructive, because the assistant acting on somebody's behalf without
/// their say is the thing a person least expects of a question box.
/// </remarks>
public sealed class AssistantTools(
    SearchQueries search,
    ProjectFacts facts,
    WorkQueries work,
    BusinessQueries business,
    AppDbContext database,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Most rows any one lookup returns, so one question cannot send a whole table.</summary>
    private const int MostRows = 50;

    public IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new("search",
            "Find people, clients, projects, work items, invoices, candidates and documents by name, "
            + "code or number. Returns only what the person asking may see.",
            """{"type":"object","properties":{"term":{"type":"string","description":"Two or more characters to look for."}},"required":["term"],"additionalProperties":false}"""),

        new("list_projects",
            "List the projects the person asking may see, with status, due date, lead and how much "
            + "work is open.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),

        new("project",
            "Everything recorded about one project that the person may see: its work items, open "
            + "pull requests, recent builds and deployments, hours and money, plus figures calculated "
            + "from them and a list of what was withheld from this person and why.",
            """{"type":"object","properties":{"project":{"type":"string","description":"The project's name or code."}},"required":["project"],"additionalProperties":false}"""),

        new("work_items",
            "Work items: the person's own, or everybody's if they may see the whole board. Can be "
            + "narrowed to open, overdue or blocked items.",
            """{"type":"object","properties":{"whose":{"type":"string","enum":["mine","everyone"]},"filter":{"type":"string","enum":["open","overdue","blocked","all"]}},"required":["whose","filter"],"additionalProperties":false}"""),

        new("invoices",
            "Invoices with what is paid and outstanding and whether each is overdue. Can be narrowed "
            + "to unpaid ones and to one client.",
            """{"type":"object","properties":{"unpaid_only":{"type":"boolean"},"client":{"type":"string","description":"A client's name or code, or empty for every client."}},"required":["unpaid_only","client"],"additionalProperties":false}"""),

        new("client",
            "One client: status, main contact, payment terms, what they owe, their projects the "
            + "person may see, and their recent invoices if the person may see invoices.",
            """{"type":"object","properties":{"client":{"type":"string","description":"The client's name or code."}},"required":["client"],"additionalProperties":false}"""),

        new("deliveries",
            "Recent builds and deployments with their outcome and a link to the log, across every "
            + "repository or for one project's repositories.",
            """{"type":"object","properties":{"project":{"type":"string","description":"A project's name or code, or empty for every repository."},"failed_only":{"type":"boolean"}},"required":["project","failed_only"],"additionalProperties":false}"""),

        new("propose_task",
            "Propose a work item for the person to create. This creates nothing: the person is shown "
            + "the proposal and decides. Use it only when they ask for a task to be created.",
            """{"type":"object","properties":{"title":{"type":"string","description":"A short title, as a person would write it."},"project":{"type":"string","description":"The project's name or code, or empty for none."}},"required":["title","project"],"additionalProperties":false}"""),
    ];

    /// <summary>How each lookup reads on the page and in the usage log.</summary>
    public static string Describe(string name) => name switch
    {
        "search" => "Searched",
        "list_projects" => "Listed projects",
        "project" => "Read a project",
        "work_items" => "Read work items",
        "invoices" => "Read invoices",
        "client" => "Read a client",
        "deliveries" => "Read builds and deployments",
        "propose_task" => "Proposed a task",
        _ => $"Asked for \"{name}\", which does not exist",
    };

    public async Task<ToolOutcome> RunAsync(
        string name, string input, Asker asker, CancellationToken cancellationToken = default)
    {
        JsonElement arguments;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ToolOutcome("The arguments were not valid JSON.", Refused: false);
        }

        return name switch
        {
            "search" => await SearchAsync(Text(arguments, "term"), asker, cancellationToken),
            "list_projects" => await ProjectsAsync(asker, cancellationToken),
            "project" => await ProjectAsync(Text(arguments, "project"), asker, cancellationToken),
            "work_items" => await WorkAsync(Text(arguments, "whose"), Text(arguments, "filter"), asker, cancellationToken),
            "invoices" => await InvoicesAsync(Flag(arguments, "unpaid_only"), Text(arguments, "client"), asker, cancellationToken),
            "client" => await ClientAsync(Text(arguments, "client"), asker, cancellationToken),
            "deliveries" => await DeliveriesAsync(Text(arguments, "project"), Flag(arguments, "failed_only"), asker, cancellationToken),
            "propose_task" => await ProposeAsync(Text(arguments, "title"), Text(arguments, "project"), asker, cancellationToken),
            _ => new ToolOutcome($"There is no lookup called \"{name}\".", Refused: false),
        };
    }

    /// <summary>
    /// The refusal the model is sent in place of the records.
    /// </summary>
    /// <remarks>
    /// It names the permission rather than apologising vaguely, so the answer the person reads can
    /// say exactly why it could not tell them — and so the model does not go looking for the same
    /// records by another route and then guess.
    /// </remarks>
    private static ToolOutcome Refuse(string what, string permission) => new(
        $"Refused: the person asking does not hold {permission}, so {what} cannot be looked up for "
        + "them. Tell them so plainly. Do not guess or estimate what it would have shown.",
        Refused: true);

    // --- the lookups ---------------------------------------------------------

    /// <remarks>
    /// No permission of its own, like the search page: the query checks each group against the
    /// person's permissions and reach before it runs, and a group they may not see is not queried
    /// at all.
    /// </remarks>
    private async Task<ToolOutcome> SearchAsync(string term, Asker asker, CancellationToken cancellationToken)
    {
        var found = await search.FindAsync(term, asker.Permissions, asker.EmployeeId, cancellationToken);

        return Answer(found.Select(result => new { result.Kind, result.Title, result.Detail }));
    }

    private async Task<ToolOutcome> ProjectsAsync(Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.ProjectsViewAll) && !asker.Holds(Permissions.ProjectsViewMember))
        {
            return Refuse("projects", Permissions.ProjectsViewMember);
        }

        var today = clock.Today;

        return Answer((await facts.WithinReachAsync(asker, cancellationToken)).Select(project => new
        {
            project.Name,
            project.Code,
            project.Status,
            project.DueOn,
            PastDue = project.DueOn < today && project.Status is ProjectStatus.Active or ProjectStatus.Planned,
            Lead = project.LeadName,
            project.OpenItems,
            project.TotalItems,
        }));
    }

    private async Task<ToolOutcome> ProjectAsync(string named, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.ProjectsViewAll) && !asker.Holds(Permissions.ProjectsViewMember))
        {
            return Refuse("projects", Permissions.ProjectsViewMember);
        }

        if (await ProjectNamedAsync(named, asker, cancellationToken) is not { } project)
        {
            return NoSuchProject(named);
        }

        var picture = await facts.GatherAsync(project.Id, asker, cancellationToken);

        return picture is null
            ? NoSuchProject(named)
            : Answer(new
            {
                Facts = picture with { Work = [.. picture.Work.Take(ProjectFacts.MostWorkSent)] },
                Calculated = picture.Calculations(),
            });
    }

    /// <remarks>
    /// The board's own rule: tasks.view_all for everybody's work, and otherwise the person's own —
    /// and "own" needs a staff record, because an account with none has no work to call its own.
    /// </remarks>
    private async Task<ToolOutcome> WorkAsync(string whose, string filter, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.TasksViewOwn) && !asker.Holds(Permissions.TasksViewAll))
        {
            return Refuse("work items", Permissions.TasksViewOwn);
        }

        Guid? assignee;

        if (whose == "everyone")
        {
            if (!asker.Holds(Permissions.TasksViewAll))
            {
                return Refuse("other people's work", Permissions.TasksViewAll);
            }

            assignee = null;
        }
        else
        {
            if (asker.EmployeeId is not { } me)
            {
                return new ToolOutcome(
                    "The person asking has no staff record, so no work is theirs.", Refused: false);
            }

            assignee = me;
        }

        var today = clock.Today;
        var items = await work.ItemsAsync(
            assigneeId: assignee, openOnly: filter is "open" or "overdue" or "blocked",
            take: filter == "all" ? MostRows : null, cancellationToken: cancellationToken);

        var chosen = filter switch
        {
            "overdue" => items.Where(item => item.DueOn < today),
            "blocked" => items.Where(item => item.Status == WorkItemStatus.Blocked),
            _ => items,
        };

        return Answer(chosen.Take(MostRows).Select(item => new
        {
            item.Reference,
            item.Title,
            item.Status,
            item.Priority,
            Project = item.ProjectName,
            AssignedTo = item.AssigneeName,
            item.DueOn,
            BlockedBecause = item.BlockedReason,
        }));
    }

    private async Task<ToolOutcome> InvoicesAsync(bool unpaidOnly, string clientNamed, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.InvoicesView))
        {
            return Refuse("invoices", Permissions.InvoicesView);
        }

        Guid? clientId = null;

        if (!string.IsNullOrWhiteSpace(clientNamed))
        {
            // No clients.view needed to narrow by one: the invoices page names each invoice's
            // client to anybody who may open it, and this is the same list, filtered.
            if (await ClientNamedAsync(clientNamed, cancellationToken) is not { } client)
            {
                return new ToolOutcome($"No client matches \"{clientNamed}\".", Refused: false);
            }

            clientId = client.Id;
        }

        var today = clock.Today;
        List<InvoiceRow> rows;

        if (unpaidOnly)
        {
            rows =
            [
                .. await business.InvoicesAsync(clientId, InvoiceStatus.Sent, take: MostRows, cancellationToken: cancellationToken),
                .. await business.InvoicesAsync(clientId, InvoiceStatus.PartlyPaid, take: MostRows, cancellationToken: cancellationToken),
            ];
        }
        else
        {
            rows = await business.InvoicesAsync(clientId, take: MostRows, cancellationToken: cancellationToken);
        }

        return Answer(rows.Select(invoice => new
        {
            invoice.Number,
            Client = invoice.ClientName,
            invoice.Status,
            invoice.IssuedOn,
            invoice.DueOn,
            Total = invoice.Total.ToString(),
            Outstanding = invoice.Outstanding.ToString(),
            Overdue = invoice.IsOverdueOn(today),
        }));
    }

    private async Task<ToolOutcome> ClientAsync(string named, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.ClientsView))
        {
            return Refuse("clients", Permissions.ClientsView);
        }

        if (await ClientNamedAsync(named, cancellationToken) is not { } client)
        {
            return new ToolOutcome($"No client matches \"{named}\".", Refused: false);
        }

        var projects = (await facts.WithinReachAsync(asker, cancellationToken))
            .Where(project => project.ClientId == client.Id)
            .Select(project => new { project.Name, project.Code, project.Status, project.DueOn, project.OpenItems });

        var invoices = asker.Holds(Permissions.InvoicesView)
            ? (await business.InvoicesAsync(client.Id, take: 10, cancellationToken: cancellationToken))
                .Select(invoice => (object)new
                {
                    invoice.Number,
                    invoice.Status,
                    invoice.IssuedOn,
                    invoice.DueOn,
                    Outstanding = invoice.Outstanding.ToString(),
                    Overdue = invoice.IsOverdueOn(clock.Today),
                })
                .ToList()
            : null;

        return Answer(new
        {
            client.Name,
            client.Code,
            client.Status,
            Contact = client.ContactName,
            client.PaymentTermDays,
            Owed = client.Owed.Select(amount => amount.ToString()),
            Projects = projects,
            RecentInvoices = invoices,
            Withheld = invoices is null ? $"Invoices: the person does not hold {Permissions.InvoicesView}." : null,
        });
    }

    /// <remarks>
    /// Behind repos.view, the permission the repository and environment pages ask for. Narrowed to
    /// a project only within the person's reach, so naming a project they are not on returns the
    /// same "no such project" as naming one that does not exist.
    /// </remarks>
    private async Task<ToolOutcome> DeliveriesAsync(string projectNamed, bool failedOnly, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.ReposView))
        {
            return Refuse("builds and deployments", Permissions.ReposView);
        }

        var repositories = database.Repositories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(projectNamed))
        {
            if (await ProjectNamedAsync(projectNamed, asker, cancellationToken) is not { } project)
            {
                return NoSuchProject(projectNamed);
            }

            repositories = repositories.Where(repository => repository.ProjectId == project.Id);
        }

        var names = await repositories
            .Select(repository => new { repository.Id, Name = repository.Owner + "/" + repository.Name })
            .ToDictionaryAsync(repository => repository.Id, repository => repository.Name, cancellationToken);

        var ids = names.Keys.ToList();
        var since = clock.Now.AddDays(-ProjectFacts.WindowDays);

        var builds = await database.Builds
            .AsNoTracking()
            .Where(build => ids.Contains(build.RepositoryId) && build.StartedAt >= since
                && (!failedOnly || build.Outcome == BuildOutcome.Failed))
            .OrderByDescending(build => build.StartedAt)
            .Take(MostRows / 2)
            .Select(build => new { build.RepositoryId, build.Name, build.Branch, build.Outcome, build.StartedAt, build.Url })
            .ToListAsync(cancellationToken);

        var deployments = await database.Deployments
            .AsNoTracking()
            .Where(deployment => ids.Contains(deployment.RepositoryId) && deployment.At >= since
                && (!failedOnly || deployment.State == DeploymentState.Failed))
            .OrderByDescending(deployment => deployment.At)
            .Take(MostRows / 2)
            .Select(deployment => new { deployment.RepositoryId, deployment.EnvironmentName, deployment.State, deployment.Branch, deployment.DeployedBy, deployment.At, deployment.Url })
            .ToListAsync(cancellationToken);

        return Answer(new
        {
            WindowDays = ProjectFacts.WindowDays,
            Builds = builds.Select(build => new
            {
                Repository = names[build.RepositoryId], build.Name, build.Branch, build.Outcome, build.StartedAt, LogAt = build.Url,
            }),
            Deployments = deployments.Select(deployment => new
            {
                Repository = names[deployment.RepositoryId], Environment = deployment.EnvironmentName,
                deployment.State, deployment.Branch, deployment.DeployedBy, deployment.At, LogAt = deployment.Url,
            }),
            Note = "The logs themselves are on the code host and are not held here; the reason a "
                + "build failed is in the log at the link, not in these records.",
        });
    }

    private async Task<ToolOutcome> ProposeAsync(string title, string projectNamed, Asker asker, CancellationToken cancellationToken)
    {
        if (!asker.Holds(Permissions.TasksCreate))
        {
            return Refuse("creating work", Permissions.TasksCreate);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return new ToolOutcome("A task needs a title.", Refused: false);
        }

        ProjectRow? project = null;

        if (!string.IsNullOrWhiteSpace(projectNamed))
        {
            project = await ProjectNamedAsync(projectNamed, asker, cancellationToken);

            if (project is null)
            {
                return NoSuchProject(projectNamed);
            }
        }

        var clipped = title.Trim().Length > 200 ? title.Trim()[..200] : title.Trim();

        return new ToolOutcome(
            "Proposed, not created. The person will see a button to create it and decides whether to "
            + "press it. Tell them that; do not say the task exists.",
            Refused: false,
            new TaskProposal(clipped, project?.Id, project?.Name));
    }

    // --- resolving names -------------------------------------------------------

    /// <summary>
    /// A project by its code or name, among those the person may see and no others.
    /// </summary>
    private async Task<ProjectRow?> ProjectNamedAsync(string named, Asker asker, CancellationToken cancellationToken)
    {
        var wanted = (named ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            return null;
        }

        var visible = await facts.WithinReachAsync(asker, cancellationToken);

        return visible.FirstOrDefault(project => string.Equals(project.Code, wanted, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(project => string.Equals(project.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault(project => project.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ClientRow?> ClientNamedAsync(string named, CancellationToken cancellationToken)
    {
        var wanted = (named ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            return null;
        }

        var clients = await business.ClientsAsync(cancellationToken: cancellationToken);

        return clients.FirstOrDefault(client => string.Equals(client.Code, wanted, StringComparison.OrdinalIgnoreCase))
            ?? clients.FirstOrDefault(client => string.Equals(client.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? clients.FirstOrDefault(client => client.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// The same sentence for a project that does not exist and one the person is not on. Two
    /// sentences would let a question discover the names of projects somebody is not on.
    /// </remarks>
    private static ToolOutcome NoSuchProject(string named) =>
        new($"No project the person may see matches \"{named}\".", Refused: false);

    private static ToolOutcome Answer(object value) =>
        new(JsonSerializer.Serialize(value, Json), Refused: false);

    private static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;
}
