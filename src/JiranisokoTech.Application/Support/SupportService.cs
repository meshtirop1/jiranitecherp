using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Support;

/// <summary>What the help desk needs read and written.</summary>
public interface ISupportRepository
{
    Task<Ticket?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Ticket?> ByNumberAsync(int number, CancellationToken cancellationToken = default);

    Task<int> LastNumberAsync(CancellationToken cancellationToken = default);

    void Add(Ticket ticket);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Requests from people, and what the firm said back.
/// </summary>
/// <remarks>
/// Section 26. Almost everything worth refusing is refused on <see cref="Ticket"/> itself,
/// where it needs no other row to decide. Two things live here because they do: allocating the
/// number, and the hand-off into a real piece of work — which is the one place this section
/// touches another aggregate, and the one place it could quietly become a second board.
/// </remarks>
public sealed class SupportService(
    ISupportRepository tickets, WorkService work, IClock clock)
{
    public async Task<Ticket> RaiseAsync(
        string subject,
        string body,
        TicketPriority priority,
        Requester from,
        Guid requesterId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var next = await tickets.LastNumberAsync(cancellationToken) + 1;

        var ticket = Ticket.Raise(
            next, subject, body, priority, from, requesterId, clock.Now, clientId);

        tickets.Add(ticket);
        await tickets.SaveAsync(cancellationToken);

        return ticket;
    }

    public async Task NoteAsync(
        Guid id, string text, Guid byEmployeeId, CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Note(text, byEmployeeId, clock.Now);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task ReplyAsync(
        Guid id,
        string text,
        Guid byEmployeeId,
        bool waitingOnThem = false,
        CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Reply(text, byEmployeeId, clock.Now, waitingOnThem);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task TheySaidAsync(
        Guid id, string text, CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.TheySaid(text, clock.Now);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task AssignAsync(
        Guid id, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Assign(employeeId);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task ReprioritiseAsync(
        Guid id,
        TicketPriority priority,
        Guid byEmployeeId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Reprioritise(priority, byEmployeeId, clock.Now);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task ResolveAsync(
        Guid id, string answer, Guid byEmployeeId, CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Resolve(answer, byEmployeeId, clock.Now);

        await tickets.SaveAsync(cancellationToken);
    }

    public async Task ReopenAsync(
        Guid id, string why, Guid byEmployeeId, CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        ticket.Reopen(why, byEmployeeId, clock.Now);

        await tickets.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Turn the request into a piece of work, once.
    /// </summary>
    /// <remarks>
    /// The one hand-off in this section, and the place it would most easily become a second
    /// board. What is created is an ordinary work item through the ordinary service, so it gets
    /// its number, its transitions and its permissions from there; the ticket keeps nothing but
    /// the identifier.
    ///
    /// The priority is mapped downwards on purpose. A client's complaint may be blocking for
    /// them and still must not be able to set the one priority on the board that stops
    /// everything — that is a decision somebody inside the firm makes, looking at everything
    /// else that is running.
    ///
    /// Neither record drags the other afterwards. Deploying the work does not resolve the
    /// ticket, because nobody has told the requester yet; resolving the ticket does not finish
    /// the work, because the fix may be a week behind the answer.
    /// </remarks>
    public async Task<WorkItem> MakeWorkAsync(
        Guid id,
        Guid raisedById,
        Guid? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var ticket = await Required(id, cancellationToken);

        /*
         * Asked before anything is created. Raising the work first and letting the ticket
         * refuse afterwards would leave an orphan card on the board with nothing pointing at it.
         */
        ticket.NotAlreadyWork();

        var item = await work.RaiseAsync(
            $"{ticket.Reference} — {ticket.Subject}",
            raisedById,
            projectId,
            priority: ticket.Priority switch
            {
                TicketPriority.Blocking => Priority.High,
                TicketPriority.Slowing => Priority.Normal,
                _ => Priority.Low,
            },
            cancellationToken: cancellationToken);

        ticket.BecameWork(item.Id);
        ticket.Note($"Being dealt with as work item #{item.Number}.", raisedById, clock.Now);

        await tickets.SaveAsync(cancellationToken);

        return item;
    }

    public Task<Ticket?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        tickets.FindAsync(id, cancellationToken);

    public Task<Ticket?> ByNumberAsync(
        int number, CancellationToken cancellationToken = default) =>
        tickets.ByNumberAsync(number, cancellationToken);

    private async Task<Ticket> Required(Guid id, CancellationToken cancellationToken) =>
        await tickets.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That ticket is not on file.");
}
