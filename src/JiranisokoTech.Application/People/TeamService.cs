using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Application.People;

/// <summary>What the team rules need from storage.</summary>
public interface ITeamRepository
{
    Task<Team?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Team?> ByHandleAsync(string slug, CancellationToken cancellationToken = default);

    Task<bool> HandleTakenAsync(
        string slug, Guid? except = null, CancellationToken cancellationToken = default);

    /// <summary>Every team, live ones first.</summary>
    Task<List<Team>> AllAsync(
        bool includeDisbanded = false, CancellationToken cancellationToken = default);

    /// <summary>The live teams one person is currently on.</summary>
    Task<List<Team>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    void Add(Team team);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Forming teams, and moving people on and off them.
/// </summary>
/// <remarks>
/// Section 6. A team is not a department and this service exists partly to keep that true: it
/// never touches a staff record. Putting somebody on the platform team does not change the
/// department they sit in, who they answer to, or who approves their leave — those are facts
/// about employment, and a team is a fact about work.
///
/// The rules needing more than one row live here, as they do for people: a handle already taken,
/// and somebody being put on a team who does not work here.
/// </remarks>
public sealed class TeamService(
    ITeamRepository teams, IPeopleRepository people, IClock clock)
{
    public async Task<Team> FormAsync(
        string name,
        string? slug = null,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Slug.From(slug ?? name);

        if (await teams.HandleTakenAsync(handle.Value, null, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another team already uses the address '{handle.Value}'. Give this one a "
                + "different short name.");
        }

        var team = Team.Form(name, handle.Value, purpose);

        teams.Add(team);
        await teams.SaveAsync(cancellationToken);

        return team;
    }

    public async Task DescribeAsync(
        Guid id, string name, string? purpose, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);

        team.Rename(name);
        team.Describe(purpose);

        await teams.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Put somebody on a team.
    /// </summary>
    /// <remarks>
    /// They have to work here, and they have to still work here. A team is a list of people
    /// somebody expects to do something, and a leaver on it is a commitment nobody is going to
    /// keep — found out at the next stand-up rather than by anybody reading this list.
    /// </remarks>
    public async Task JoinAsync(
        Guid id, Guid employeeId, DateOnly? on = null, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);
        var person = await people.FindAsync(employeeId, cancellationToken)
            ?? throw new InvalidOperationException("That person is not on the staff list.");

        if (person.Status == EmploymentStatus.Left)
        {
            throw new InvalidOperationException(
                $"{person.FullName} has left. A team is a list of people somebody is expecting "
                + "work from.");
        }

        team.Join(employeeId, on ?? clock.Today);

        await teams.SaveAsync(cancellationToken);
    }

    public async Task LeaveAsync(
        Guid id, Guid employeeId, DateOnly? on = null, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);

        team.Leave(employeeId, on ?? clock.Today);

        await teams.SaveAsync(cancellationToken);
    }

    public async Task LeadAsync(
        Guid id, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);

        team.Lead(employeeId);

        await teams.SaveAsync(cancellationToken);
    }

    public async Task DisbandAsync(
        Guid id, DateOnly? on = null, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);

        team.Disband(on ?? clock.Today);

        await teams.SaveAsync(cancellationToken);
    }

    public async Task ReformAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var team = await Required(id, cancellationToken);

        team.Reform();

        await teams.SaveAsync(cancellationToken);
    }

    public Task<List<Team>> AllAsync(
        bool includeDisbanded = false, CancellationToken cancellationToken = default) =>
        teams.AllAsync(includeDisbanded, cancellationToken);

    public Task<Team?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        teams.FindAsync(id, cancellationToken);

    public Task<Team?> ByHandleAsync(string slug, CancellationToken cancellationToken = default) =>
        teams.ByHandleAsync(slug, cancellationToken);

    /// <summary>The teams one person is on, for their staff page.</summary>
    public Task<List<Team>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        teams.ForEmployeeAsync(employeeId, cancellationToken);

    private async Task<Team> Required(Guid id, CancellationToken cancellationToken) =>
        await teams.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That team does not exist.");
}
