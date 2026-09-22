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

    public ProjectStatus Status { get; private set; }

    public DateOnly? DueOn { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

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

    public void ForClient(Guid? clientId) => ClientId = clientId;

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
