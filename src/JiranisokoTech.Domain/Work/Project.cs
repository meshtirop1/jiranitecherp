using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Work;

/// <summary>Where a project has got to.</summary>
public enum ProjectStatus
{
    /// <summary>Agreed in principle. Work can be written down; nothing is running.</summary>
    Planned = 1,

    Active = 2,

    /// <summary>Paused by a decision — a client, a budget, a dependency.</summary>
    OnHold = 3,

    Delivered = 4,

    Cancelled = 5,
}

/// <summary>
/// A named body of work with an end.
/// </summary>
/// <remarks>
/// Work items can exist without one — the small jobs that keep a firm running
/// belong to nobody's project, and forcing a project on them produces a
/// "General" bucket that becomes the largest one in the system.
/// </remarks>
public sealed class Project : Entity, IAuditable
{
    private Project()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Project(string name, Slug code, Guid? departmentId, DateOnly? dueOn)
    {
        Name = Require(name, nameof(name));
        Code = code.Value;
        DepartmentId = departmentId;
        DueOn = dueOn;
        Status = ProjectStatus.Planned;

        Raise(new ProjectStarted(Id, Name, Code));
    }

    public static Project Begin(
        string name, string? code = null, Guid? departmentId = null, DateOnly? dueOn = null) =>
        new(name, Slug.From(code ?? name), departmentId, dueOn);

    public string Name { get; private set; }

    /// <summary>
    /// The short name people say and type. Fixed once it exists.
    /// </summary>
    /// <remarks>
    /// It ends up in addresses, commit messages and conversation, so re-deriving
    /// it from a rename would break every reference anybody had already made —
    /// including the ones outside this system.
    /// </remarks>
    public string Code { get; private init; }

    public string? Summary { get; private set; }

    public Guid? DepartmentId { get; private set; }

    /// <summary>The one person answerable for it.</summary>
    public Guid? LeadId { get; private set; }

    public Guid? ClientId { get; private set; }

    /// <summary>
    /// The contract this project is delivered under, when it is delivered under one.
    /// </summary>
    /// <remarks>
    /// <b>Section 70 names this as one of two missing links in the relationship model, and it is
    /// the one that costs the most.</b> A contract had a client and a project had a client, so the
    /// two were siblings and "which contract is this project delivered under" could only be
    /// answered by reading a client's list of contracts beside its list of projects and guessing.
    /// Sections 91 and 94 both stop at the same place: client to contract to project is the first
    /// hop of the delivery chain and of the finance chain, and it was two separate facts about a
    /// client.
    ///
    /// Nullable, because most of the firm's work predates any contract row and internal work is
    /// delivered under none. A project may be delivered under one contract rather than several:
    /// two contracts covering one project needs an apportioning rule for the money, and nobody
    /// has agreed one — the same reasoning <see cref="JiranisokoTech.Domain.Money.Invoice"/>
    /// already gives for its own project link.
    /// </remarks>
    public Guid? ContractId { get; private set; }

    /// <summary>
    /// The team delivering it, when one team is answerable for it.
    /// </summary>
    /// <remarks>
    /// <b>Section 91 names the hop this is missing: "project → team → developer is not a step
    /// anybody can take".</b> A team knew its members and nothing about what they were doing, so
    /// its page listed people and no work — which section 98 counts as one of the two navigation
    /// hops still broken, and §72 as the reason employee → project exists only as a project's
    /// lead.
    ///
    /// On the project rather than a join table between the two. A project is delivered by one
    /// team at a time in this firm; two teams on one project is a thing to say in the project's
    /// summary, not a structure to carry in the schema for a case nobody has yet. The lead stays
    /// separate and stays on the project, because the person answerable for a piece of work is
    /// not always the lead of the team doing it.
    /// </remarks>
    public Guid? TeamId { get; private set; }

    public ProjectStatus Status { get; private set; }

    public DateOnly? DueOn { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>
    /// What the project was agreed to cost, as minor units.
    /// </summary>
    /// <remarks>
    /// Stored as a number beside its currency, like a contract's value and a claim's
    /// amount, so a report can sum the column.
    ///
    /// A budget and not a forecast. It is the figure agreed with the client or set by the
    /// firm at the start, and it does not move as the work does — the whole use of it is
    /// comparing what was agreed against what happened, and a budget that drifted to
    /// match the spending would always be met.
    /// </remarks>
    public long? BudgetMinorUnits { get; private set; }

    public string? BudgetCurrency { get; private set; }

    /// <summary>The agreed budget, when both halves are present.</summary>
    public Common.Money? Budget =>
        BudgetMinorUnits is { } minor && BudgetCurrency is { Length: 3 } currency
            ? Common.Money.Of(minor, currency)
            : null;

    /// <summary>
    /// Set or clear what this was agreed to cost.
    /// </summary>
    /// <remarks>
    /// Refuses a negative budget, because a project cannot be agreed to earn the firm
    /// money by existing, and a negative here would make every margin calculation report
    /// a profit.
    /// </remarks>
    public void Budgeted(Common.Money? budget)
    {
        if (budget is { MinorUnits: < 0 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), "A budget cannot be less than nothing.");
        }

        BudgetMinorUnits = budget?.MinorUnits;
        BudgetCurrency = budget?.Currency;
    }

