using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Procurement;

namespace JiranisokoTech.Application.Procurement;

/// <summary>What buying things needs read and written.</summary>
public interface IProcurementRepository
{
    Task<PurchaseRequest?> FindRequestAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<PurchaseRequest>> RequestsAsync(CancellationToken cancellationToken = default);

    Task<List<PurchaseRequest>> RequestsForAsync(
        Guid raisedById, CancellationToken cancellationToken = default);

    Task<PurchaseOrder?> FindOrderAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<PurchaseOrder>> OrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>Orders carrying a line that came from one of these request lines.</summary>
    Task<List<PurchaseOrder>> OrdersAgainstAsync(
        IReadOnlyCollection<Guid> requestLineIds, CancellationToken cancellationToken = default);

    Task<int> LastRequestNumberAsync(int year, CancellationToken cancellationToken = default);

    Task<int> LastOrderNumberAsync(int year, CancellationToken cancellationToken = default);

    void Add(PurchaseRequest request);

    void Add(PurchaseOrder order);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Asking to buy something, committing to a supplier, and recording what arrived.
/// </summary>
/// <remarks>
/// Section 61. The rules here are the ones needing more than one row: a reference nobody else
/// holds, a supplier that is still current, and whether a request has actually been ordered —
/// which is a query over the orders rather than a flag on the request, because a stored figure is
/// one that can disagree with the documents it was added up from.
/// </remarks>
public sealed class ProcurementService(
    IProcurementRepository procurement,
    Vendors.IVendorRepository vendors,
    IClock clock)
{
    /// <summary>
    /// Ask the firm to buy something.
    /// </summary>
    /// <remarks>
    /// The reference is allocated the way an invoice number is — read the last one and add one —
    /// so two people raising a request in the same instant collide on the unique index rather
    /// than quietly sharing a reference.
    /// </remarks>
    public async Task<PurchaseRequest> RaiseAsync(
        Guid raisedById,
        string justification,
        DateOnly? neededBy = null,
        CancellationToken cancellationToken = default)
    {
        var year = clock.Today.Year;
        var next = await procurement.LastRequestNumberAsync(year, cancellationToken) + 1;

        var request = PurchaseRequest.Raise(
            $"PR-{year}-{next:0000}", raisedById, justification, clock.Now, neededBy);

        procurement.Add(request);
        await procurement.SaveAsync(cancellationToken);

        return request;
    }

    public async Task AddLineAsync(
        Guid requestId,
        string description,
        int quantity,
        long unitMinorUnits,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Add(description, quantity, Money.Of(unitMinorUnits, currency));

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task RemoveLineAsync(
        Guid requestId, Guid lineId, CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Remove(lineId);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task DescribeRequestAsync(
        Guid requestId,
        string justification,
        DateOnly? neededBy,
        CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Describe(justification, neededBy);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task SubmitAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Submit(clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task ApproveAsync(
        Guid requestId, string? note = null, CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Approved(clock.Now, note);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task RefuseAsync(
        Guid requestId, string why, CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Refused(why, clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task WithdrawAsync(
        Guid requestId, string why, CancellationToken cancellationToken = default)
    {
        var request = await RequiredRequest(requestId, cancellationToken);

        request.Withdraw(why, clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Commit to a supplier.
    /// </summary>
    /// <remarks>
    /// The supplier has to be one the firm still buys from. Raising an order against a company
    /// the firm has stopped dealing with is a mistake worth catching at the moment it is made
    /// rather than when somebody rings them.
    /// </remarks>
    public async Task<PurchaseOrder> RaiseOrderAsync(
        Guid vendorId,
        Guid accountId,
        Guid raisedById,
        CancellationToken cancellationToken = default)
    {
        var vendor = await vendors.FindAsync(vendorId, cancellationToken)
            ?? throw new InvalidOperationException("That supplier is not on file.");

        if (!vendor.IsCurrent)
        {
            throw new InvalidOperationException(
                $"{vendor.Name} is no longer bought from. Put them back on the books before "
                + "raising an order against them.");
        }

        var year = clock.Today.Year;
        var next = await procurement.LastOrderNumberAsync(year, cancellationToken) + 1;

        var order = PurchaseOrder.Raise(
            $"PO-{year}-{next:0000}", vendorId, accountId, raisedById, clock.Now);

        procurement.Add(order);
        await procurement.SaveAsync(cancellationToken);

        return order;
    }

    /// <summary>
    /// Put something on an order, optionally against an approved request line.
    /// </summary>
    /// <remarks>
    /// The request is not consumed and is not marked. Its line is pointed at, so the request
    /// stays exactly as its approver read it — which is the half of section 16's pipeline
    /// argument that does transfer: every conversion step in every system ever built loses the
    /// history.
    ///
    /// Only an approved request can be drawn from. Ordering against something still waiting on a
    /// decision is spending money the firm has not agreed to spend, and doing it against a
    /// refused one is worse.
    /// </remarks>
    public async Task AddOrderLineAsync(
        Guid orderId,
        string description,
        int quantity,
        long unitMinorUnits,
        string currency,
        Guid? requestId = null,
        Guid? requestLineId = null,
        CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        if (requestId is { } from)
        {
            var request = await RequiredRequest(from, cancellationToken);

            if (!request.IsApproved)
            {
                throw new InvalidOperationException(
                    $"{request.Reference} has not been approved, so nothing can be ordered "
                    + "against it yet.");
            }

            if (requestLineId is { } line && request.Lines.All(one => one.Id != line))
            {
                throw new InvalidOperationException(
                    $"That line is not on {request.Reference}.");
            }
        }

        order.Add(description, quantity, Money.Of(unitMinorUnits, currency), requestLineId);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task RemoveOrderLineAsync(
        Guid orderId, Guid lineId, CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Remove(lineId);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task DescribeOrderAsync(
        Guid orderId,
        Guid accountId,
        DateOnly? expectedOn,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Describe(accountId, expectedOn, notes);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task PlaceAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Place(clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task ReceiveAsync(
        Guid orderId,
        Guid lineId,
        int quantity,
        DateOnly receivedOn,
        ReceivedCondition condition,
        Guid receivedById,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Receive(lineId, quantity, receivedOn, condition, receivedById, note);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task CloseShortAsync(
        Guid orderId, Guid lineId, string why, CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.CloseShort(lineId, why, clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task PayAsync(
        Guid orderId,
        long minorUnits,
        string currency,
        DateOnly paidOn,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Pay(Money.Of(minorUnits, currency), paidOn, reference);

        await procurement.SaveAsync(cancellationToken);
    }

    public async Task CancelOrderAsync(
        Guid orderId, string why, CancellationToken cancellationToken = default)
    {
        var order = await RequiredOrder(orderId, cancellationToken);

        order.Cancel(why, clock.Now);

        await procurement.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Approved requests with nothing ordered against them.
    /// </summary>
    /// <remarks>
    /// The list this section exists to produce, and it is a query rather than a column. A stored
    /// "ordered" flag would be a figure that can disagree with the orders it was derived from,
    /// and the day it does nobody can tell which is wrong — the accounting decision, applied to
    /// purchasing.
    /// </remarks>
    public async Task<List<PurchaseRequest>> ApprovedAndUnorderedAsync(
        CancellationToken cancellationToken = default)
    {
        var approved = (await procurement.RequestsAsync(cancellationToken))
            .Where(one => one.IsApproved)
            .ToList();

        if (approved.Count == 0)
        {
            return [];
        }

        var lineIds = approved.SelectMany(one => one.Lines).Select(one => one.Id).ToList();

        var ordered = (await procurement.OrdersAgainstAsync(lineIds, cancellationToken))
            .Where(one => one.State != PurchaseOrderState.Cancelled)
            .SelectMany(one => one.Lines)
            .Where(one => one.RequestLineId is not null)
            .Select(one => one.RequestLineId!.Value)
            .ToHashSet();

        return [.. approved.Where(one => one.Lines.Any(line => !ordered.Contains(line.Id)))];
    }

    public Task<List<PurchaseRequest>> RequestsAsync(
        CancellationToken cancellationToken = default) =>
        procurement.RequestsAsync(cancellationToken);

    public Task<List<PurchaseRequest>> RequestsForAsync(
        Guid raisedById, CancellationToken cancellationToken = default) =>
        procurement.RequestsForAsync(raisedById, cancellationToken);

    public Task<PurchaseRequest?> RequestAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        procurement.FindRequestAsync(id, cancellationToken);

    public Task<List<PurchaseOrder>> OrdersAsync(CancellationToken cancellationToken = default) =>
        procurement.OrdersAsync(cancellationToken);

    public Task<PurchaseOrder?> OrderAsync(Guid id, CancellationToken cancellationToken = default) =>
        procurement.FindOrderAsync(id, cancellationToken);

    private async Task<PurchaseRequest> RequiredRequest(
        Guid id, CancellationToken cancellationToken) =>
        await procurement.FindRequestAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That request is not on file.");

    private async Task<PurchaseOrder> RequiredOrder(Guid id, CancellationToken cancellationToken) =>
        await procurement.FindOrderAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That order is not on file.");
}
