using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Integrations;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Integrations;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Integrations;

/// <summary>
/// A second kind of destination on the same machinery.
/// </summary>
/// <remarks>
/// Section 51 asks that new providers can be added without changing core business logic, and until
/// now that sentence was untestable here because there was one provider. The point of these tests
/// is not Slack. It is that adding Slack changed nothing that decides <em>whether</em> to notify
/// somebody — not a domain event, not the offered list, not the queue, not a retry, not a backoff,
/// not a dead letter, not the replay button — and that the dispatcher picks a sender by asking it
/// what it handles rather than by knowing about either of them.
///
/// <b>The reason it was worth doing is that the outbound half of section 40 had no possible
/// consumer.</b> It was finished, tested and marked done, and what it posts is a bespoke envelope
/// that only code somebody writes can read. A firm of twenty software engineers has not written
/// that code, so every part of it was machinery with nothing at the far end.
/// </remarks>
public class ChatDestinationTests
{
    private static readonly DateTimeOffset Nine =
        new(2026, 9, 21, 9, 0, 0, TimeSpan.FromHours(3));

    /// <summary>
    /// <b>Every kind of destination has something registered that can talk to it.</b>
    /// </summary>
    /// <remarks>
    /// The gate that makes adding a provider safe, and the one this section needs most. A value on
    /// the enum with no sender behind it is a subscription somebody sets up on a screen, which
    /// then queues, retries for two days, dead-letters and switches itself off — and the reason
    /// shown is "nothing in this application knows how to post to a Mattermost destination", which
    /// nobody reading the screen can do anything about.
    ///
    /// Derived from the assembly rather than from a list, so the next provider is covered by
    /// having been written rather than by somebody remembering this file.
    /// </remarks>
    [Fact]
    public void Every_kind_of_destination_has_a_sender_that_handles_it()
    {
        var handled = Senders().Select(one => one.Handles).ToHashSet();

        var orphaned = Enum.GetValues<DestinationKind>()
            .Where(kind => !handled.Contains(kind))
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "These destination kinds can be chosen on the screen and nothing in the application "
            + "knows how to post to them, so a subscription to one queues, retries for two days "
            + "and switches itself off: " + string.Join(", ", orphaned)
            + "\n\nWrite an IOutboundSender whose Handles returns it, and register it beside "
            + "HttpOutboundSender.");
    }

    /// <summary>Two senders never claim the same kind.</summary>
    /// <remarks>
    /// The dispatcher takes the first that matches, so two claiming one kind is a coin toss
    /// decided by registration order — which is to say by a line somebody may reorder while
    /// tidying up.
    /// </remarks>
    [Fact]
    public void No_two_senders_claim_the_same_kind_of_destination()
    {
        var shared = Senders()
            .GroupBy(one => one.Handles)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: "
                + string.Join(", ", group.Select(one => one.GetType().Name)))
            .ToList();

        Assert.True(
            shared.Count == 0,
            "These kinds have more than one sender claiming them, so which one is used depends on "
            + "registration order: " + string.Join(" · ", shared));
    }

    /// <summary>
    /// A Slack destination has to be a Slack address.
    /// </summary>
    /// <remarks>
    /// The kind decides what is posted, so a row marked Slack pointing at somebody's own server
    /// sends chat-shaped JSON at it — which that server refuses with a 4xx indistinguishable from
    /// a temporary outage, so the queue retries into a stranger's address for days before giving
    /// up. Refused in the aggregate rather than on the form, because the form is not the only
    /// caller.
    /// </remarks>
    [Fact]
    public void A_slack_destination_has_to_be_a_slack_address()
    {
        var refused = Assert.Throws<ArgumentException>(() => Subscription.Add(
            "Engineering channel",
            "https://accounts.example.com/hooks/erp",
            "protected",
            [nameof(InvoiceSent)],
            Nine,
            DestinationKind.Slack));

        Assert.Contains(Subscription.SlackHost, refused.Message);

        // And the same address is perfectly good as an ordinary webhook.
        var allowed = Subscription.Add(
            "Accounts system",
            "https://accounts.example.com/hooks/erp",
            "protected",
            [nameof(InvoiceSent)],
            Nine);

        Assert.Equal(DestinationKind.Webhook, allowed.Kind);
    }

    [Fact]
    public void A_slack_address_is_accepted_and_still_has_to_be_https()
    {
        var slack = Subscription.Add(
            "Engineering channel",
            "https://hooks.slack.com/services/T0000/B0000/xxxxxxxx",
            "protected",
            [nameof(InvoiceSent)],
            Nine,
            DestinationKind.Slack);

        Assert.Equal(DestinationKind.Slack, slack.Kind);

        Assert.Throws<ArgumentException>(() => Subscription.Add(
            "Engineering channel",
            "http://hooks.slack.com/services/T0000/B0000/xxxxxxxx",
            "protected",
            [nameof(InvoiceSent)],
            Nine,
            DestinationKind.Slack));
    }

    /// <summary>
    /// Every subscription is sent by the one sender that handles its kind.
    /// </summary>
    /// <remarks>
    /// Two destinations queued in one batch and two senders registered, so the assertion is not
    /// "the right one ran" but "each ran for its own and neither ran for the other". A dispatcher
    /// that took the first sender in the list would pass a test with one subscription in it.
    /// </remarks>
    [Fact]
    public async Task Each_destination_is_sent_by_the_sender_that_handles_its_kind()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (server, channel) = await TwoDestinationsAsync(fixture);

        var toServers = new Counting(DestinationKind.Webhook);
        var toSlack = new Counting(DestinationKind.Slack);

        await Dispatch(fixture, [toServers, toSlack]);

        Assert.Equal([server], toServers.Sent);
        Assert.Equal([channel], toSlack.Sent);
    }

    /// <summary>
    /// A destination nothing can post to gives up and switches itself off.
    /// </summary>
    /// <remarks>
    /// A missing sender is a wiring fault, not something the person reading the screen did — so
    /// the temptation is to throw. It is refused instead, because throwing inside the batch would
    /// take out the notifications queued behind it for destinations that work. The ordinary
    /// machinery then does the right thing, which this asserts: it retries, it gives up, and the
    /// subscription eventually says so on the screen.
    /// </remarks>
    [Fact]
    public async Task A_destination_with_no_sender_gives_up_rather_than_stopping_the_batch()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (server, channel) = await TwoDestinationsAsync(fixture);

        var toServers = new Counting(DestinationKind.Webhook);

        // Only the webhook sender is registered, so the Slack destination has nothing.
        await Dispatch(fixture, [toServers]);

        // The one that works went out, which is the whole point of not throwing.
        Assert.Equal([server], toServers.Sent);

        await using var after = fixture.NewContext();

        var stranded = await after.OutboundDeliveries
            .AsNoTracking()
            .SingleAsync(one => one.SubscriptionId == channel);

        Assert.Equal(OutboundStatus.Failed, stranded.Status);
        Assert.Contains("knows how to post", stranded.Error);
        Assert.NotNull(stranded.NextAttemptAt);
    }

    /// <summary>
    /// What a channel is told is a sentence and which thing it was about.
    /// </summary>
    /// <remarks>
    /// The wording comes from <c>OutboundEvents.Offered</c> — the description somebody already
    /// wrote and the screen already shows beside the checkbox — rather than from a template per
    /// event. Fourteen templates against fourteen event shapes is fourteen places where renaming
    /// a field turns the channel message into a sentence with a gap in it, silently.
    /// </remarks>
    [Fact]
    public void A_channel_is_told_the_words_somebody_already_wrote_and_which_thing_it_was()
    {
        var said = SlackOutboundSender.Say(Delivery(nameof(InvoiceSent), new
        {
            number = "JIR-0042",
            client = "Mombasa Freight",
        }));

        Assert.Contains(OutboundEvents.Offered[nameof(InvoiceSent)], said);
        Assert.Contains("JIR-0042", said);
        Assert.Contains(nameof(InvoiceSent), said);
    }

    /// <summary>
    /// A notification carrying nothing recognisable still says what happened.
    /// </summary>
    /// <remarks>
    /// The graceful end of the degradation. An event whose fields are named nothing this looks for
    /// posts its sentence and its name, which is a message somebody can act on; the silent end
    /// would be a blank line in a channel, and the loud end would be a delivery that dead-letters
    /// because the sender threw over a missing field.
    /// </remarks>
    [Fact]
    public void A_notification_with_nothing_recognisable_in_it_still_says_what_happened()
    {
        var bare = SlackOutboundSender.Say(Delivery(nameof(InvoiceSent), new { wholly = "odd" }));

        Assert.Contains(OutboundEvents.Offered[nameof(InvoiceSent)], bare);
        Assert.Contains(nameof(InvoiceSent), bare);

        // And a payload that is not the envelope at all, which a replayed old row could be.
        var malformed = SlackOutboundSender.Say(
            OutboundDelivery.Queue(Guid.CreateVersion7(), nameof(InvoiceSent), "not json", Nine));

        Assert.Contains(nameof(InvoiceSent), malformed);
    }

    /// <summary>
    /// An event nobody wrote words for says its name rather than nothing.
    /// </summary>
    /// <remarks>
    /// Reachable through the replay button: a delivery row queued under an event name that has
    /// since been taken off the offered list is still in the table and can still be retried.
    /// </remarks>
    [Fact]
    public void An_event_no_longer_on_the_offered_list_still_posts_something()
    {
        var said = SlackOutboundSender.Say(Delivery("SomethingRetired", new { name = "Whatever" }));

        Assert.Contains("SomethingRetired", said);
        Assert.Contains("Whatever", said);
    }

    private static List<IOutboundSender> Senders() =>
        [.. typeof(HttpOutboundSender).Assembly
            .GetTypes()
            .Where(type => typeof(IOutboundSender).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false })
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(Empty)];

    /// <remarks>
    /// Built with nulls, which is safe for the only thing asked of them here: <c>Handles</c> is a
    /// literal on every sender, and nothing below calls <c>SendAsync</c>.
    /// </remarks>
    private static IOutboundSender Empty(Type type)
    {
        var constructor = type.GetConstructors().Single();

        var nothing = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();

        return (IOutboundSender)constructor.Invoke(nothing);
    }

    private static OutboundDelivery Delivery(string name, object data) =>
        OutboundDelivery.Queue(
            Guid.CreateVersion7(),
            name,
            JsonSerializer.Serialize(new { @event = name, at = Nine, data }),
            Nine);

    /// <summary>One subscription of each kind, each with one notification waiting.</summary>
    private static async Task<(Guid Server, Guid Channel)> TwoDestinationsAsync(
        DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var server = Subscription.Add(
            "Accounts system", "https://accounts.example.com/hooks", "protected",
            [nameof(InvoiceSent)], fixture.Clock.Now);

        var channel = Subscription.Add(
            "Engineering channel", "https://hooks.slack.com/services/T0/B0/zzzz", "protected",
            [nameof(InvoiceSent)], fixture.Clock.Now, DestinationKind.Slack);

        context.Subscriptions.AddRange(server, channel);

        foreach (var one in new[] { server, channel })
        {
            context.OutboundDeliveries.Add(OutboundDelivery.Queue(
                one.Id, nameof(InvoiceSent), """{"event":"InvoiceSent"}""", fixture.Clock.Now));
        }

        await context.SaveChangesAsync();

        return (server.Id, channel.Id);
    }

    private static async Task Dispatch(
        DatabaseFixture fixture, IEnumerable<IOutboundSender> senders)
    {
        await using var context = fixture.NewContext();

        await new OutboundDispatcher(
                new IntegrationRepository(context),
                senders,
                new Plain(),
                fixture.Clock,
                NullLogger<OutboundDispatcher>.Instance)
            .RunOnceAsync();
    }

    /// <summary>A sender that records which subscriptions it was asked to post to.</summary>
    private sealed class Counting(DestinationKind handles) : IOutboundSender
    {
        public DestinationKind Handles => handles;

        public List<Guid> Sent { get; } = [];

        public Task<SendResult> SendAsync(
            Subscription subscription,
            OutboundDelivery delivery,
            string secret,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(subscription.Id);

            return Task.FromResult(SendResult.Ok(200));
        }
    }

    private sealed class Plain : ISecretStore
    {
        public string Protect(string plain) => plain;

        public string? Reveal(string protectedValue) => protectedValue;
    }
}
