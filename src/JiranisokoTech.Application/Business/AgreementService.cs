using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Contracts;

namespace JiranisokoTech.Application.Business;

/// <summary>What the firm's own paperwork needs read and written.</summary>
public interface IAgreementRepository
{
    Task<Agreement?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> ReferenceTakenAsync(
        string reference, Guid? except = null, CancellationToken cancellationToken = default);

    Task<List<Agreement>> AllAsync(CancellationToken cancellationToken = default);

    /// <summary>Whatever is on one person's record.</summary>
    Task<List<Agreement>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default);

    void Add(Agreement agreement);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Employment contracts, vendor agreements and NDAs.
/// </summary>
/// <remarks>
/// Section 17's second half, and the reason it is a second half rather than more of the first:
/// a client contract carries a value and is what invoices are raised against, and this paper
/// carries obligations and no money. One table would have meant a nullable value column and a
/// screen apologising for it on half the rows.
///
/// What it adds over a folder of PDFs is a date something runs out on, and a job that reads it.
/// </remarks>
public sealed class AgreementService(IAgreementRepository agreements, IClock clock)
{
    /// <summary>
    /// Write one down.
    /// </summary>
    /// <remarks>
    /// The reference is checked here as well as by the index, because the index's message is a
    /// constraint violation and this one can say which agreement already answers to it — and
    /// somebody typing a reference that exists has usually picked up the wrong file.
    /// </remarks>
    public async Task<Agreement> DraftAsync(
        AgreementKind kind,
        string reference,
        string title,
        string party,
        Guid? employeeId = null,
        CancellationToken cancellationToken = default)
    {
        if (await agreements.ReferenceTakenAsync(
            reference, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                $"{reference.Trim()} is already on file. References are what people quote in "
                + "an email, and two pieces of paper answering to one make every reference "
                + "to it ambiguous.");
        }

        var agreement = Agreement.Draft(
            kind, reference, title, party, clock.Now, employeeId);

        agreements.Add(agreement);
        await agreements.SaveAsync(cancellationToken);

        return agreement;
    }

    public async Task DescribeAsync(
        Guid id,
        AgreementKind kind,
        string title,
        string party,
        Guid? employeeId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var agreement = await Required(id, cancellationToken);

        agreement.Describe(kind, title, party, employeeId, notes);

        await agreements.SaveAsync(cancellationToken);
    }

    public async Task RunsAsync(
        Guid id,
        DateOnly? startsOn,
        DateOnly? endsOn,
        int? noticeDays,
        CancellationToken cancellationToken = default)
    {
        var agreement = await Required(id, cancellationToken);

        agreement.Runs(startsOn, endsOn, noticeDays);

        await agreements.SaveAsync(cancellationToken);
    }

    public async Task SignedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agreement = await Required(id, cancellationToken);

        agreement.Signed(clock.Now);

        await agreements.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Replace one with a later one.
    /// </summary>
    /// <remarks>
    /// The replacement has to exist, because the whole value of recording this is that somebody
    /// reading the old one can find the new one — and a reference to a piece of paper nobody
    /// wrote down is a dead end with a date on it.
    /// </remarks>
    public async Task SupersedeAsync(
        Guid id, string byReference, CancellationToken cancellationToken = default)
    {
        var agreement = await Required(id, cancellationToken);

        if (!await agreements.ReferenceTakenAsync(
            byReference, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException(
                $"Nothing on file answers to {byReference.Trim()}. Write the new agreement down "
                + "first, so that whoever reads this one can find it.");
        }

        agreement.SupersededBy(byReference, clock.Now);

        await agreements.SaveAsync(cancellationToken);
    }

    public async Task EndAsync(
        Guid id, string why, CancellationToken cancellationToken = default)
    {
        var agreement = await Required(id, cancellationToken);

        agreement.End(why, clock.Now);

        await agreements.SaveAsync(cancellationToken);
    }

    public Task<List<Agreement>> AllAsync(CancellationToken cancellationToken = default) =>
        agreements.AllAsync(cancellationToken);

    public Task<Agreement?> OneAsync(Guid id, CancellationToken cancellationToken = default) =>
        agreements.FindAsync(id, cancellationToken);

    /// <summary>What is on one person's record, for their staff page.</summary>
    public Task<List<Agreement>> ForEmployeeAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        agreements.ForEmployeeAsync(employeeId, cancellationToken);

    private async Task<Agreement> Required(Guid id, CancellationToken cancellationToken) =>
        await agreements.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That agreement is not on file.");
}
