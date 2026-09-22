using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// A part of the firm, and the person answerable for it.
/// </summary>
/// <remarks>
/// The head is a fact about the department rather than a flag on the person,
/// because it is the department that can only have one of them. Recording it the
/// other way round — a "head" tick on an employee — makes two heads of
/// engineering a perfectly valid pair of rows.
/// </remarks>
public sealed class Department : Entity, IAuditable
{
    private Department()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    private Department(string name, Slug slug, string? description)
    {
        Name = Require(name, nameof(name));
        Slug = slug.Value;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        Raise(new DepartmentOpened(Id, Name, Slug));
    }

    public static Department Open(string name, string? slug = null, string? description = null) =>
        new(name, Common.Slug.From(slug ?? name), description);

    public string Name { get; private set; }

    /// <summary>
    /// The short name in addresses, fixed once the department exists.
    /// </summary>
    /// <remarks>
    /// Stored as text rather than the value object because that is what a column
    /// holds, and the type is recovered by <see cref="Handle"/> on the way out.
    /// It does not follow a rename: re-deriving it would break every link
    /// anybody had saved, for the sake of tidiness nobody asked for.
    /// </remarks>
    public string Slug { get; private init; }

    public Slug Handle => Common.Slug.FromStored(Slug);

    public string? Description { get; private set; }

    /// <summary>Who answers for this department. Null while the post is vacant.</summary>
    public Guid? HeadEmployeeId { get; private set; }

    /// <summary>
    /// Whether this department is still part of the firm.
    /// </summary>
    /// <remarks>
    /// Closed rather than deleted. Every task, requisition and approval that
    /// ever named it still names it, and a deleted row turns all of that into a
    /// dangling identifier.
    /// </remarks>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Put somebody in charge, or leave the post vacant.
    /// </summary>
    /// <remarks>
    /// This raises an event rather than only setting a column, because in the
    /// system this replaces, holding the post is what grants the department-head
    /// role and losing it is what takes the role away. Revoking a role has to end
    /// sessions and write an audit entry, and neither belongs inside a setter.
    /// </remarks>
    public void AppointHead(Guid? employeeId)
    {
        if (HeadEmployeeId == employeeId)
        {
            return;
        }

        if (!IsActive && employeeId is not null)
        {
            throw new InvalidOperationException(
                $"{Name} is closed. Reopen it before putting somebody in charge of it.");
        }

        var from = HeadEmployeeId;
        HeadEmployeeId = employeeId;

        Raise(new DepartmentHeadChanged(Id, from, employeeId));
    }

    public void Close()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;

        // The post goes with the department. Leaving somebody as head of
        // something that no longer exists is how a stale role outlives the
        // reason it was granted.
        if (HeadEmployeeId is not null)
        {
            var from = HeadEmployeeId;
            HeadEmployeeId = null;

            Raise(new DepartmentHeadChanged(Id, from, null));
        }

        Raise(new DepartmentClosed(Id));
    }

    public void Reopen()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;

        Raise(new DepartmentReopened(Id));
    }

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void Describe(string? description) =>
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    /// <summary>Nothing here is a secret.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
