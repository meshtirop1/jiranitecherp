using System.Reflection;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Every kind of event a payload can be read as is acted on by the dispatcher.
/// </summary>
/// <remarks>
/// The test <see cref="DeliveryDispatcher"/>'s default arm names, and it exists because
/// the fault it guards against is a silent one. WaitingDeliveriesAsync hands back every
/// delivery still Received or Failed, so a delivery that passes through that switch
/// without being marked Handled, Ignored or Failed is read again on the next pass, and
/// the one after that, for ever — no error, no attempt counted, and nothing on the
/// deliveries screen to suggest anything is wrong. Adding a case to <see cref="GitEvent"/>
/// and forgetting the arm is the whole of what it takes, and nothing else in the build
/// notices.
///
/// The sweep is over reflection rather than a list of cases written out here, because a
/// list would have to be kept up to date by the same person who has just forgotten the
/// switch arm. A case added to GitEvent tomorrow is put in front of the dispatcher by
/// this test on the build that follows, without anybody being reminded.
/// </remarks>
public class GitEventCoverageTests
{
    /// <summary>
    /// The pull request number every constructed case is given.
    /// </summary>
    /// <remarks>
    /// One number, used both for the int in <see cref="GitEvent.Reviewed"/> and for the
    /// pull request seeded below, because Reviewed is the single case whose handling
    /// reaches for a row that has to be there already: a review of a pull request this
    /// system has never recorded throws on purpose, so that the delivery is tried again
    /// once the two have arrived in order. Without the seeded pull request this test
    /// would report a missing Reviewed arm on every run while the arm sat there working.
    /// </remarks>
    private const int Number = 412;

    private const string Sha = "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5";

    private const string Branch = "feature/412-payment-api";

    private static readonly DateTimeOffset At =
        new(2026, 9, 22, 14, 3, 11, TimeSpan.FromHours(3));

    /// <summary>
    /// No event leaves its delivery in the queue.
    /// </summary>
    /// <remarks>
    /// Every case is collected and reported together rather than asserted one at a time,
    /// so that somebody who has added two events and wired up neither is told about both
    /// on the first run instead of on two runs.
    /// </remarks>
    [Fact]
    public async Task Every_git_event_leaves_its_delivery_out_of_the_waiting_queue()
    {
        var stranded = new List<string>();

        foreach (var one in Cases())
        {
            if (await LeftWaiting(one) is { } state)
            {
                stranded.Add($"{one.GetType().Name} ({state})");
            }
        }

        Assert.True(
            stranded.Count == 0,
            "These events left their delivery waiting, so it will be read again on every "
            + "pass and whatever it described will never be recorded. Each one needs an "
            + "arm in DeliveryDispatcher's switch: "
            + string.Join(", ", stranded));
    }

    /// <summary>
    /// Nothing outside GitEvent.cs can be a GitEvent.
    /// </summary>
    /// <remarks>
    /// What makes the sweep above sufficient rather than merely thorough. The base
    /// record's own constructor is private, so a case cannot be declared anywhere except
    /// inside GitEvent itself, and walking its nested types is therefore walking every
    /// event the dispatcher can ever be handed.
    ///
    /// Were that constructor widened to protected — one word, and a plausible thing to do
    /// while adding a case in a hurry — a GitEvent could be declared in another file
    /// altogether, the walk over the nested types would go on passing, and the new case
    /// would reach the default arm in production having been reported as covered here.
    /// </remarks>
    [Fact]
    public void A_git_event_can_only_be_one_of_the_cases_declared_inside_it()
    {
        var open = typeof(GitEvent)
            .GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => !constructor.IsPrivate && !IsCopy(constructor))
            .ToList();

        Assert.True(
            open.Count == 0,
            "GitEvent can now be inherited from outside its own file, so a walk over its "
            + "nested types is no longer a walk over every case there is: "
            + string.Join(", ", open.Select(constructor => constructor.ToString())));

