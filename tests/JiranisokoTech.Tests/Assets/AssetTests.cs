using JiranisokoTech.Application.Assets;
using JiranisokoTech.Domain.Assets;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Assets;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Assets;

using Money = JiranisokoTech.Domain.Common.Money;

/// <summary>
/// The asset register, and the duplication it replaced.
/// </summary>
/// <remarks>
/// Section 15. Before it there were two lists — what a joiner was handed and what a leaver owed
/// back — kept on two different aggregates, filled in separately, and unable between them to
/// answer the only question a register exists for: where is this laptop, and who has had it.
///
/// Two rules here carry the weight. <b>One thing cannot be out with two people</b>, which is the
/// state the whole register exists to make impossible and which happens by somebody issuing a
/// machine they did not know was already out. And <b>lost is not retired</b>: a machine with the
/// firm's data on it going missing is a different conversation from one that came to the end of
/// its life, and a register that files them together makes the first invisible.
/// </remarks>
public class AssetTests
{
    /// <summary>Two things cannot share a tag.</summary>
    /// <remarks>
    /// The tag is the firm's own label and the one identifier that can be said down a telephone.
    /// Two rows sharing one means every reference to it is ambiguous, including the ones already
    /// written on a delivery note.
    /// </remarks>
    [Fact]
    public async Task Two_things_cannot_share_a_tag()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuyAsync(
                "jd-014", AssetKind.Phone, "iPhone", fixture.Clock.Today));

        Assert.Contains("JD-014", refusal.Message);
    }

    /// <summary>
    /// A tag typed in lower case is the same tag.
    /// </summary>
    /// <remarks>
    /// Asserted separately because it is the way the uniqueness actually gets got round: the
    /// index is case-sensitive, so jd-014 would be a second row for the same laptop — and a
    /// second row for one machine is precisely the fault the register was built to end.
    /// </remarks>
    [Fact]
    public async Task A_tag_is_the_same_tag_in_any_case()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            " jd-014 ", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        Assert.Equal("JD-014", asset.Tag);
    }

    /// <summary>One thing cannot be out with two people.</summary>
    [Fact]
    public async Task Something_already_out_cannot_be_issued_again()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var amina = await Person(fixture, context, "Amina Hassan");
        var brian = await Person(fixture, context, "Brian Otieno");

        await service.IssueAsync(asset.Id, amina, fixture.Clock.Today);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueAsync(asset.Id, brian, fixture.Clock.Today));

        Assert.Contains("already has this", refusal.Message);
    }

    /// <summary>
    /// The whole of a thing's life is on it, in order, and it cannot be edited.
    /// </summary>
    /// <remarks>
    /// The part worth having. A status column says where a laptop is today; this says it went
    /// out, came back with a cracked screen, was repaired and went out again — which settles
    /// whether the screen was already cracked, and tells somebody when to stop repairing it.
    /// </remarks>
    [Fact]
    public async Task Everything_that_happens_to_it_is_kept()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var amina = await Person(fixture, context, "Amina Hassan");

        await service.IssueAsync(asset.Id, amina, fixture.Clock.Today);

        fixture.Clock.Advance(TimeSpan.FromDays(180));

        await service.TakeBackAsync(
            asset.Id, fixture.Clock.Today, "Screen cracked in the corner");

        await service.RepairAsync(asset.Id, "Screen replacement", fixture.Clock.Today);
        await service.RepairedAsync(asset.Id, "New screen fitted", fixture.Clock.Today);

        var stored = await context.Assets
            .Include(one => one.Movements)
            .SingleAsync(one => one.Id == asset.Id);

        Assert.Equal(AssetStatus.InStock, stored.Status);
        Assert.Equal(5, stored.Movements.Count);
        Assert.Contains(
            stored.Movements, one => one.What.Contains("Screen cracked in the corner"));
        Assert.Contains(stored.Movements, one => one.PersonId == amina);
    }

    /// <summary>Lost and retired are different states, and lost keeps who had it.</summary>
    /// <remarks>
    /// The first question about a missing laptop is who had it last, and it is the only
    /// question the record can answer once the machine is gone.
    /// </remarks>
    [Fact]
    public async Task Something_lost_keeps_who_had_it_last()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var amina = await Person(fixture, context, "Amina Hassan");

        await service.IssueAsync(asset.Id, amina, fixture.Clock.Today);
        await service.MissingAsync(
            asset.Id, "Not returned when they left", fixture.Clock.Today);

        var stored = await context.Assets
            .Include(one => one.Movements)
            .SingleAsync(one => one.Id == asset.Id);

        Assert.Equal(AssetStatus.Lost, stored.Status);
        Assert.Null(stored.HeldById);

        var last = stored.Movements.First();

        Assert.Equal(AssetStatus.Lost, last.To);
        Assert.Equal(amina, last.PersonId);
    }

    /// <summary>Something retired or lost cannot be handed to anybody.</summary>
    [Fact]
    public async Task Something_out_of_service_cannot_be_issued()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var amina = await Person(fixture, context, "Amina Hassan");

        await service.RetireAsync(asset.Id, "Battery swollen", fixture.Clock.Today);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueAsync(asset.Id, amina, fixture.Clock.Today));
    }

    /// <summary>What somebody is holding is one query, and it is what both checklists read.</summary>
    /// <remarks>
    /// The point of the whole section. The joiner's screen and the leaver's screen ask this same
    /// question of this same table, which is what stops them disagreeing.
    /// </remarks>
    [Fact]
    public async Task What_one_person_is_holding_is_one_query()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var amina = await Person(fixture, context, "Amina Hassan");
        var brian = await Person(fixture, context, "Brian Otieno");

        var laptop = await service.BuyAsync(
            "JD-014", AssetKind.Laptop, "MacBook Air", fixture.Clock.Today);

        var phone = await service.BuyAsync(
            "JD-015", AssetKind.Phone, "iPhone 17", fixture.Clock.Today);

        var monitor = await service.BuyAsync(
            "JD-016", AssetKind.Monitor, "Dell 27in", fixture.Clock.Today);

        await service.IssueAsync(laptop.Id, amina, fixture.Clock.Today);
        await service.IssueAsync(phone.Id, amina, fixture.Clock.Today);
        await service.IssueAsync(monitor.Id, brian, fixture.Clock.Today);

        var hers = await service.HeldByAsync(amina);
        var spare = await service.InStockAsync();

        Assert.Equal(2, hers.Count);
        Assert.Empty(spare);

        await service.TakeBackAsync(phone.Id, fixture.Clock.Today);

        Assert.Single(await service.HeldByAsync(amina));
        Assert.Single(await service.InStockAsync());
    }

    /// <summary>The cost is recorded in minor units, like every other amount here.</summary>
    /// <remarks>
    /// Asserted because three money boxes in this application once stored a hundredth of what
    /// was typed, and each of them looked right on the screen that wrote it.
    /// </remarks>
    [Fact]
    public async Task A_cost_is_stored_in_minor_units()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var asset = await service.BuyAsync(
            "JD-014",
            AssetKind.Laptop,
            "MacBook Air",
            fixture.Clock.Today,
            cost: Money.Of(185_000_00, "KES"));

        var stored = await context.Assets.SingleAsync(one => one.Id == asset.Id);

        Assert.Equal(18_500_000, stored.CostMinorUnits);
        Assert.Equal(Money.Of(185_000_00, "KES"), stored.Cost);
    }

    private static AssetService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new AssetRepository(context), fixture.Clock);

    private static async Task<Guid> Person(
        DatabaseFixture fixture, TestDbContext context, string name)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, null, "Delivery engineer");

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
