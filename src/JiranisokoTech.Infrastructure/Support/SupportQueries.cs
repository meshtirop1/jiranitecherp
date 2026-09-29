using JiranisokoTech.Application.Support;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Support;

public sealed class SupportRepository(AppDbContext database) : ISupportRepository
{
    public Task<Ticket?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Tickets.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<Ticket?> ByNumberAsync(
        int number, CancellationToken cancellationToken = default) =>
        database.Tickets.FirstOrDefaultAsync(one => one.Number == number, cancellationToken);

    /// <summary>
    /// The highest number used, or nothing.
    /// </summary>
    /// <remarks>
    /// Max over the column rather than a count, for the reason every other numbered thing here
    /// uses one: a count is wrong the moment a row is ever removed, and it is wrong quietly —
    /// the next ticket takes a number that is already on somebody's telephone note.
    /// </remarks>
    public async Task<int> LastNumberAsync(CancellationToken cancellationToken = default) =>
        await database.Tickets.AnyAsync(cancellationToken)
            ? await database.Tickets.MaxAsync(one => one.Number, cancellationToken)
            : 0;

    public void Add(Ticket ticket) => database.Tickets.Add(ticket);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}

/// <summary>One line of the queue.</summary>
public sealed record TicketRow(
    Guid Id,
    int Number,
    string Reference,
    string Subject,
    TicketPriority Priority,
    TicketStatus Status,
    string Who,
    string? Client,
    string? Assignee,
    DateTimeOffset RespondBy,
    DateTimeOffset? FirstRespondedAt,
    bool ResponseOverdue);

/// <summary>Somebody a ticket can be raised for.</summary>
/// <remarks>
/// One flat list of colleagues and client contacts rather than two cascading selects, because a
/// cascading select needs a round trip and that round trip is the only thing that would force
/// this page to be interactive — which would in turn make the raise form untestable.
/// </remarks>
public sealed record PossibleRequester(
    Guid Id, Requester Kind, string Name, string? Client, Guid? ClientId)
{
    /// <summary>What the select posts, which has to be both halves of the answer.</summary>
    /// <remarks>
    /// The kind travels with the identifier because together they are one answer. A select
    /// posting only the identifier would leave the page to work out which of two tables to look
    /// in, and the only way to do that is to look in both and prefer one — so a colleague and a
    /// contact would be told apart by which query happened to be written first.
    ///
    /// The writing and the reading sit beside each other deliberately. They are a pair, and a
    /// separator changed in one of two places is a form that silently stops raising tickets for
    /// anybody outside the firm.
    /// </remarks>
    public string Posted => $"{(int)Kind}:{Id}";

    /// <summary>Read back what the select posted.</summary>
    public static bool TryRead(string? posted, out Requester kind, out Guid id)
    {
        kind = Requester.Colleague;
        id = Guid.Empty;

        if (posted is null)
        {
            return false;
        }

        var split = posted.IndexOf(':', StringComparison.Ordinal);

        if (split <= 0
            || !int.TryParse(posted[..split], out var which)
            || !Enum.IsDefined(typeof(Requester), which)
            || !Guid.TryParse(posted[(split + 1)..], out id))
        {
            return false;
        }

        kind = (Requester)which;

        return true;
    }
}

/// <summary>
/// Reading the help desk.
/// </summary>
/// <remarks>
/// Section 26. The queue never loads a thread: a ticket's messages run to ten thousand
/// characters each and the list shows none of them, so every read here is a projection.
/// </remarks>
public sealed class SupportQueries(AppDbContext database)
{
    /// <summary>Enough for a desk. There is no paging, and a bound instead.</summary>
    public const int Most = 300;

