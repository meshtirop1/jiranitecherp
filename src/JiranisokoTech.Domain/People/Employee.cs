using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// Somebody who works here.
/// </summary>
/// <remarks>
/// Deliberately not the same thing as an account. An account is how a person
/// signs in and lives with ASP.NET Identity in the infrastructure; this is the
/// person the business has a relationship with, and it lives in the domain where
/// the rules about them can be tested without a database or a web server in the
/// room.
///
/// Keeping them apart costs one nullable column and buys three things the single
/// row cannot do: somebody can be on the payroll before their login exists;
/// service accounts and, later, client logins can exist without pretending to be
/// staff; and a person who leaves keeps their name on everything they did while
/// their account is closed the same afternoon.
///
/// The department and the line manager are separate facts on purpose. An
/// engineer can answer to a team lead rather than to the head of engineering,
/// and a system that derives one from the other cannot express that at all.
/// </remarks>
public sealed class Employee : Entity, IAuditable
{
    private Employee()
    {
        FullName = string.Empty;
    }

    private Employee(string fullName, DateOnly startsOn, Guid? departmentId, string? jobTitle)
    {
        FullName = Require(fullName);
        StartsOn = startsOn;
        DepartmentId = departmentId;
        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : jobTitle.Trim();
        Status = EmploymentStatus.Invited;

        Raise(new EmployeeHired(Id, FullName, departmentId, startsOn));
    }

    public static Employee Hire(
        string fullName,
        DateOnly startsOn,
        Guid? departmentId = null,
        string? jobTitle = null) =>
        new(fullName, startsOn, departmentId, jobTitle);

    public string FullName { get; private set; }

    public string? JobTitle { get; private set; }

    /// <summary>
    /// The account this person signs in with, if they have one.
    /// </summary>
    /// <remarks>
    /// Null is ordinary, not a gap waiting to be filled: somebody joining next
    /// month has a record before they have a login, and not every employee ever
    /// needs one.
    /// </remarks>
    public Guid? AccountId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    /// <summary>Who they answer to. Null at the top of the firm, and nowhere else.</summary>
    public Guid? ReportsToId { get; private set; }

    public EmploymentStatus Status { get; private set; }

    /// <summary>Their first day. In the future for somebody who has not started.</summary>
    public DateOnly StartsOn { get; private set; }

    public DateOnly? LeftOn { get; private set; }

    /// <summary>
    /// Hours a week this person is available for planned work.
    /// </summary>
    /// <remarks>
    /// Capacity, not a contract. It is what scheduling divides by, so it is kept
    /// here rather than inferred from a job title — a part-timer and a
    /// contractor are both normal and both wrong at forty.
    /// </remarks>
    public int WeeklyCapacityHours { get; private set; } = 40;

    /// <summary>Can be given work: here, started, and not stopped.</summary>
    public bool IsAssignable => Status == EmploymentStatus.Active;

    /// <summary>Still employed, whatever today looks like.</summary>
    public bool IsEmployed => Status is not EmploymentStatus.Left;

    public void Start()
    {
        if (Status != EmploymentStatus.Invited)
        {
            throw new InvalidOperationException(
                $"{FullName} cannot start: they are already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = EmploymentStatus.Active;

        Raise(new EmployeeStarted(Id, FullName));
    }

    /// <summary>
    /// They have left.
    /// </summary>
    /// <remarks>
    /// The record stays and the name stays on their work. Deleting a leaver
    /// turns every task, approval and audit entry they touched into "unknown
    /// user", which is how a system loses the half of its history that people
    /// actually read.
    /// </remarks>
    public void Leave(DateOnly on, string reason)
    {
        if (Status == EmploymentStatus.Left)
        {
            throw new InvalidOperationException($"{FullName} has already left.");
        }

        if (on < StartsOn)
        {
            throw new ArgumentException(
                $"{FullName} cannot leave on {on:d MMM yyyy}, before starting on "
                + $"{StartsOn:d MMM yyyy}.",
                nameof(on));
        }

        Status = EmploymentStatus.Left;
        LeftOn = on;

        Raise(new EmployeeLeft(Id, FullName, on, Require(reason, nameof(reason))));
    }

    public void Suspend(string reason)
    {
        if (Status != EmploymentStatus.Active)
        {
            throw new InvalidOperationException(
                $"Only somebody active can be suspended; {FullName} is "
                + $"{Status.ToString().ToLowerInvariant()}.");
        }

        Status = EmploymentStatus.Suspended;

        Raise(new EmployeeSuspended(Id, Require(reason, nameof(reason))));
    }

    /// <summary>
    /// Back at work, from suspension or from having left.
    /// </summary>
    /// <remarks>
    /// Re-hiring somebody who left is a real thing and is done on the record
    /// they already have, so that their history stays one person rather than
    /// two. The leaving date is cleared, because it is no longer true.
    /// </remarks>
    public void Reinstate()
    {
        if (Status is EmploymentStatus.Active or EmploymentStatus.Invited)
        {
            throw new InvalidOperationException(
                $"{FullName} is {Status.ToString().ToLowerInvariant()} and has nothing to be "
                + "reinstated from.");
        }

        Status = EmploymentStatus.Active;
        LeftOn = null;

        Raise(new EmployeeReinstated(Id));
    }

    public void MoveTo(Guid? departmentId)
    {
        if (DepartmentId == departmentId)
        {
            return;
        }

        var from = DepartmentId;
        DepartmentId = departmentId;

        Raise(new EmployeeMoved(Id, from, departmentId));
    }

    /// <summary>
    /// Set who this person answers to.
    /// </summary>
    /// <remarks>
    /// Reporting to themselves is refused here because it needs nothing but this
    /// object to detect. A longer loop — A to B to C to A — needs the rest of the
    /// graph and is refused by <see cref="ReportingLine"/>, which the caller
    /// consults first.
    /// </remarks>
    public void ReportsTo(Guid? managerId)
    {
        if (managerId == Id)
        {
            throw new InvalidOperationException($"{FullName} cannot report to themselves.");
        }

        if (ReportsToId == managerId)
        {
            return;
        }

        var from = ReportsToId;
        ReportsToId = managerId;

        Raise(new ReportingLineChanged(Id, from, managerId));
    }

    public void LinkAccount(Guid accountId)
    {
        if (AccountId == accountId)
        {
            return;
        }

        if (AccountId is not null)
        {
            throw new InvalidOperationException(
                $"{FullName} already has an account. Unlink the old one first, so that the "
                + "change is a deliberate act and not a typo.");
        }

        AccountId = accountId;

        Raise(new EmployeeAccountLinked(Id, accountId));
    }

    public void UnlinkAccount() => AccountId = null;

    public void Rename(string fullName) => FullName = Require(fullName);

    public void SetJobTitle(string? jobTitle) =>
        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : jobTitle.Trim();

    /// <summary>
    /// How many hours a week they are available.
    /// </summary>
    /// <remarks>
    /// Bounded by the number of hours in a week, which is the only limit this
    /// object can be sure of. A figure above it is always a typo, and scheduling
    /// would quietly plan around it.
    /// </remarks>
    public void SetWeeklyCapacity(int hours)
    {
        if (hours is < 0 or > 168)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hours), hours, "A week has 168 hours, and capacity cannot be negative.");
        }

        WeeklyCapacityHours = hours;
    }

    /// <summary>
    /// Nothing here is a secret, so nothing is withheld from the trail.
    /// </summary>
    /// <remarks>
    /// Named anyway rather than left to the default. An empty set says somebody
    /// thought about it; a missing member says nobody did, and the next person to
    /// add a national insurance number would have no prompt to reconsider.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter = "fullName") =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
