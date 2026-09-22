using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Search;

/// <summary>What a result points at, and the page it opens.</summary>
public enum ResultKind
{
    Person = 1,
    Client = 2,
    Project = 3,
    WorkItem = 4,
    Invoice = 5,
    Candidate = 6,
}

public sealed record SearchResult(ResultKind Kind, Guid Id, string Title, string? Detail, string Href);

/// <summary>
/// One box that looks in several places.
/// </summary>
/// <remarks>
/// The whole point of a search box is that somebody types a name without first
/// deciding which screen it belongs to. That is also what makes it the easiest
/// place in a system to leak: every other page is behind one permission, and
/// this one reaches into six tables at once. So the permissions a person holds
/// are a parameter, every group checks its own before it runs, and a group
/// somebody may not see is not queried at all rather than queried and filtered —
/// the difference matters when the thing being hidden is how many candidates
/// applied or what a client is worth.
///
/// Matching is <c>lower(column) LIKE '%term%'</c>, spelled as
/// <c>ToLower().Contains(...)</c> so that both providers produce it. PostgreSQL
/// LIKE is case-sensitive and SQLite's is not, so anything relying on the
/// default would behave differently in the tests than in production — which is
/// the kind of difference that is found by a person typing their own name in
/// lower case and getting nothing.
/// </remarks>
public sealed class SearchQueries(AppDbContext database)
{
    /// <summary>Short enough to match half the database, so it is refused.</summary>
    public const int ShortestTerm = 2;

    /// <summary>Per group, so one crowded group cannot crowd out the others.</summary>
    private const int PerGroup = 8;

    public async Task<List<SearchResult>> FindAsync(
        string term,
        IReadOnlySet<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var cleaned = (term ?? string.Empty).Trim().ToLowerInvariant();

        if (cleaned.Length < ShortestTerm)
        {
            return [];
        }

        var found = new List<SearchResult>();

        if (permissions.Contains(Permissions.EmployeesView))
        {
            var people = await database.Employees
                .AsNoTracking()
                // Name and job title. An email address would be the obvious
                // third, and it is not here because it belongs to the account
                // rather than the staff record — searching it would mean joining
                // the identity tables into a query that is otherwise entirely
                // about the business.
                .Where(person => person.FullName.ToLower().Contains(cleaned)
                    || (person.JobTitle != null && person.JobTitle.ToLower().Contains(cleaned)))
                .OrderBy(person => person.FullName)
                .Take(PerGroup)
                .Select(person => new { person.Id, person.FullName, person.JobTitle })
                .ToListAsync(cancellationToken);

            found.AddRange(people.Select(person => new SearchResult(
                ResultKind.Person, person.Id, person.FullName, person.JobTitle,
                $"/people/{person.Id}")));
        }

        if (permissions.Contains(Permissions.ClientsView))
        {
            var clients = await database.Clients
                .AsNoTracking()
                .Where(client => client.Name.ToLower().Contains(cleaned)
                    || client.Code.ToLower().Contains(cleaned))
                .OrderBy(client => client.Name)
                .Take(PerGroup)
                .Select(client => new { client.Id, client.Name, client.Code })
                .ToListAsync(cancellationToken);

            found.AddRange(clients.Select(client => new SearchResult(
                ResultKind.Client, client.Id, client.Name, client.Code, "/clients")));
        }

        // A project is visible to somebody who can see all of them, and also to
        // an engineer who can only see the ones they are on. The narrower
        // permission is the one most people hold, so leaving it out would mean
        // the search box found nothing for almost everybody.
        if (permissions.Contains(Permissions.ProjectsViewAll)
            || permissions.Contains(Permissions.ProjectsViewMember))
        {
            var projects = await database.Projects
                .AsNoTracking()
                .Where(project => project.Name.ToLower().Contains(cleaned)
                    || project.Code.ToLower().Contains(cleaned))
                .OrderBy(project => project.Name)
                .Take(PerGroup)
                .Select(project => new { project.Id, project.Name, project.Code, project.Status })
                .ToListAsync(cancellationToken);

            found.AddRange(projects.Select(project => new SearchResult(
                ResultKind.Project,
                project.Id,
                project.Name,
                $"{project.Code} — {Spaced(project.Status.ToString())}",
                "/projects")));
        }

        if (permissions.Contains(Permissions.TasksViewAll)
            || permissions.Contains(Permissions.TasksViewOwn))
        {
            var items = await database.WorkItems
                .AsNoTracking()
                .Where(item => item.Title.ToLower().Contains(cleaned))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Title)
                .Take(PerGroup)
                .Select(item => new { item.Id, item.Title, item.Status })
                .ToListAsync(cancellationToken);

            found.AddRange(items.Select(item => new SearchResult(
                ResultKind.WorkItem,
                item.Id,
                item.Title,
                Spaced(item.Status.ToString()),
                $"/work/{item.Id}")));
        }

        if (permissions.Contains(Permissions.InvoicesView))
        {
            var invoices = await database.Invoices
                .AsNoTracking()
                .Where(invoice => invoice.Number.ToLower().Contains(cleaned))
                .OrderByDescending(invoice => invoice.Number)
                .Take(PerGroup)
                .Select(invoice => new { invoice.Id, invoice.Number, invoice.Status })
                .ToListAsync(cancellationToken);

            found.AddRange(invoices.Select(invoice => new SearchResult(
                ResultKind.Invoice,
                invoice.Id,
                invoice.Number,
                Spaced(invoice.Status.ToString()),
                $"/invoices/{invoice.Id}")));
        }

        if (permissions.Contains(Permissions.CandidatesView))
        {
            var candidates = await database.Candidates
                .AsNoTracking()
                .Where(candidate => candidate.FullName.ToLower().Contains(cleaned)
                    || candidate.Email.ToLower().Contains(cleaned))
                .OrderBy(candidate => candidate.FullName)
                .Take(PerGroup)
                .Select(candidate => new { candidate.Id, candidate.FullName, candidate.Email })
                .ToListAsync(cancellationToken);

            found.AddRange(candidates.Select(candidate => new SearchResult(
                ResultKind.Candidate, candidate.Id, candidate.FullName, candidate.Email,
                "/hiring")));
        }

        return found;
    }

    /// <summary>InProgress reads as "In progress" to somebody who is not a programmer.</summary>
    private static string Spaced(string name) =>
        string.Concat(name.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {char.ToLowerInvariant(character)}" : $"{character}"));
}
