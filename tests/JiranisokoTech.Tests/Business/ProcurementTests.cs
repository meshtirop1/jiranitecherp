using JiranisokoTech.Application.Procurement;
using JiranisokoTech.Application.Vendors;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Procurement;
using JiranisokoTech.Infrastructure.Procurement;
using JiranisokoTech.Infrastructure.Vendors;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// Asking to buy something, committing to a supplier, and what actually arrived.
/// </summary>
/// <remarks>
/// Section 61. The invariant the whole section rests on is one line of arithmetic — ordered
/// equals received plus written off plus outstanding, per line, always — and the tests here are
/// mostly about the ways a system gets that wrong: by storing the remainder, by letting somebody
/// receive more than was ordered, or by quietly deciding an order is finished when three of ten
/// never came.
/// </remarks>
public class ProcurementTests
{
    /// <summary>
    /// A request cannot be sent with nothing on it.
    /// </summary>
    /// <remarks>
    /// The invoice's rule for the invoice's reason: somebody is being asked to approve spending
    /// and there is nothing to approve.
    /// </remarks>
    [Fact]
    public async Task A_request_with_nothing_on_it_cannot_be_sent()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var request = await procurement.RaiseAsync(who, "The despatch laptop will not run it");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.SubmitAsync(request.Id));

        Assert.Contains("nothing on this request", refusal.Message);
    }

    /// <summary>
    /// Once submitted, the lines cannot change.
    /// </summary>
    /// <remarks>
    /// The list is what the approver is reading. Changing it afterwards is the fault the
    /// invoice's draft rule exists to prevent — somebody approves one thing and the record says
    /// another, with nothing to show the two were ever different.
    /// </remarks>
    [Fact]
    public async Task A_submitted_request_is_frozen()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var request = await procurement.RaiseAsync(who, "Two monitors for the despatch desk");

        await procurement.AddLineAsync(request.Id, "Monitor", 2, 25_000_00, "KES");
        await procurement.SubmitAsync(request.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.AddLineAsync(request.Id, "And a keyboard", 1, 3_000_00, "KES"));

        Assert.Contains("sent for approval", refusal.Message);
    }

    /// <summary>A request is in one currency.</summary>
    /// <remarks>
    /// Caught at the line with both codes named, rather than left to Money to throw about
    /// arithmetic on the total — the person typing needs to know which line is the odd one.
    /// </remarks>
    [Fact]
    public async Task A_request_is_in_one_currency()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var request = await procurement.RaiseAsync(who, "Kit for the new engineer");

        await procurement.AddLineAsync(request.Id, "Laptop", 1, 120_000_00, "KES");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.AddLineAsync(request.Id, "Licence", 1, 200_00, "USD"));

        Assert.Contains("KES", refusal.Message);
        Assert.Contains("USD", refusal.Message);
    }

    /// <summary>
    /// Nothing can be ordered against a request that has not been approved.
    /// </summary>
    /// <remarks>
    /// Ordering against something still waiting is spending money the firm has not agreed to
    /// spend, and against a refused one it is worse.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_ordered_against_an_unapproved_request()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var request = await procurement.RaiseAsync(who, "A laptop");

        await procurement.AddLineAsync(request.Id, "Laptop", 1, 120_000_00, "KES");
        await procurement.SubmitAsync(request.Id);

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.AddOrderLineAsync(
                order.Id, "Laptop", 1, 120_000_00, "KES", request.Id));

        Assert.Contains("has not been approved", refusal.Message);
    }

    /// <summary>
    /// Raising an order does not consume or mark the request.
    /// </summary>
    /// <remarks>
    /// The half of section 16's pipeline argument that does transfer: every conversion step in
    /// every system ever built loses the history. The request stays exactly as its approver read
    /// it, and the link is on the order's line pointing back.
    /// </remarks>
    [Fact]
    public async Task Ordering_does_not_change_the_request()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var request = await procurement.RaiseAsync(who, "A laptop");

        await procurement.AddLineAsync(request.Id, "Laptop", 1, 120_000_00, "KES");
        await procurement.SubmitAsync(request.Id);
        await procurement.ApproveAsync(request.Id);

        var line = (await procurement.RequestAsync(request.Id))!.Lines.Single();
        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(
            order.Id, "Laptop", 1, 118_000_00, "KES", request.Id, line.Id);

        var after = await procurement.RequestAsync(request.Id);

        // Unchanged: still approved, still one line, still the figure the approver saw.
        Assert.Equal(PurchaseRequestState.Approved, after!.State);
        Assert.Single(after.Lines);
        Assert.Equal(120_000_00, after.Lines.Single().Unit.MinorUnits);

        // And the order points back at it.
        var ordered = (await procurement.OrderAsync(order.Id))!.Lines.Single();

        Assert.Equal(line.Id, ordered.RequestLineId);
    }

    /// <summary>
    /// Approved and nothing ordered is a query, not a flag.
    /// </summary>
    /// <remarks>
    /// The accounting decision applied to purchasing: a stored figure is one that can disagree
    /// with the documents it was added up from, and the day it does nobody can tell which is
    /// wrong.
    /// </remarks>
    [Fact]
    public async Task Approved_and_unordered_is_worked_out_from_the_orders()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var request = await procurement.RaiseAsync(who, "A laptop");

        await procurement.AddLineAsync(request.Id, "Laptop", 1, 120_000_00, "KES");
        await procurement.SubmitAsync(request.Id);
        await procurement.ApproveAsync(request.Id);

        Assert.Single(await procurement.ApprovedAndUnorderedAsync());

        var line = (await procurement.RequestAsync(request.Id))!.Lines.Single();
        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(
            order.Id, "Laptop", 1, 118_000_00, "KES", request.Id, line.Id);

        Assert.Empty(await procurement.ApprovedAndUnorderedAsync());
    }

    /// <summary>
    /// Ordering ten and receiving seven leaves three, and the order stays open.
    /// </summary>
    /// <remarks>
    /// The case the whole section is shaped around. Nothing stores "outstanding" — it is the
    /// ordered quantity less what the receipts add up to less what was written off, computed
    /// every time it is asked for.
    /// </remarks>
    [Fact]
    public async Task A_partial_delivery_leaves_the_rest_outstanding()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 10, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        var line = (await procurement.OrderAsync(order.Id))!.Lines.Single();

        await procurement.ReceiveAsync(
            order.Id, line.Id, 7, fixture.Clock.Today, ReceivedCondition.Good, who);

        var after = (await procurement.OrderAsync(order.Id))!;

        Assert.Equal(7, after.ReceivedOnLine(line.Id));
        Assert.Equal(3, after.OutstandingOn(line.Id));

        // Still open. It does not quietly decide itself finished.
        Assert.Equal(PurchaseOrderState.Placed, after.State);
        Assert.False(after.EverythingSettled);
    }

    /// <summary>Receiving more than was ordered is refused, with both numbers.</summary>
    /// <remarks>
    /// Either the count is wrong or the supplier sent more than was asked for, and both are
    /// conversations rather than something to absorb silently.
    /// </remarks>
    [Fact]
    public async Task More_cannot_arrive_than_was_ordered()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 2, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        var line = (await procurement.OrderAsync(order.Id))!.Lines.Single();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.ReceiveAsync(
                order.Id, line.Id, 3, fixture.Clock.Today, ReceivedCondition.Good, who));

        Assert.Contains("Only 2", refusal.Message);
        Assert.Contains("3 was entered", refusal.Message);
    }

    /// <summary>
    /// Writing off what is not coming closes the order honestly.
    /// </summary>
    /// <remarks>
    /// Without it the only ways to close are to pretend the missing three arrived — which puts
    /// three things on the register that do not exist — or to leave the order open for ever.
    /// A reason is required, because a quantity written off with none cannot be told from a
    /// counting mistake.
    /// </remarks>
    [Fact]
    public async Task What_is_never_coming_is_written_off_with_a_reason()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 10, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        var line = (await procurement.OrderAsync(order.Id))!.Lines.Single();

        await procurement.ReceiveAsync(
            order.Id, line.Id, 7, fixture.Clock.Today, ReceivedCondition.Good, who);

        await Assert.ThrowsAsync<ArgumentException>(
            () => procurement.CloseShortAsync(order.Id, line.Id, "   "));

        await procurement.CloseShortAsync(
            order.Id, line.Id, "discontinued; the supplier refunded the difference");

        var after = (await procurement.OrderAsync(order.Id))!;

        Assert.Equal(3, after.Lines.Single().Short);
        Assert.Equal(0, after.OutstandingOn(line.Id));
        Assert.Equal(PurchaseOrderState.Complete, after.State);
        Assert.Contains("discontinued", after.Outcome);
    }

    /// <summary>A placed order cannot have its lines changed.</summary>
    /// <remarks>
    /// The supplier is holding a document with these figures on it, and editing our copy leaves
    /// the two disagreeing with nothing to say which is right.
    /// </remarks>
    [Fact]
    public async Task A_placed_order_is_frozen()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 1, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.AddOrderLineAsync(order.Id, "Another", 1, 1_000_00, "KES"));

        Assert.Contains("has been placed", refusal.Message);
    }

    /// <summary>Paying more than the order is for is refused.</summary>
    /// <remarks>
    /// An overpayment is nearly always the same payment entered twice, and absorbing it silently
    /// is how a supplier ledger stops matching a bank statement.
    /// </remarks>
    [Fact]
    public async Task An_order_cannot_be_overpaid()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 1, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        await procurement.PayAsync(order.Id, 120_000_00, "KES", fixture.Clock.Today, "FT26001");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.PayAsync(
                order.Id, 120_000_00, "KES", fixture.Clock.Today, "FT26002"));

        Assert.Contains("entered twice", refusal.Message);
    }

    /// <summary>An order cannot be raised against a supplier the firm no longer buys from.</summary>
    [Fact]
    public async Task No_order_against_a_former_supplier()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        await vendors.MoveToAsync(supplier.Id, JiranisokoTech.Domain.Vendors.VendorStatus.Former);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.RaiseOrderAsync(supplier.Id, account, who));

        Assert.Contains("no longer bought from", refusal.Message);
    }

    /// <summary>An order something has arrived against cannot be cancelled.</summary>
    /// <remarks>
    /// Cancelling would throw away the record that seven laptops are sitting in the office.
    /// Closing the outstanding lines short is the honest alternative, and the message says so.
    /// </remarks>
    [Fact]
    public async Task An_order_with_deliveries_cannot_be_cancelled()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (procurement, vendors) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");
        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var account = await Account(context, "Equipment");

        var order = await procurement.RaiseOrderAsync(supplier.Id, account, who);

        await procurement.AddOrderLineAsync(order.Id, "Laptop", 10, 120_000_00, "KES");
        await procurement.PlaceAsync(order.Id);

        var line = (await procurement.OrderAsync(order.Id))!.Lines.Single();

        await procurement.ReceiveAsync(
            order.Id, line.Id, 1, fixture.Clock.Today, ReceivedCondition.Good, who);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => procurement.CancelOrderAsync(order.Id, "changed our mind"));

        Assert.Contains("Close the outstanding lines short", refusal.Message);
    }

    private static (ProcurementService Procurement, VendorService Vendors) Services(
        DatabaseFixture fixture, TestDbContext context)
    {
        var vendors = new VendorRepository(context);

        return (
            new ProcurementService(new ProcurementRepository(context), vendors, fixture.Clock),
            new VendorService(vendors, fixture.Clock));
    }

    private static async Task<Guid> Somebody(
        TestDbContext context, DatabaseFixture fixture, string name)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, null, "Engineer");

        employee.Start();
        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }

    private static async Task<Guid> Account(TestDbContext context, string name)
    {
        var account = JiranisokoTech.Domain.Money.Account.Open(
            "5000", name, AccountKind.Expense);

        context.Accounts.Add(account);
        await context.SaveChangesAsync();

        return account.Id;
    }
}
