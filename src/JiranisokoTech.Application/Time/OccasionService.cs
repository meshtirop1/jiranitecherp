using JiranisokoTech.Domain.Time;

namespace JiranisokoTech.Application.Time;

/// <summary>What the firm's own dates need read and written.</summary>
public interface IOccasionRepository
{
    Task<Occasion?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Everything still to come, soonest first.</summary>
    Task<List<Occasion>> FromAsync(DateOnly from, CancellationToken cancellationToken = default);

    void Add(Occasion occasion);

    void Remove(Occasion occasion);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The dates the firm plans around that belong to nothing else.
/// </summary>
/// <remarks>
/// Section 33's only writeable part. Everything else on the calendar is written by the screen
/// that owns it — leave on the leave page, a sprint on the planning page — and read from there.
/// </remarks>
public sealed class OccasionService(IOccasionRepository occasions)
{
    public async Task<Occasion> PlanAsync(
        DateOnly on,
        string name,
        DateOnly? until = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var occasion = Occasion.Planned(on, name, until, note);

        occasions.Add(occasion);
        await occasions.SaveAsync(cancellationToken);

        return occasion;
    }

    public async Task DescribeAsync(
        Guid id,
        string name,
        DateOnly on,
        DateOnly? until,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var occasion = await Required(id, cancellationToken);

        occasion.Rename(name);
        occasion.Runs(on, until);
        occasion.Describe(note);

        await occasions.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Take it off the calendar.
    /// </summary>
    /// <remarks>
    /// Deleted rather than marked withdrawn, which is unusual here and deliberate. Almost
    /// everything in this system is kept because somebody acted on it — an invoice was sent, a
    /// laptop was issued, a decision was taken. A meeting cancelled before it happened is none of
    /// those: it is a plan that changed, and a tombstone on the calendar would put a thing that
    /// did not occur in front of everybody who opens the page. The audit trail keeps the record
    /// that it was there and was removed.
    /// </remarks>
    public async Task WithdrawAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var occasion = await Required(id, cancellationToken);

        occasions.Remove(occasion);
        await occasions.SaveAsync(cancellationToken);
    }

    public Task<List<Occasion>> FromAsync(
        DateOnly from, CancellationToken cancellationToken = default) =>
        occasions.FromAsync(from, cancellationToken);

    private async Task<Occasion> Required(Guid id, CancellationToken cancellationToken) =>
        await occasions.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That occasion is not on the calendar.");
}
