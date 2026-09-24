using JiranisokoTech.Application.Procurement;
using JiranisokoTech.Domain.Procurement;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Procurement;

/// <summary>The purchasing reads and writes, over the one context.</summary>
public sealed class ProcurementRepository(AppDbContext database) : IProcurementRepository
{
    public Task<PurchaseRequest?> FindRequestAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.PurchaseRequests
            .Include(one => one.Lines)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Waiting and approved first, then what is settled, newest within each. The purchasing
    /// screen is opened to act on something, and the rows that can be acted on are the ones that
    /// have not been decided or have been approved and not yet ordered.
    /// </remarks>
    public Task<List<PurchaseRequest>> RequestsAsync(
        CancellationToken cancellationToken = default) =>
        database.PurchaseRequests
            .AsNoTracking()
            .Include(one => one.Lines)
            .OrderBy(one => one.State == PurchaseRequestState.Refused
                || one.State == PurchaseRequestState.Withdrawn)
            .ThenByDescending(one => one.RaisedAt)
            .ToListAsync(cancellationToken);

    public Task<List<PurchaseRequest>> RequestsForAsync(
        Guid raisedById, CancellationToken cancellationToken = default) =>
        database.PurchaseRequests
            .AsNoTracking()
            .Include(one => one.Lines)
            .Where(one => one.RaisedById == raisedById)
            .OrderByDescending(one => one.RaisedAt)
            .ToListAsync(cancellationToken);

    public Task<PurchaseOrder?> FindOrderAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        Full(database.PurchaseOrders)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<PurchaseOrder>> OrdersAsync(CancellationToken cancellationToken = default) =>
        Full(database.PurchaseOrders.AsNoTracking())
            .OrderBy(one => one.State == PurchaseOrderState.Complete
                || one.State == PurchaseOrderState.Cancelled)
            .ThenByDescending(one => one.RaisedAt)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// An empty set of lines means no orders rather than every order. The same trap
    /// <c>Reach.Nothing</c> exists for: a narrowing that returns everything when narrowed to
    /// nothing has silently become a widening.
    /// </remarks>
    public Task<List<PurchaseOrder>> OrdersAgainstAsync(
        IReadOnlyCollection<Guid> requestLineIds,
        CancellationToken cancellationToken = default) =>
        requestLineIds.Count == 0
            ? Task.FromResult(new List<PurchaseOrder>())
            : Full(database.PurchaseOrders.AsNoTracking())
                .Where(one => one.Lines.Any(
                    line => line.RequestLineId != null
                        && requestLineIds.Contains(line.RequestLineId.Value)))
                .ToListAsync(cancellationToken);

    /// <remarks>
    /// The last number used this year, read from the reference itself rather than kept in a
    /// counter table. A counter is a second thing to keep in step, and the references are already
    /// unique — so the number in them is the record of what has been used.
    /// </remarks>
    public async Task<int> LastRequestNumberAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var prefix = $"PR-{year}-";

        var last = await database.PurchaseRequests
            .AsNoTracking()
            .Where(one => one.Reference.StartsWith(prefix))
            .OrderByDescending(one => one.Reference)
            .Select(one => one.Reference)
            .FirstOrDefaultAsync(cancellationToken);

        return Tail(last, prefix);
    }

    public async Task<int> LastOrderNumberAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var prefix = $"PO-{year}-";

        var last = await database.PurchaseOrders
            .AsNoTracking()
            .Where(one => one.Number.StartsWith(prefix))
            .OrderByDescending(one => one.Number)
            .Select(one => one.Number)
            .FirstOrDefaultAsync(cancellationToken);

        return Tail(last, prefix);
    }

    public void Add(PurchaseRequest request) => database.PurchaseRequests.Add(request);

    public void Add(PurchaseOrder order) => database.PurchaseOrders.Add(order);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);

    /// <summary>An order with everything the screen that shows one needs.</summary>
    /// <remarks>
    /// All three collections, because the order page draws all three and the outstanding figure
    /// is computed from the receipts — loading them lazily would be a query per line on a page
    /// that always shows every line.
    /// </remarks>
    private static IQueryable<PurchaseOrder> Full(IQueryable<PurchaseOrder> query) =>
        query
            .Include(one => one.Lines)
            .Include(one => one.Receipts)
            .Include(one => one.Payments);

    private static int Tail(string? reference, string prefix) =>
        reference is not null
        && int.TryParse(reference[prefix.Length..], out var number)
            ? number
            : 0;
}
