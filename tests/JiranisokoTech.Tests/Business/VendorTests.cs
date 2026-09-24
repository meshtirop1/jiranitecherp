using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Vendors;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Vendors;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Vendors;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The firm's suppliers, and the gap section 17 named.
/// </summary>
/// <remarks>
/// Section 62 exists because a supplier's name was free text in three tables at once — on an
/// agreement, on a standing cost and on a platform resource — so the firm could hold a signed
/// hosting agreement and the monthly charge for that hosting and have nothing able to say the two
/// were about one company.
///
/// The tests worth writing are the ones about the decisions rather than the fields: that a
/// supplier is not a client, that the link onto an agreement is optional so nothing already on
/// file breaks, and that the one-to-ring rule really is the same rule the client contact book
/// uses rather than a second copy of it.
/// </remarks>
public class VendorTests
{
    /// <summary>
    /// Two suppliers cannot share a short name, however it was typed.
    /// </summary>
    /// <remarks>
    /// The section fails if there are two Safaricom rows. A unique index on the name would not
    /// stop it, because it is case-sensitive — the same fault the asset tag upper-cases to
    /// avoid — so the constraint is on the reduced code and the refusal names the supplier that
    /// already holds it.
    /// </remarks>
    [Fact]
    public async Task Two_suppliers_cannot_share_a_short_name()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await service.TakeOnAsync("Safaricom PLC", null, "Airtime and data");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TakeOnAsync("SAFARICOM plc"));

        Assert.Contains("Safaricom PLC already uses", refusal.Message);
        Assert.Equal(1, await context.Vendors.CountAsync());
    }

    /// <summary>
    /// A company can be a client and a supplier at once, with a different state on each side.
    /// </summary>
    /// <remarks>
    /// The case that decides the whole section. One party table with IsClient and IsVendor flags
    /// has one status column, and the day the firm stops selling to somebody while going on
    /// buying from them it would have to say Former and Active simultaneously.
    /// </remarks>
    [Fact]
    public async Task A_company_can_be_a_client_and_a_supplier_with_different_states()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var vendors = Service(fixture, context);

        var client = JiranisokoTech.Domain.Clients.Client.TakeOn("Safaricom PLC", "safaricom-client");

        client.MoveTo(JiranisokoTech.Domain.Clients.ClientStatus.Former);
        context.Clients.Add(client);
        await context.SaveChangesAsync();

        var supplier = await vendors.TakeOnAsync("Safaricom PLC", "safaricom", "Airtime");

        Assert.Equal(VendorStatus.Active, supplier.Status);
        Assert.Equal(
            JiranisokoTech.Domain.Clients.ClientStatus.Former,
            (await context.Clients.FirstAsync(one => one.Id == client.Id)).Status);
    }

    /// <summary>
    /// An agreement keeps working with no supplier linked to it.
    /// </summary>
    /// <remarks>
    /// The optionality is what protects every row already in the table. The migration adds a
    /// nullable column and backfills nothing, because a script matching "Safaricom" to
    /// "Safaricom PLC" is a script that will one day match the wrong one silently. Somebody links
    /// an old agreement when they next open it.
    /// </remarks>
    [Fact]
    public async Task An_agreement_with_no_supplier_linked_still_works()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var agreements = Agreements(fixture, context);

        var agreement = await agreements.DraftAsync(
            AgreementKind.Vendor, "JTS-VEN-2026-007", "Hosting", "HostPinnacle");

        Assert.Null(agreement.VendorId);
        Assert.Equal("HostPinnacle", agreement.Party);

        // And it can still be edited, which is the thing a required link would have refused.
        await agreements.DescribeAsync(
            agreement.Id, AgreementKind.Vendor, "Hosting and domains", "HostPinnacle",
            null, null, null);

        Assert.Equal("Hosting and domains", agreement.Title);
    }

    /// <summary>
    /// An agreement is with a member of staff or with a supplier, never both.
    /// </summary>
    /// <remarks>
    /// Refused rather than tolerated, because the screen has to pick one to show — and a row
    /// holding both would show whichever the markup happened to test first, which is a page that
    /// disagrees with itself depending on the order two if statements were written.
    /// </remarks>
    [Fact]
    public async Task An_agreement_cannot_be_with_a_person_and_a_supplier_at_once()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var vendors = Service(fixture, context);
        var agreements = Agreements(fixture, context);

        var supplier = await vendors.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");

        var employee = JiranisokoTech.Domain.People.Employee.Hire(
            "Brian Kiptoo", fixture.Clock.Today, null, "Engineer");

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => agreements.DraftAsync(
                AgreementKind.Vendor, "JTS-VEN-2026-008", "Hosting", "HostPinnacle",
                employee.Id, supplier.Id));
    }

    /// <summary>
    /// The first contact added is the one to ring, without anybody being asked.
    /// </summary>
    /// <remarks>
    /// A contact book whose only entry is marked as nothing in particular is one where the next
    /// person to write to that company has to guess.
    /// </remarks>
    [Fact]
    public async Task The_first_contact_becomes_the_one_to_ring()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");

        var first = await service.AddContactAsync(supplier.Id, "Wanjiku Mwangi", "Account manager");
        var second = await service.AddContactAsync(supplier.Id, "Otieno Were", "Support");

        Assert.True(first.IsMain);
        Assert.False(second.IsMain);
    }

    /// <summary>
    /// Promoting somebody clears whoever held the post.
    /// </summary>
    /// <remarks>
    /// Through the rule shared with the client contact book. Two people marked as the one to ring
    /// is the state the whole flag exists to prevent.
    /// </remarks>
    [Fact]
    public async Task Promoting_somebody_clears_whoever_held_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var first = await service.AddContactAsync(supplier.Id, "Wanjiku Mwangi");
        var second = await service.AddContactAsync(supplier.Id, "Otieno Were");

        await service.PromoteAsync(second.Id);

        var book = await service.ContactsForAsync(supplier.Id);

        Assert.Single(book, one => one.IsMain);
        Assert.Equal(second.Id, book.Single(one => one.IsMain).Id);
        Assert.False(book.Single(one => one.Id == first.Id).IsMain);
    }

    /// <summary>
    /// When the one to ring leaves, the longest-serving of whoever is left takes it.
    /// </summary>
    /// <remarks>
    /// The entity clears the flag when somebody leaves — it has to, or an order goes to an
    /// address that bounces for a year — so without this the supplier is left with contacts and
    /// nobody flagged, which is the state the book exists to prevent arrived at by the back door.
    /// This is the same fault the client contact book already found by being opened, and it is
    /// now the same code preventing both.
    /// </remarks>
    [Fact]
    public async Task When_the_one_to_ring_leaves_somebody_else_takes_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var first = await service.AddContactAsync(supplier.Id, "Wanjiku Mwangi");

        fixture.Clock.Advance(TimeSpan.FromDays(1));

        var second = await service.AddContactAsync(supplier.Id, "Otieno Were");

        fixture.Clock.Advance(TimeSpan.FromDays(1));

        var third = await service.AddContactAsync(supplier.Id, "Achieng Odhiambo");

        await service.GoneAsync(first.Id);

        var book = await service.ContactsForAsync(supplier.Id);

        Assert.Equal(second.Id, book.Single(one => one.IsMain).Id);
        Assert.False(book.Single(one => one.Id == first.Id).IsHere);

        // Nothing is deleted: the leaver's row is what explains a year-old email.
        Assert.Equal(3, book.Count);
        Assert.Contains(book, one => one.Id == third.Id);
    }

    /// <summary>Somebody who has left cannot be made the one to ring.</summary>
    [Fact]
    public async Task Somebody_who_has_left_cannot_be_the_one_to_ring()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");
        var first = await service.AddContactAsync(supplier.Id, "Wanjiku Mwangi");
        var second = await service.AddContactAsync(supplier.Id, "Otieno Were");

        await service.GoneAsync(second.Id);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PromoteAsync(second.Id));

        Assert.Contains("Otieno Were has left", refusal.Message);
        Assert.Equal(first.Id, (await service.ContactsForAsync(supplier.Id))
            .Single(one => one.IsMain).Id);
    }

    /// <summary>
    /// A KRA PIN is upper-cased, so one taxpayer is not two.
    /// </summary>
    /// <remarks>
    /// The same reasoning the asset tag arrived at from the other direction: a case-sensitive
    /// comparison would make P051234567X and p051234567x two different taxpayers.
    /// </remarks>
    [Fact]
    public async Task A_tax_pin_is_upper_cased()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");

        await service.RegisteredAsync(supplier.Id, " p051234567x ", "Nairobi", 30);

        Assert.Equal("P051234567X", (await service.OneAsync(supplier.Id))!.TaxPin);
    }

    /// <summary>Payment terms are refused beyond six months, as a client's are.</summary>
    [Fact]
    public async Task Payment_terms_have_a_ceiling()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var supplier = await service.TakeOnAsync("HostPinnacle Ltd", "hostpinnacle");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RegisteredAsync(supplier.Id, null, null, 365));
    }

    private static VendorService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new VendorRepository(context), fixture.Clock);

    private static AgreementService Agreements(DatabaseFixture fixture, TestDbContext context) =>
        new(new AgreementRepository(context), fixture.Clock);
}
