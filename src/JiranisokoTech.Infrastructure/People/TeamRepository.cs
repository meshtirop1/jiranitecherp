using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.People;

/// <summary>
/// The team reads and writes, over the one context.
/// </summary>
/// <remarks>
/// Every read includes the memberships, because a team without its members is a name — every
/// caller here wants the list, and a lazy load would be a query per team in a loop over the
/// board.
/// </remarks>
public sealed class TeamRepository(AppDbContext database) : ITeamRepository
{
    public Task<Team?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Teams
            .Include(team => team.Members)
            .FirstOrDefaultAsync(team => team.Id == id, cancellationToken);

    public Task<Team?> ByHandleAsync(
        string slug, CancellationToken cancellationToken = default) =>
        database.Teams
            .Include(team => team.Members)
            .FirstOrDefaultAsync(team => team.Slug == slug, cancellationToken);

    public Task<bool> HandleTakenAsync(
        string slug, Guid? except = null, CancellationToken cancellationToken = default) =>
        database.Teams.AnyAsync(team => team.Slug == slug && team.Id != except, cancellationToken);

    public async Task<List<Team>> AllAsync(
        bool includeDisbanded = false, CancellationToken cancellationToken = default)
    {
        var query = database.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .AsQueryable();

        if (!includeDisbanded)
        {
            query = query.Where(team => team.IsActive);
        }

        return await query
            .OrderByDescending(team => team.IsActive)
            .ThenBy(team => team.Name)
            .ToListAsync(cancellationToken);
    }

    /// <remarks>
    /// Current spells only, and live teams only. A person's page answers "what are you working
    /// on", and a disbanded team or a spell that ended last year is not an answer to that —
    /// the team's own page is where its history is kept.
    /// </remarks>
    public Task<List<Team>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Teams
            .AsNoTracking()
            .Include(team => team.Members)
            .Where(team => team.IsActive
                && team.Members.Any(one => one.EmployeeId == employeeId && one.LeftOn == null))
            .OrderBy(team => team.Name)
            .ToListAsync(cancellationToken);

    public void Add(Team team) => database.Teams.Add(team);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
