using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Privacy;

namespace JiranisokoTech.Application.Privacy;

/// <summary>What the privacy register needs read and written.</summary>
public interface IPrivacyRepository
{
    Task<PrivacyRequest?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<PrivacyRequest>> AllAsync(CancellationToken cancellationToken = default);

    Task<int> LastNumberAsync(int year, CancellationToken cancellationToken = default);

    void Add(PrivacyRequest request);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Requests made under the Data Protection Act, and what was done about them.
/// </summary>
/// <remarks>
/// Section 55. The erasure itself lives on <see cref="Employee.Forget"/>, because emptying the
/// personal data around a name needs nothing but that object. What lives here is everything that
/// needs more than one row: allocating a reference, and refusing to erase a staff record when the
/// outcome recorded against it does not say that is what was decided.
/// </remarks>
public sealed class PrivacyService(
    IPrivacyRepository privacy, IPeopleRepository people, IClock clock)
{
    public async Task<PrivacyRequest> ReceiveAsync(
        PrivacyAsk ask,
        SubjectKind subjectKind,
        string subject,
        DateOnly receivedOn,
        Guid? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        var year = clock.Today.Year;
        var next = await privacy.LastNumberAsync(year, cancellationToken) + 1;

        var request = PrivacyRequest.Received(
            $"DPA-{year}-{next:000}",
            ask,
            subjectKind,
            subject,
            receivedOn,
            clock.Now,
            subjectId);

        privacy.Add(request);
        await privacy.SaveAsync(cancellationToken);

        return request;
    }

    public async Task DecideAsync(
        Guid requestId,
        DataClass held,
        OutcomeKind kind,
        Guid decidedById,
        string? basis = null,
        DateOnly? until = null,
        CancellationToken cancellationToken = default)
    {
        var request = await Required(requestId, cancellationToken);

        request.Decide(held, kind, decidedById, clock.Now, basis, until);

        await privacy.SaveAsync(cancellationToken);
    }

    public async Task ArrivedAsync(
        Guid requestId, DateOnly receivedOn, CancellationToken cancellationToken = default)
    {
        var request = await Required(requestId, cancellationToken);

        request.Arrived(receivedOn);

        await privacy.SaveAsync(cancellationToken);
    }

    public async Task AnsweredAsync(
        Guid requestId, string note, CancellationToken cancellationToken = default)
    {
        var request = await Required(requestId, cancellationToken);

        request.Answered(note, clock.Now);

        await privacy.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Actually empty the personal data on a staff record.
    /// </summary>
    /// <remarks>
    /// Behind its own permission and refused unless the request says so. That second check is the
    /// point: a button that erases somebody's record without a decision recorded against a class
    /// of their data is a button that empties a personnel file on one person's say-so, with
    /// nothing afterwards able to say why it happened.
    ///
    /// Only the classes actually marked erased are emptied, and at present that is the contact
    /// details, the identity numbers and the next of kin — the three that
    /// <see cref="Employee.Forget"/> clears together, because they are the three held under no
    /// continuing basis once somebody has left.
    /// </remarks>
    public async Task ForgetAsync(
        Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await Required(requestId, cancellationToken);

        if (request.Ask != PrivacyAsk.Erasure)
        {
            throw new InvalidOperationException(
                $"{request.Reference} is a request to see what the firm holds, not to erase it.");
        }

        if (request.SubjectId is not { } who || request.SubjectKind != SubjectKind.Employee)
        {
            throw new InvalidOperationException(
                "This request is not against a staff record, so there is nothing here to empty.");
        }

        var erasing = request.Outcomes
            .Where(one => one.Kind == OutcomeKind.Erased)
            .Select(one => one.Held)
            .ToHashSet();

        if (!erasing.Overlaps(Erasable))
        {
            throw new InvalidOperationException(
                "Nothing on this request has been decided as erased. Record what is being erased "
                + "and on what basis anything else is kept, then do it — a record emptied with "
                + "no decision behind it is one nobody can explain afterwards.");
        }

        var employee = await people.FindAsync(who, cancellationToken)
            ?? throw new InvalidOperationException("That staff record is not on file.");

        employee.Forget();

        await people.SaveAsync(cancellationToken);
    }

    public Task<List<PrivacyRequest>> AllAsync(CancellationToken cancellationToken = default) =>
        privacy.AllAsync(cancellationToken);

    public Task<PrivacyRequest?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        privacy.FindAsync(id, cancellationToken);

    /// <summary>The classes <see cref="Employee.Forget"/> can actually empty.</summary>
    /// <remarks>
    /// Named here so that asking to erase a pay record — which the firm is obliged to keep for
    /// its statutory period — does not silently count as authorisation to empty a staff record.
    /// </remarks>
    private static readonly DataClass[] Erasable =
    [
        DataClass.ContactDetails,
        DataClass.IdentityNumbers,
        DataClass.NextOfKin,
    ];

    private async Task<PrivacyRequest> Required(Guid id, CancellationToken cancellationToken) =>
        await privacy.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That request is not on file.");
}