        Assert.Equal(
            Cases().Select(one => one.GetType().Name).Order(),
            typeof(GitEvent).Assembly
                .GetTypes()
                .Where(type => type.IsSubclassOf(typeof(GitEvent)))
                .Select(type => type.Name)
                .Order());
    }

    /// <summary>
    /// The compiler's copy constructor, which is not a way in.
    /// </summary>
    /// <remarks>
    /// Every record has one, and on a record that is not sealed it is protected rather
    /// than private, so it would otherwise read here as a constructor somebody outside
    /// could reach. It takes a GitEvent, which means it can only copy a case that already
    /// exists: a newly derived record still has to call a base constructor that is not
    /// the copy, and the only one of those is private.
    /// </remarks>
    private static bool IsCopy(ConstructorInfo constructor) =>
        constructor.GetParameters() is [var only] && only.ParameterType == typeof(GitEvent);

    /// <summary>One instance of each case the union declares.</summary>
    private static List<GitEvent> Cases() =>
        [.. typeof(GitEvent)
            .GetNestedTypes()
            .Where(nested => nested.IsSubclassOf(typeof(GitEvent)))
            .Select(nested => (GitEvent)Plausible(nested, nested.Name))];

    /// <summary>
    /// One dispatcher pass over one delivery read as this event. Returns what is still
    /// waiting afterwards, or null when nothing is.
    /// </summary>
    /// <remarks>
    /// A database of its own per case, and that matters more than it looks. The cases
    /// share a pull request number and a commit hash, so on one shared database an
    /// earlier case's writes would answer a later case's lookups — Reviewed would find
    /// the pull request PullRequestChanged had just created, and would go on passing
    /// after somebody had deleted the arm that creates it.
    ///
    /// What is asked afterwards is the repository query the dispatcher's own loop uses,
    /// rather than the delivery's status, because being picked up again is the fault
    /// itself. Failed counts as waiting on purpose: the default arm turns a missing case
    /// into a throw, so a case with no arm arrives back here as a delivery that will be
    /// retried four more times and then dead-letter, which is a different silence and not
    /// a better one.
    /// </remarks>
    private static async Task<string?> LeftWaiting(GitEvent read)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        await using (var seed = fixture.NewContext())
        {
            var repository = Repository.Connect(
                GitProvider.GitHub,
                "jiranisokotech",
                "erp",
                null,
                new string('0', 64),
                fixture.Clock.Now);

            seed.Repositories.Add(repository);
            await seed.SaveChangesAsync();

            seed.PullRequests.Add(PullRequest.Opened(
                repository.Id,
                Number,
                "Payment API retries",
                Branch,
                "meshtirop1",
                null,
                fixture.Clock.Now));

            // The body is never read here — the adapter below answers with the case under
            // test whatever it is handed — but a delivery is stored with one anyway,
            // because a delivery carrying no body is not one this system would ever have
            // accepted, and a fixture that could not have happened proves less.
            seed.Deliveries.Add(WebhookDelivery.Receive(
                GitProvider.GitHub,
                "d-1",
                "the-event",
                "{}",
                repository.Id,
                fixture.Clock.Now));

            await seed.SaveChangesAsync();
        }

        await using (var running = fixture.NewContext())
        {
            await new DeliveryDispatcher(
                    new EngineeringRepository(running),
                    new WorkRepository(running),
                    [new AdapterThatReads(read)],
                    fixture.Clock,
                    NullLogger<DeliveryDispatcher>.Instance)
                .RunOnceAsync();
        }

        await using var after = fixture.NewContext();

        var waiting = await new EngineeringRepository(after).WaitingDeliveriesAsync(50);

        return waiting.Count == 0
            ? null
            : $"{waiting[0].Status}: {waiting[0].Error ?? "nothing was recorded against it"}";
    }

    /// <summary>
    /// A value of the wanted type, plausible enough for the domain to accept it.
    /// </summary>
    /// <remarks>
    /// Built from the constructor's parameters rather than by naming each case, so that a
    /// case added later is constructed without this file being touched — which is the
    /// whole of what makes the sweep automatic.
    ///
    /// It throws rather than substituting a default for a type it does not recognise, and
    /// that is the honest behaviour: a null branch or an empty sha is refused by the
    /// aggregates' own guards, so the delivery is marked Failed and the run would report
    /// a missing dispatcher arm when what is missing is a line in here.
    /// </remarks>
    private static object Plausible(Type wanted, string named)
    {
        var type = Nullable.GetUnderlyingType(wanted) ?? wanted;

        if (type == typeof(string))
        {
            return Text(named);
        }

        if (type == typeof(int))
        {
            return Number;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(DateTimeOffset))
        {
            return At;
        }

        if (type.IsEnum)
        {
            return Enum.Parse(type, Enum.GetNames(type)[0]);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var one = Array.CreateInstance(element, 1);

            one.SetValue(Plausible(element, named), 0);

            return one;
        }

        if (type.GetConstructors() is [{ } only])
        {
            return only.Invoke([
                .. only.GetParameters()
                    .Select(parameter => Plausible(parameter.ParameterType, parameter.Name!))
            ]);
        }

        throw new InvalidOperationException(
            $"GitEventCoverageTests cannot make a plausible {type.Name} for '{named}'. Add "
            + "it to Plausible, because until it is there this case cannot be put in front "
            + "of the dispatcher and the arm that handles it is checked by nothing.");
    }

    /// <summary>
    /// What a real delivery would have carried in a field of this name.
    /// </summary>
    /// <remarks>
    /// Named rather than one string for every parameter, because several of these are
    /// read by the code under test and a wrong-looking one sends it elsewhere: a branch
    /// with no work reference in it takes a different path through WorkItemFor than one
    /// with, and a blank anything at all is refused outright by the aggregate's Required
    /// guard, which surfaces as a delivery left Failed and reads exactly like a missing
    /// arm.
    ///
    /// The fallback is a sentence and not an empty string for that same reason: a field
    /// nobody here anticipated should still reach the domain as something it accepts, so
    /// that a run reports on the dispatcher rather than on this file.
    /// </remarks>
    private static string Text(string named) => named.ToLowerInvariant() switch
    {
        "sha" => Sha,
        "branch" => Branch,
        "externalid" => "1387294561",
        "url" => "https://github.com/jiranisokotech/erp/actions/runs/1387294561",
        "author" or "reviewer" or "deployedby" => "meshtirop1",
        "environmentname" => "production",
        "message" => "Add the retry",
        "name" => "build and test",
        "title" => "Payment API retries",
        "why" => "The repository was starred, and nothing here acts on that.",
        _ => "recorded by GitEventCoverageTests",
    };

    /// <summary>
    /// An adapter that reads every delivery as the case under test.
    /// </summary>
    /// <remarks>
    /// The point of the provider seam: what the dispatcher does with an event does not
    /// depend on which host sent it, so a case can be put in front of it without a
    /// payload that some real provider would have to be persuaded to produce.
    ///
    /// Writing GitHub JSON for each case instead would be testing GitHubProvider's
    /// reading, which GitHubPayloadTests already does, and it would leave any case no
    /// provider yet sends a payload for — the next one somebody adds — with no coverage
    /// at all, which is the gap this file exists to close.
    /// </remarks>
    private sealed class AdapterThatReads(GitEvent read) : IGitProvider
    {
        public GitProvider Provider => GitProvider.GitHub;

        public bool IsSigned(ReadOnlySpan<byte> body, string? signature, string secret) =>
            true;

        public string? DeliveryIdIn(
            IReadOnlyDictionary<string, string> headers, string payload) => "d-1";

        public string? EventIn(
            IReadOnlyDictionary<string, string> headers, string payload) => "the-event";

        public string? SignatureIn(IReadOnlyDictionary<string, string> headers) => null;

        public string? RepositoryIn(string payload) => "jiranisokotech/erp";

        public GitEvent Read(string eventName, string payload) => read;
    }
}
