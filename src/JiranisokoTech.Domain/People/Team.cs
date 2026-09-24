using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// One person's time on one team.
/// </summary>
/// <remarks>
/// Dated on both ends rather than a row that appears and disappears, because "who was on the
/// platform team when this decision was taken" is a question somebody asks six months later, and
/// a membership table that only knows about now cannot answer it. Removing somebody writes a
/// leaving date; nothing is deleted.
///
/// Which is also why rejoining is allowed and produces a second row. Somebody lent to another
/// team for a quarter and then brought back has two spells, and flattening those into one
/// membership with the earliest date would claim they never left.
/// </remarks>
public sealed class TeamMembership : Entity
{
    private TeamMembership()
    {
    }

    internal TeamMembership(Guid employeeId, DateOnly joinedOn)
    {
        EmployeeId = employeeId;
        JoinedOn = joinedOn;
    }

    public Guid EmployeeId { get; private init; }

    public DateOnly JoinedOn { get; private init; }

    public DateOnly? LeftOn { get; private set; }

    public bool IsCurrent => LeftOn is null;

    internal void Left(DateOnly on)
    {
        if (on < JoinedOn)
        {
            throw new ArgumentException(
                "Somebody cannot leave a team before they joined it.", nameof(on));
        }

        LeftOn = on;
    }
}

/// <summary>
/// A group of people working on something together.
/// </summary>
/// <remarks>
/// Section 6, and deliberately <b>not</b> the same thing as a department, which is why it is a
/// second aggregate rather than a second kind of <see cref="Department"/>.
///
/// A department is where somebody sits in the firm: one of them, on their staff record, and it
/// decides who they answer to, who may read their record and who signs off their leave. A team
/// is what they are working on: several at a time, changing more often than anybody updates an
/// org chart, and routinely crossing departments — a delivery squad with two engineers, a
/// designer and somebody from the office is the ordinary case rather than the exception.
///
/// Modelling teams as sub-departments was the alternative and it fails on exactly that case.
/// One person cannot sit in two places at once, so a cross-department team either takes people
/// out of the department that answers for them, or it duplicates them. The first breaks leave
/// approval and the second breaks headcount.
///
/// Joining a team therefore changes nothing about a staff record. That is the whole point of
/// the separation, and there is a test which asserts it rather than a comment which claims it.
/// </remarks>
public sealed class Team : Entity, IAuditable
{
    private readonly List<TeamMembership> _members = [];

    private Team()
    {
        Name = string.Empty;
        Slug = string.Empty;
    }

    private Team(string name, Slug slug, string? purpose)
    {
        Name = Require(name, nameof(name));
        Slug = slug.Value;
        Purpose = Trimmed(purpose);

        Raise(new TeamFormed(Id, Name, Slug));
    }

    public static Team Form(string name, string? slug = null, string? purpose = null) =>
        new(name, Common.Slug.From(slug ?? name), purpose);

    public string Name { get; private set; }

    /// <summary>The short name in addresses, fixed once the team exists.</summary>
    public string Slug { get; private init; }

    public Slug Handle => Common.Slug.FromStored(Slug);

    /// <summary>
    /// What this team is for.
    /// </summary>
    /// <remarks>
    /// Optional in the database and asked for on the screen. A team nobody can describe in a
    /// sentence is usually two teams or none, and the sentence is what tells somebody six
    /// months later whether the team they are looking at is the one they mean.
    /// </remarks>
    public string? Purpose { get; private set; }

