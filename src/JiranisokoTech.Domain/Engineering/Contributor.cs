using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Engineering;

/// <summary>
/// The name somebody commits under, and who that is.
/// </summary>
/// <remarks>
/// The missing link between the repositories and the people. A commit carries a
/// provider login — `meshtirop1`, or an Azure DevOps sign-in address — and this
/// system knows about employees. Nothing connects the two, and without a connection
/// the repositories can show what was built but never who built it in terms the rest
/// of the ERP understands.
///
/// It lives in Engineering rather than as a field on Employee, and that is the whole
/// design decision in this file. Putting a git handle on a person would mean People
/// referring to Engineering — a module that exists to record staff, leave and pay
/// taking a dependency on one that reads webhooks. It would also be wrong on its own
/// terms: one person has a GitHub login, possibly a GitLab one, and an Azure DevOps
/// address, and they are not the same string. A handle is a fact about a repository,
/// so it is recorded where the repositories are.
///
/// Deliberately not automatic. A login that looks like somebody's name is not
/// evidence that it is them, and the consequence of guessing wrong is a person's
/// timesheet showing work they did not do. Somebody claims a handle, and the claim is
/// in the audit trail.
/// </remarks>
public sealed class Contributor : Entity, IAuditable
{
    private Contributor()
    {
        Handle = string.Empty;
    }

    private Contributor(
        GitProvider provider, string handle, Guid employeeId, DateTimeOffset at)
    {
        Provider = provider;
        Handle = Required(handle, nameof(handle));
        EmployeeId = employeeId;
        ClaimedAt = at;

        Raise(new ContributorClaimed(Id, provider, Handle, employeeId, at));
    }

    public static Contributor Claim(
        GitProvider provider, string handle, Guid employeeId, DateTimeOffset at) =>
        new(provider, handle, employeeId, at);

    public GitProvider Provider { get; private init; }

    /// <summary>The login, as the provider spells it.</summary>
    public string Handle { get; private init; }

    public Guid EmployeeId { get; private set; }

    public DateTimeOffset ClaimedAt { get; private init; }

    /// <summary>
    /// Point a handle at somebody else.
    /// </summary>
    /// <remarks>
    /// Because the realistic mistake is claiming a handle for the wrong person, and
    /// the fix has to be one action rather than a delete and a re-add — which would
    /// lose the trail of who it was attributed to in between.
    /// </remarks>
    public void Reassign(Guid employeeId, DateTimeOffset at)
    {
        if (EmployeeId == employeeId)
        {
            return;
        }

        EmployeeId = employeeId;

        Raise(new ContributorClaimed(Id, Provider, Handle, employeeId, at));
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record ContributorClaimed(
    Guid ContributorId,
    GitProvider Provider,
    string Handle,
    Guid EmployeeId,
    DateTimeOffset At) : DomainEvent;
