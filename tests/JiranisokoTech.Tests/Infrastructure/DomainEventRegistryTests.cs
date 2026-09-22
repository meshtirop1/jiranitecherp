using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Messaging;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// Turning a stored name back into the type it came from.
/// </summary>
public class DomainEventRegistryTests
{
    [Fact]
    public void A_stored_name_finds_its_type()
    {
        var registry = DomainEventRegistry.From([typeof(GadgetMade), typeof(GadgetRenamed)]);

        Assert.Equal(typeof(GadgetMade), registry.Find(nameof(GadgetMade)));
        Assert.Equal(typeof(GadgetRenamed), registry.Find(nameof(GadgetRenamed)));
    }

    /// <summary>
    /// Null is an answer rather than a failure: a row written before an event
    /// class was deleted can never be rebuilt, and the dispatcher abandons those
    /// instead of retrying them forever.
    /// </summary>
    [Fact]
    public void A_name_this_build_does_not_have_is_simply_not_found()
    {
        var registry = DomainEventRegistry.From([typeof(GadgetMade)]);

        Assert.Null(registry.Find("SomethingDeletedInVersionTwo"));
    }

    /// <summary>
    /// The cost of storing a plain name instead of an assembly-qualified one,
    /// paid at startup rather than at dispatch.
    /// </summary>
    /// <remarks>
    /// A process that will not start is a bad morning. A dispatcher that hands
    /// one namespace's PaymentReceived payload to the other namespace's handler
    /// is a bad quarter, and nothing in a log would say so.
    /// </remarks>
    [Fact]
    public void Two_events_sharing_a_name_are_refused()
    {
        var clash = Assert.Throws<InvalidOperationException>(() =>
            DomainEventRegistry.From([typeof(Warehouse.Dispatched), typeof(Invoicing.Dispatched)]));

        Assert.Contains("Dispatched", clash.Message);
        Assert.Contains("Warehouse", clash.Message);
        Assert.Contains("Invoicing", clash.Message);
    }

    /// <summary>
    /// Listing a type twice is not a clash — one assembly can legitimately be
    /// handed to the scan more than once.
    /// </summary>
    [Fact]
    public void The_same_type_listed_twice_is_not_a_clash()
    {
        var registry = DomainEventRegistry.From([typeof(GadgetMade), typeof(GadgetMade)]);

        Assert.Equal(typeof(GadgetMade), registry.Find(nameof(GadgetMade)));
    }

    /// <summary>
    /// The real assembly scan, over the real domain: it must not already
    /// contain a pair that would stop the application starting.
    /// </summary>
    [Fact]
    public void The_domain_assembly_has_no_clashing_events()
    {
        var registry = DomainEventRegistry.Build(typeof(IDomainEvent).Assembly);

        Assert.NotNull(registry);
    }
}