    /// <summary>Who runs it. Null while nobody does.</summary>
    /// <remarks>
    /// A fact about the team rather than a flag on the person, for the reason
    /// <see cref="Department"/> gives: it is the team that can only have one.
    ///
    /// Not the same person as the department head, and not derived from them. A team lead runs
    /// a piece of work; a head of department answers for people. Deriving one from the other
    /// would make every cross-department team's lead ambiguous.
    /// </remarks>
    public Guid? LeadEmployeeId { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>Everybody who has ever been on it, current spells first.</summary>
    /// <remarks>
    /// A copy, not the backing list. Handing EF its own navigation property makes it treat
    /// added rows as updates, and then nothing is saved and nothing complains.
    /// </remarks>
    public IReadOnlyList<TeamMembership> Members =>
        [.. _members
            .OrderBy(one => one.LeftOn is not null)
            .ThenByDescending(one => one.JoinedOn)
            .ThenByDescending(one => one.Id)];

    public IReadOnlyList<TeamMembership> Current =>
        [.. Members.Where(one => one.IsCurrent)];

    public int Size => _members.Count(one => one.IsCurrent);

    public bool Has(Guid employeeId) =>
        _members.Any(one => one.EmployeeId == employeeId && one.IsCurrent);

    /// <summary>
    /// Put somebody on the team.
    /// </summary>
    /// <remarks>
    /// Refused for somebody already on it, because two current spells for one person makes the
    /// size wrong and every "is this person on the team" answer arbitrary. Rejoining after a
    /// spell has ended is fine and is the case the refusal is careful not to catch.
    /// </remarks>
    public TeamMembership Join(Guid employeeId, DateOnly on)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException(
                $"{Name} has been disbanded. Re-form it before putting anybody on it.");
        }

        if (Has(employeeId))
        {
            throw new InvalidOperationException("They are already on this team.");
        }

        var membership = new TeamMembership(employeeId, on);

        _members.Add(membership);

        Raise(new TeamJoined(Id, Name, employeeId, on));

        return membership;
    }

    /// <summary>
    /// Take somebody off it.
    /// </summary>
    /// <remarks>
    /// The lead's post goes with them. A team lead who is not on the team is either a mistake
    /// or a department head wearing the wrong hat, and leaving the identifier there means the
    /// team page names somebody who is not in the list underneath it.
    /// </remarks>
    public void Leave(Guid employeeId, DateOnly on)
    {
        var membership = _members.FirstOrDefault(
            one => one.EmployeeId == employeeId && one.IsCurrent)
            ?? throw new InvalidOperationException("They are not on this team.");

        membership.Left(on);

        if (LeadEmployeeId == employeeId)
        {
            LeadEmployeeId = null;

            Raise(new TeamLeadChanged(Id, employeeId, null));
        }

        Raise(new TeamLeft(Id, Name, employeeId, on));
    }

    /// <summary>
    /// Say who runs it.
    /// </summary>
    /// <remarks>
    /// The lead has to be on the team. Not a tidiness rule: the lead is who somebody asks about
    /// the team's work, and somebody who is not on it cannot answer — while the screen showing
    /// a name above a list that does not contain it reads as a bug in the list.
    /// </remarks>
    public void Lead(Guid? employeeId)
    {
        if (LeadEmployeeId == employeeId)
        {
            return;
        }

        if (employeeId is { } lead && !Has(lead))
        {
            throw new InvalidOperationException(
                "A team is led by somebody on it. Put them on the team first.");
        }

        var from = LeadEmployeeId;
        LeadEmployeeId = employeeId;

        Raise(new TeamLeadChanged(Id, from, employeeId));
    }

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void Describe(string? purpose) => Purpose = Trimmed(purpose);

    /// <summary>
    /// Disband it.
    /// </summary>
    /// <remarks>
    /// Not deleted, for the reason a department is not: everything anybody wrote about this
    /// team's work still names it, and the memberships are the record of who was on it.
    /// Ending them all here rather than leaving them open, because a disbanded team with
    /// current members would keep appearing on every one of those people's pages.
    /// </remarks>
    public void Disband(DateOnly on)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;

        foreach (var membership in _members.Where(one => one.IsCurrent).ToList())
        {
            membership.Left(on < membership.JoinedOn ? membership.JoinedOn : on);
        }

        LeadEmployeeId = null;

        Raise(new TeamDisbanded(Id, Name, on));
    }

    public void Reform()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
    }

    /// <summary>Nothing here is a secret. Who is on which team is meant to be read.</summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