    public async Task<List<TicketRow>> QueueAsync(
        DateTimeOffset now,
        TicketStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = database.Tickets.AsNoTracking();

        if (status is { } wanted)
        {
            query = query.Where(one => one.Status == wanted);
        }

        var rows = await query
            /*
             * Resolved last, then soonest promise first. Not newest first: somebody sitting
             * down to the desk needs the order the promises run out in, and the order things
             * were typed is the order that makes the oldest unanswered ticket invisible.
             */
            .OrderBy(one => one.Status == TicketStatus.Resolved)
            .ThenBy(one => one.RespondBy)
            .Take(Most)
            .Select(one => new
            {
                one.Id,
                one.Number,
                one.Subject,
                one.Priority,
                one.Status,
                one.From,
                one.RequesterId,
                one.RespondBy,
                one.FirstRespondedAt,
                Client = database.Clients
                    .Where(client => client.Id == one.ClientId)
                    .Select(client => client.Name)
                    .FirstOrDefault(),
                Assignee = database.Employees
                    .Where(person => person.Id == one.AssigneeId)
                    .Select(person => person.FullName)
                    .FirstOrDefault(),
                Colleague = database.Employees
                    .Where(person => person.Id == one.RequesterId)
                    .Select(person => person.FullName)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        /*
         * The contacts are fetched in a second query rather than joined into the first. The
         * ticket holds an identifier with no key behind it — the price of a column pointing at
         * two tables — so there is nothing for EF to join on, and a correlated subquery per row
         * would be three hundred of them on a full queue.
         */
        var contactIds = rows
            .Where(row => row.From == Requester.ClientContact)
            .Select(row => row.RequesterId)
            .ToHashSet();

        var contacts = contactIds.Count == 0
            ? []
            : await database.Contacts
                .AsNoTracking()
                .Where(contact => contactIds.Contains(contact.Id))
                .ToDictionaryAsync(
                    contact => contact.Id, contact => contact.Name, cancellationToken);

        return
        [
            .. rows.Select(row => new TicketRow(
                row.Id,
                row.Number,
                $"S{row.Number}",
                row.Subject,
                row.Priority,
                row.Status,
                row.From == Requester.ClientContact
                    ? contacts.GetValueOrDefault(row.RequesterId) ?? "somebody"
                    : row.Colleague ?? "somebody",
                row.Client,
                row.Assignee,
                row.RespondBy,
                row.FirstRespondedAt,
                row.FirstRespondedAt is null && now > row.RespondBy)),
        ];
    }

    /// <summary>
    /// How many tickets nobody has answered and the time to do it has gone.
    /// </summary>
    /// <remarks>
    /// The number in the navigation, and it is this one rather than a count of open tickets for
    /// the reason the incidents entry already states: a count that does not clear teaches
    /// people to ignore it. This clears the moment somebody says something to whoever asked,
    /// which is exactly the act it exists to provoke.
    /// </remarks>
    public Task<int> UnansweredAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        database.Tickets.CountAsync(
            one => one.FirstRespondedAt == null
                && one.Status != TicketStatus.Resolved
                && one.RespondBy < now,
            cancellationToken);

    /// <summary>Everybody a ticket can be raised for.</summary>
    public async Task<List<PossibleRequester>> RequestersAsync(
        CancellationToken cancellationToken = default)
    {
        var colleagues = await database.Employees
            .AsNoTracking()
            .Where(person => person.Status != Domain.People.EmploymentStatus.Left)
            .OrderBy(person => person.FullName)
            .Select(person => new PossibleRequester(
                person.Id, Requester.Colleague, person.FullName, null, null))
            .ToListAsync(cancellationToken);

        /*
         * Only contacts who are still there: a contact who has left is a fact worth keeping,
         * but offering them in a list of people to raise a ticket for would mean promising an
         * answer to somebody who has gone.
         *
         * Ordered before the record is constructed rather than after it. Ordering by a member
         * of a constructor-projected type is something EF cannot translate, and it fails at run
         * time rather than at build time — which reaches the browser as a 500 on this page.
         */
        var contacts = await database.Contacts
            .AsNoTracking()
            .Where(contact => contact.GoneAt == null)
            .Join(
                database.Clients.AsNoTracking(),
                contact => contact.ClientId,
                client => client.Id,
                (contact, client) => new
                {
                    contact.Id,
                    contact.Name,
                    Client = client.Name,
                    ClientId = client.Id,
                })
            .OrderBy(one => one.Client)
            .ThenBy(one => one.Name)
            .ToListAsync(cancellationToken);

        return
        [
            .. colleagues,
            .. contacts.Select(one => new PossibleRequester(
                one.Id, Requester.ClientContact, one.Name, one.Client, one.ClientId)),
        ];
    }

    /// <summary>The name against an identifier that points at one of two tables.</summary>
    public async Task<(string Name, string? Client)> WhoAsync(
        Requester kind, Guid requesterId, CancellationToken cancellationToken = default)
    {
        if (kind == Requester.Colleague)
        {
            var name = await database.Employees
                .AsNoTracking()
                .Where(person => person.Id == requesterId)
                .Select(person => person.FullName)
                .FirstOrDefaultAsync(cancellationToken);

            return (name ?? "somebody", null);
        }

        var found = await database.Contacts
            .AsNoTracking()
            .Where(contact => contact.Id == requesterId)
            .Join(
                database.Clients.AsNoTracking(),
                contact => contact.ClientId,
                client => client.Id,
                (contact, client) => new { contact.Name, Client = client.Name })
            .FirstOrDefaultAsync(cancellationToken);

        return (found?.Name ?? "somebody", found?.Client);
    }
}
