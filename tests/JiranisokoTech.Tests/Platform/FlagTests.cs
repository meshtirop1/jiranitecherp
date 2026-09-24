using JiranisokoTech.Application.Platform;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Platform;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Platform;

/// <summary>
/// Things the firm can turn on and off without deploying.
/// </summary>
/// <remarks>
/// Section 68. Three things here matter more than the rest.
///
/// <b>A key is what source code asks for</b>, so it is normalised once on the way in. A flag
/// that answers to <c>Invoices.USD</c> and not to <c>invoices.usd</c> fails in the one way
/// nobody debugs quickly: by being off, silently, with no error anywhere.
///
/// <b>Off is the answer for anything unknown.</b> A flag that does not exist and a flag that is
/// off have to look the same to an application, because otherwise the absence of a row is a
/// state somebody has to guess about — and two applications will guess differently.
///
/// <b>Every move is kept with its reason.</b> This is what an incident reads. A flag is quick
/// precisely because it leaves no trace anywhere else, which makes it the change least likely
/// to be mentioned and most likely to be the cause.
/// </remarks>
public class FlagTests
{
    [Theory]
    [InlineData("invoices.usd", "invoices.usd")]
    [InlineData("  Invoices.USD  ", "invoices.usd")]
    [InlineData("invoices usd", "invoices.usd")]
    public async Task A_key_is_what_the_application_will_ask_for(string typed, string stored)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync(typed, "Invoices in US dollars");

        Assert.Equal(stored, flag.Key);
    }

    /// <summary>Two flags cannot share a key.</summary>
    /// <remarks>
    /// Because an application asking for it would get whichever the database returned first,
    /// which is a different answer on a different day.
    /// </remarks>
    [Fact]
    public async Task Two_flags_cannot_share_a_key()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        await service.AddAsync("invoices.usd", "Invoices in US dollars");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddAsync("Invoices USD", "The same thing again"));
    }

    /// <summary>A new flag is off everywhere, in every environment.</summary>
    /// <remarks>
    /// All four states present from the start, because a flag with no row for production reads
    /// as absent rather than as off — and absent is the state an application has to guess about.
    /// </remarks>
    [Fact]
    public async Task A_new_flag_is_off_everywhere()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("invoices.usd", "Invoices in US dollars");

        Assert.Equal(4, flag.Settings.Count);
        Assert.All(flag.Settings, one => Assert.False(one.On));
    }

    /// <summary>
    /// A flag is on in one environment and off in another, which is the whole point.
    /// </summary>
    [Fact]
    public async Task A_flag_is_per_environment()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("invoices.usd", "Invoices in US dollars");

        await service.SetAsync(
            flag.Id, DeploymentEnvironment.Staging, true, "trying it out", null);

        var staging = await service.ForAsync(DeploymentEnvironment.Staging);
        var production = await service.ForAsync(DeploymentEnvironment.Production);

        Assert.True(staging["invoices.usd"]);
        Assert.False(production["invoices.usd"]);
    }

    /// <summary>Moving a flag needs a reason, and the reason is kept.</summary>
    /// <remarks>
    /// The most useful thing in this feature. A flag moved at two in the morning with no note is
    /// the most confusing artefact an incident review can meet: the timeline says the harm
    /// stopped and nothing says why.
    /// </remarks>
    [Fact]
    public async Task Moving_a_flag_needs_a_reason_and_keeps_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("pricing.new", "The new pricing engine");

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetAsync(
                flag.Id, DeploymentEnvironment.Production, true, "  ", null));

        await service.SetAsync(
            flag.Id,
            DeploymentEnvironment.Production,
            false,
            "the invoice PDF comes out blank in USD",
            null);

        var change = Assert.Single(flag.Changes);

        Assert.Equal("the invoice PDF comes out blank in USD", change.Why);
        Assert.False(change.On);
    }

    /// <summary>
    /// Setting a flag to what it already is records nothing.
    /// </summary>
    /// <remarks>
    /// So that a screen posting a whole form does not fill the history with changes nobody
    /// made — which would bury the two that mattered under forty that did not.
    /// </remarks>
    [Fact]
    public async Task Setting_it_to_what_it_already_is_records_nothing()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("invoices.usd", "Invoices in US dollars");

        await service.SetAsync(
            flag.Id, DeploymentEnvironment.Production, false, "no change at all", null);

        Assert.Empty(flag.Changes);
    }

    /// <summary>A retired flag is not served, and cannot be moved.</summary>
    /// <remarks>
    /// Not served, so an application still asking falls back to its own default, which is what
    /// a flag's absence has to mean. Not deleted, so the history of what happened to it stays —
    /// deleting a flag an application still reads is how a feature comes back on in production
    /// without anybody deploying anything.
    /// </remarks>
    [Fact]
    public async Task A_retired_flag_is_not_served_and_cannot_be_moved()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("invoices.usd", "Invoices in US dollars");

        await service.SetAsync(
            flag.Id, DeploymentEnvironment.Production, true, "shipping it", null);

        await service.RetireAsync(flag.Id);

        var production = await service.ForAsync(DeploymentEnvironment.Production);

        Assert.DoesNotContain("invoices.usd", production.Keys);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetAsync(
                flag.Id, DeploymentEnvironment.Production, false, "second thoughts", null));

        Assert.Equal(1, await context.Flags.CountAsync());
    }

    /// <summary>
    /// What moved in a window, which is what an incident reads.
    /// </summary>
    /// <remarks>
    /// The reason the history exists at all. An incident's "what changed just before" could see
    /// deployments and releases and not the thing somebody actually did at 02:14 — and the
    /// window has to exclude what happened afterwards, because a flag moved after something
    /// broke did not break it.
    /// </remarks>
    [Fact]
    public async Task What_moved_in_a_window_is_what_moved_in_that_window()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var flag = await service.AddAsync("pricing.new", "The new pricing engine");

        await service.SetAsync(
            flag.Id, DeploymentEnvironment.Production, true, "shipping it", null);

        var wentWrong = fixture.Clock.Now.AddMinutes(30);

        fixture.Clock.Advance(TimeSpan.FromHours(2));

        await service.SetAsync(
            flag.Id, DeploymentEnvironment.Production, false, "turning it back off", null);

        var before = await service.MovedBetweenAsync(wentWrong.AddHours(-4), wentWrong);

        var only = Assert.Single(before);

        Assert.Equal("shipping it", only.Why);
        Assert.True(only.On);
    }

    private static FlagService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new FlagRepository(context), fixture.Clock);
}