    public bool IsRunning => Status is ProjectStatus.Planned or ProjectStatus.Active
        or ProjectStatus.OnHold;

    public void Activate(DateTimeOffset at)
    {
        if (Status is ProjectStatus.Delivered or ProjectStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"{Name} is {Status.ToString().ToLowerInvariant()} and cannot be started again.");
        }

        if (Status == ProjectStatus.Active)
        {
            return;
        }

        Status = ProjectStatus.Active;
        DeliveredAt = null;

        Raise(new ProjectStatusChanged(Id, Name, ProjectStatus.Active, at));
    }

    public void Hold(string reason, DateTimeOffset at)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException($"{Name} is not running, so it cannot be paused.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // A project on hold with no reason is one nobody can restart,
            // because nobody remembers what it is waiting for.
            throw new ArgumentException("Say why it is paused.", nameof(reason));
        }

        Status = ProjectStatus.OnHold;

        Raise(new ProjectStatusChanged(Id, Name, ProjectStatus.OnHold, at, reason.Trim()));
    }

    /// <summary>
    /// Finished and handed over.
    /// </summary>
    /// <remarks>
    /// Whether any work is still open under it is not knowable from here, and is
    /// checked by the service that can count. A project delivered with six open
    /// items is either wrong or worth saying out loud.
    /// </remarks>
    public void Deliver(DateTimeOffset at)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(
                $"{Name} is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = ProjectStatus.Delivered;
        DeliveredAt = at;

        Raise(new ProjectStatusChanged(Id, Name, ProjectStatus.Delivered, at));
    }

    public void Cancel(string reason, DateTimeOffset at)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException(
                $"{Name} is already {Status.ToString().ToLowerInvariant()}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Say why it was cancelled.", nameof(reason));
        }

        Status = ProjectStatus.Cancelled;
        DeliveredAt = at;

        Raise(new ProjectStatusChanged(Id, Name, ProjectStatus.Cancelled, at, reason.Trim()));
    }

    public void LeadBy(Guid? employeeId)
    {
        if (LeadId == employeeId)
        {
            return;
        }

        var from = LeadId;
        LeadId = employeeId;

        Raise(new ProjectLeadChanged(Id, from, employeeId));
    }

    /// <summary>
    /// Move the project to a client, or to none.
    /// </summary>
    /// <remarks>
    /// Taking the client off takes the contract with it. A contract belongs to a client, so a
    /// project with no client and a contract would be claiming to be delivered under an agreement
    /// with somebody it is not for — and that is the shape of wrong answer a report repeats
    /// without anybody noticing, because each half reads correctly on its own.
    /// </remarks>
    public void ForClient(Guid? clientId)
    {
        ClientId = clientId;

        if (clientId is null)
        {
            ContractId = null;
        }
    }

    /// <summary>
    /// Say which contract this project is delivered under, or that it is under none.
    /// </summary>
    /// <remarks>
    /// Refuses a contract on a project that has no client, for the reason above. That the contract
    /// belongs to THIS client is checked in the service, which is the layer that can read one —
    /// the entity holds the identifier and cannot see the row behind it.
    /// </remarks>
    /// <summary>Say which team is delivering it, or that no team is named.</summary>
    /// <remarks>
    /// No rule beyond the team existing, which the service checks. A team may deliver work for
    /// any client or none, and a project may sensibly have no team named on it — that is every
    /// project in this database until somebody says otherwise.
    /// </remarks>
    public void DeliveredBy(Guid? teamId) => TeamId = teamId;

    public void DeliveredUnder(Guid? contractId)
    {
        if (contractId is not null && ClientId is null)
        {
            throw new ArgumentException(
                "Say which client this project is for before saying which of their contracts it "
                + "is delivered under.",
                nameof(contractId));
        }

        ContractId = contractId;
    }

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void Summarise(string? summary) =>
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();

    public void DueBy(DateOnly? on) => DueOn = on;

    public void PlaceIn(Guid? departmentId) => DepartmentId = departmentId;

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
