using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Authorization;
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

    /// <summary>
    /// An attached file.
    /// </summary>
    /// <remarks>
    /// Added last and deliberately kept last in the results, because a document is almost
    /// never what somebody is looking for when they type into a search box — they want the
    /// client or the project the document is attached to, and the document is how they get
    /// there when they cannot remember which.
    /// </remarks>
    Document = 7,
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
public sealed class SearchQueries(AppDbContext database, Reaches reaches)
{
    /// <summary>Short enough to match half the database, so it is refused.</summary>
    public const int ShortestTerm = 2;

    /// <summary>Per group, so one crowded group cannot crowd out the others.</summary>
    private const int PerGroup = 8;

    public async Task<List<SearchResult>> FindAsync(
        string term,
        IReadOnlySet<string> permissions,
        Guid? employeeId = null,
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

        /*
         * Narrowed to the projects this person can actually see, which it was not.
         *
         * This block used to treat holding projects.view_member — granted to every
         * engineer — as permission to find every project in the firm by name, on the
         * reasoning that returning nothing would make the search box useless for almost
         * everybody. Both halves of that were true and the conclusion was still wrong: the
         * answer was never "everything or nothing", it was the projects they are on, and
         * there was no way to express that until Reach existed.
         */
        var reach = await reaches.ProjectsAsync(permissions, employeeId, cancellationToken);

        if (!reach.IsNothing)
        {
            var projects = await reach
                .Apply(database.Projects.AsNoTracking(), project => project.Id)
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

        /*
         * Documents, matched on file name and tags, and narrowed to the kinds this person
         * may see.
         *
         * The narrowing is the whole of the care here. A document inherits the permission of
         * the thing it is attached to — see Documents.PermissionToSee — so searching them
         * without that filter would let anybody who can open the search box find the file
         * name of every contract, payslip and disciplinary letter in the firm. A file name is
         * not nothing: "grievance-outcome-mwangi.pdf" discloses the whole of its contents.
         *
         * Superseded versions are excluded. Somebody searching wants the contract, not the
         * four drafts of it, and the history is on the thing the document is attached to.
         */
        var kinds = Enum.GetValues<AttachedTo>()
            .Where(kind => permissions.Contains(
                Application.Documents.Documents.PermissionToSee(kind)))
            .ToList();

        if (kinds.Count > 0)
        {
            var documents = await database.Attachments
                .AsNoTracking()
                .Where(document => kinds.Contains(document.Kind)
                    && document.SupersededAt == null
                    && (document.FileName.ToLower().Contains(cleaned)
                        || (document.Tags != null && document.Tags.Contains(cleaned))))
                .OrderByDescending(document => document.UploadedAt)
                .Take(PerGroup)
                .Select(document => new
                {
                    document.Id,
                    document.FileName,
                    document.Kind,
                    document.Tags,
                })
                .ToListAsync(cancellationToken);

            found.AddRange(documents.Select(document => new SearchResult(
                ResultKind.Document,
                document.Id,
                document.FileName,
                document.Tags is { Length: > 0 }
                    ? $"{Spaced(document.Kind.ToString())} — {document.Tags}"
                    : Spaced(document.Kind.ToString()),
                $"/documents/{document.Id}")));
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
