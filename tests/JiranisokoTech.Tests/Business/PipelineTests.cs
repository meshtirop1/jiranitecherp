using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Tests.Infrastructure;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Business;

/// <summary>
/// The pipeline, and the people at a client.
/// </summary>
/// <remarks>
/// Section 16 had clients, their contracts and their invoices — the whole of the relationship
/// after it has been won, and nothing about winning it. So the pipeline lived in somebody's
/// head and in a spreadsheet, and the two disagreed.
///
/// What is worth testing here is not that a stage can be set. It is the four refusals and the
/// one silent behaviour that make the difference between a pipeline somebody trusts and a
/// second spreadsheet.
/// </remarks>
public class PipelineTests
{
    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        private BusinessRepository Repository => new(_context);

        public ClientService Clients => new(Repository);

        public OpportunityService Opportunities => new(Repository, db.Clock);

        public ContactService Contacts => new(Repository, db.Clock);

        public BusinessQueries Reads => new(_context, db.Clock);

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();
    }

    // --- stages ---------------------------------------------------------------

    /// <summary>
    /// An enquiry is an opportunity at the first stage, with no client behind it.
    /// </summary>
    /// <remarks>
    /// The alternative — a Lead type that converts into an Opportunity — is the obvious
    /// design and it is wrong: the conversion step loses the notes, the dates and who first
    /// spoke to them, every time, in every system that has ever had one.
    /// </remarks>
    [Fact]
    public async Task An_enquiry_needs_nobody_to_be_a_client_first()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync(
            "Fleet tracking for the northern depots", "Acme Haulage (via Grace)");

        Assert.Equal(Stage.Enquiry, enquiry.Stage);
        Assert.Null(enquiry.ClientId);
        Assert.True(enquiry.IsOpen);
    }

    /// <summary>
    /// Moving backwards is allowed.
    /// </summary>
    /// <remarks>
    /// Deliberately. A proposal that comes back for requalification is an ordinary Tuesday,
    /// and a pipeline that only moved one way is one people work around by opening a second
    /// opportunity — which then counts twice in every total anybody reads from it.
    /// </remarks>
    [Fact]
    public async Task An_opportunity_can_go_backwards()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await module.Opportunities.MoveToAsync(enquiry.Id, Stage.Proposed);
        await module.Opportunities.MoveToAsync(enquiry.Id, Stage.Qualified);

        var row = Assert.Single(await module.Reads.PipelineAsync());

        Assert.Equal(Stage.Qualified, row.Stage);
    }

    /// <summary>
    /// One that has been decided is not reopened.
    /// </summary>
    /// <remarks>
    /// Won and lost are answers. Reopening one would make the month it was decided in depend
    /// on when somebody last looked at it, and every figure drawn from the pipeline move
    /// underneath whoever was reading it.
    /// </remarks>
    [Fact]
    public async Task A_decided_opportunity_is_not_reopened()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await module.Opportunities.MoveToAsync(enquiry.Id, Stage.Won);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Opportunities.MoveToAsync(enquiry.Id, Stage.Negotiating));

        Assert.Contains("already marked won", refused.Message);
    }

    /// <summary>
    /// Losing one demands a reason.
    /// </summary>
    /// <remarks>
    /// That sentence is the only useful thing a lost opportunity leaves behind. Without it the
    /// row says somebody said no, which nobody can act on and nobody reads twice.
    /// </remarks>
    [Fact]
    public async Task Losing_one_demands_a_reason()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Opportunities.MoveToAsync(enquiry.Id, Stage.Lost));

        await module.Opportunities.MoveToAsync(
            enquiry.Id, Stage.Lost, "Went with the incumbent on price.");

        var row = Assert.Single(await module.Reads.PipelineAsync(includeClosed: true));

        Assert.Equal(Stage.Lost, row.Stage);
        Assert.Equal("Went with the incumbent on price.", row.Outcome);
    }

    /// <summary>Winning one demands nothing, because nothing else has to be true.</summary>
    [Fact]
    public async Task Winning_one_needs_no_reason()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await module.Opportunities.MoveToAsync(enquiry.Id, Stage.Won);

        var row = Assert.Single(await module.Reads.PipelineAsync(includeClosed: true));

        Assert.Equal(Stage.Won, row.Stage);
        Assert.Null(row.Outcome);
    }

    /// <summary>
    /// Winning one creates nothing.
    /// </summary>
    /// <remarks>
    /// Taking somebody on as a client, raising a project and agreeing a contract are three
    /// decisions a person makes, each with its own consequences and its own permission. An
    /// opportunity that created all three on being marked won would be a dropdown with the
    /// authority of a signature.
    /// </remarks>
    [Fact]
    public async Task Winning_one_creates_no_client_and_no_contract()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await module.Opportunities.MoveToAsync(enquiry.Id, Stage.Won);

        Assert.Empty(await module.Reads.ClientsAsync());
        Assert.Empty(await module.Reads.ContractsAsync());
    }

    /// <summary>
    /// A client named on an opportunity has to be one.
    /// </summary>
    /// <remarks>
    /// Refused here rather than left to the foreign key, whose complaint arrives as a
    /// DbUpdateException in a screen's face.
    /// </remarks>
    [Fact]
    public async Task An_opportunity_cannot_name_a_client_that_does_not_exist()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Opportunities.OpenAsync(
                "Depot tracking", "Acme Haulage", clientId: Guid.CreateVersion7()));

        Assert.Contains("no client", refused.Message);
    }

    /// <summary>What it might be worth cannot be less than nothing.</summary>
    [Fact]
    public async Task An_opportunity_cannot_be_worth_less_than_nothing()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => module.Opportunities.UpdateAsync(
                enquiry.Id,
                "Depot tracking",
                "Acme Haulage",
                clientId: null,
                ownerId: null,
                value: Money.Of(-1, "KES"),
                expectedOn: null));
    }

    // --- silence --------------------------------------------------------------

    /// <summary>
    /// Writing down a call counts as the opportunity moving.
    /// </summary>
    /// <remarks>
    /// The one silent behaviour worth a test of its own. Without it a pipeline calls an
    /// opportunity stale while somebody is speaking to the client weekly — which is a report
    /// nobody believes twice, and once they stop believing it they stop reading it.
    /// </remarks>
    [Fact]
    public async Task Writing_down_a_call_counts_as_the_opportunity_moving()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        db.Clock.Advance(TimeSpan.FromDays(20));

        var stale = Assert.Single(await module.Reads.PipelineAsync());
        Assert.Equal(20, stale.Quiet);

        await module.Opportunities.HappenedAsync(
            enquiry.Id, ActivityKind.Call, "Spoke to their ops lead; proposal wanted Friday.");

        var moved = Assert.Single(await module.Reads.PipelineAsync());

        Assert.Equal(0, moved.Quiet);
        Assert.Equal(1, moved.Activities);
    }

    /// <summary>
    /// The pipeline is ordered by silence, not by value.
    /// </summary>
    /// <remarks>
    /// A list ordered by value shows what somebody hopes for; a list ordered by silence shows
    /// what they have stopped doing. Only one of those changes what anybody does on the
    /// afternoon they read it.
    /// </remarks>
    [Fact]
    public async Task The_pipeline_is_ordered_by_silence()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var forgotten = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        db.Clock.Advance(TimeSpan.FromDays(30));

        var fresh = await module.Opportunities.OpenAsync(
            "Warehouse scanners", "Bluebird Logistics", value: Money.Of(4_000_00, "KES"));

        var rows = await module.Reads.PipelineAsync();

        Assert.Equal([forgotten.Id, fresh.Id], rows.Select(row => row.Id));
        Assert.Equal(30, rows[0].Quiet);
        Assert.Equal(0, rows[1].Quiet);
    }

    /// <summary>
    /// Decided ones are out of the way unless asked for.
    /// </summary>
    /// <remarks>
    /// They are answers, and a pipeline carrying every answer ever given is a list nobody
    /// scrolls to the bottom of — which means the stale enquiry at the top stops being read
    /// along with everything else.
    /// </remarks>
    [Fact]
    public async Task Decided_opportunities_are_left_out_unless_asked_for()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var open = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");
        var done = await module.Opportunities.OpenAsync("Scanners", "Bluebird Logistics");

        await module.Opportunities.MoveToAsync(done.Id, Stage.Won);

        Assert.Equal(open.Id, Assert.Single(await module.Reads.PipelineAsync()).Id);
        Assert.Equal(2, (await module.Reads.PipelineAsync(includeClosed: true)).Count);
    }

    /// <summary>
    /// The activity log reads newest first, and holds everything written to it.
    /// </summary>
    /// <remarks>
    /// Newest first because the question is what happened last. There is no edit and no
    /// delete: the value of a log is that it says what was true when it was written, and one
    /// somebody can tidy afterwards is a log nobody can rely on in the conversation it exists
    /// for.
    /// </remarks>
    [Fact]
    public async Task The_log_reads_newest_first()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var enquiry = await module.Opportunities.OpenAsync("Depot tracking", "Acme Haulage");

        await module.Opportunities.HappenedAsync(enquiry.Id, ActivityKind.Call, "First call.");
        db.Clock.Advance(TimeSpan.FromDays(2));
        await module.Opportunities.HappenedAsync(enquiry.Id, ActivityKind.Sent, "Proposal sent.");

        var log = await module.Reads.ActivitiesAsync(enquiry.Id);

        Assert.Equal(["Proposal sent.", "First call."], log.Select(entry => entry.What));
    }

    // --- the people at a client ----------------------------------------------

    /// <summary>
    /// The first person recorded at a client is the one to call.
    /// </summary>
    /// <remarks>
    /// Whether or not anybody ticked the box. Otherwise a client has contacts and no main one,
    /// which reads on every screen as "nobody knows who to ring" — and is nearly always
    /// untrue.
    /// </remarks>
    [Fact]
    public async Task The_first_contact_at_a_client_is_the_one_to_call()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace", "Operations lead");

        Assert.True(grace.IsMain);
    }

    /// <summary>
    /// Only one contact at a client is the one to call first.
    /// </summary>
    /// <remarks>
    /// The rule this service exists for. The entity cannot enforce it: only something holding
    /// all of a client's contacts knows which other one to unset, and an entity asked to do it
    /// would have to be handed its own siblings.
    ///
    /// It is not a unique index either, deliberately — see the note in ContactConfiguration.
    /// Promoting somebody is one save containing two UPDATEs whose order nothing fixes, so a
    /// partial unique index would fire on whichever ordering EF happened to pick.
    /// </remarks>
    [Fact]
    public async Task Promoting_a_contact_demotes_whoever_held_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        await module.Contacts.AddAsync(client.Id, "Grace", "Operations lead");
        var paul = await module.Contacts.AddAsync(client.Id, "Paul", "Finance");

        await module.Contacts.MainIsAsync(paul.Id);

        var people = await module.Reads.ContactsAsync(client.Id);

        Assert.Equal("Paul", Assert.Single(people, one => one.IsMain).Name);
    }

    /// <summary>Somebody added as the main contact takes it from whoever held it.</summary>
    [Fact]
    public async Task Adding_somebody_as_the_main_contact_takes_it_from_whoever_held_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        await module.Contacts.AddAsync(client.Id, "Grace", "Operations lead");
        await module.Contacts.AddAsync(client.Id, "Paul", "Finance", isMain: true);

        var people = await module.Reads.ContactsAsync(client.Id);

        Assert.Equal("Paul", Assert.Single(people, one => one.IsMain).Name);
    }

    /// <summary>
    /// A contact who has left stays, and is no longer the one to call.
    /// </summary>
    /// <remarks>
    /// The record stays because the correspondence sent to them is still the correspondence,
    /// and this date explains "nobody there is replying" more often than anything else does.
    /// The flag goes because leaving it set is how an email goes to an address that bounces
    /// for a year.
    /// </remarks>
    [Fact]
    public async Task A_contact_who_has_left_stays_and_is_no_longer_the_one_to_call()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace", "Operations lead");

        await module.Contacts.GoneAsync(grace.Id);

        var person = Assert.Single(await module.Reads.ContactsAsync(client.Id));

        Assert.Equal("Grace", person.Name);
        Assert.False(person.IsHere);
        Assert.False(person.IsMain);
    }

    /// <summary>
    /// When the one to call leaves, the longest-serving of the rest becomes it.
    /// </summary>
    /// <remarks>
    /// The regression test for a fault no unit test caught and opening a client page did. The
    /// entity clears the flag when somebody leaves, which is right — leaving it set is how an
    /// email goes to an address that bounces for a year — so a client whose main contact left
    /// was left with contacts and nobody flagged.
    ///
    /// Longest-serving is an arbitrary rule and an explainable one. It is nearly always the
    /// right person, and it is one click to change on the screen that shows it.
    /// </remarks>
    [Fact]
    public async Task When_the_one_to_call_leaves_the_longest_serving_of_the_rest_becomes_it()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace", "Operations lead");

        db.Clock.Advance(TimeSpan.FromDays(1));
        await module.Contacts.AddAsync(client.Id, "Paul", "Finance");

        db.Clock.Advance(TimeSpan.FromDays(1));
        await module.Contacts.AddAsync(client.Id, "Njeri", "Procurement");

        await module.Contacts.GoneAsync(grace.Id);

        var people = await module.Reads.ContactsAsync(client.Id);

        Assert.Equal("Paul", Assert.Single(people, one => one.IsMain).Name);
    }

    /// <summary>
    /// The last person at a client leaving does not leave a flag on somebody who has gone.
    /// </summary>
    /// <remarks>
    /// The edge the promotion rule has to survive: there is nobody to promote, and the answer
    /// is nobody rather than the leaver keeping it.
    /// </remarks>
    [Fact]
    public async Task The_last_contact_leaving_leaves_nobody_to_call()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace");

        await module.Contacts.GoneAsync(grace.Id);

        var people = await module.Reads.ContactsAsync(client.Id);

        Assert.DoesNotContain(people, one => one.IsMain);
    }

    /// <summary>Somebody who has left cannot be made the one to call.</summary>
    [Fact]
    public async Task Somebody_who_has_left_cannot_be_made_the_one_to_call()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace");
        await module.Contacts.GoneAsync(grace.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Contacts.MainIsAsync(grace.Id));

        Assert.Contains("has left", refused.Message);
    }

    /// <summary>
    /// Leavers are listed after everybody who is still there.
    /// </summary>
    /// <remarks>
    /// Rather than hidden. "The person we dealt with left in March" is the answer to a
    /// question somebody is actually asking when they open a client, and a screen that hid
    /// them would send them to the audit trail for it.
    /// </remarks>
    [Fact]
    public async Task Leavers_are_listed_below_everybody_still_there()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Haulage");

        var grace = await module.Contacts.AddAsync(client.Id, "Grace");
        await module.Contacts.AddAsync(client.Id, "Paul");

        await module.Contacts.GoneAsync(grace.Id);

        var people = await module.Reads.ContactsAsync(client.Id);

        Assert.Equal(["Paul", "Grace"], people.Select(one => one.Name));
    }

    /// <summary>A contact has to be at a client that exists.</summary>
    [Fact]
    public async Task A_contact_cannot_be_at_a_client_that_does_not_exist()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Contacts.AddAsync(Guid.CreateVersion7(), "Grace"));

        Assert.Contains("no client", refused.Message);
    }

    // --- what the trail carries ----------------------------------------------

    /// <summary>
    /// The forecast and the direct line are kept out of the audit trail.
    /// </summary>
    /// <remarks>
    /// The value because it is a guess that changes weekly, and a trail carrying every
    /// revision of every forecast buries the changes somebody actually needs to find. The
    /// phone number because it belongs to a person outside the firm, and nobody administering
    /// this system needs a stranger's mobile number in an audit row.
    /// </remarks>
    [Fact]
    public void The_trail_leaves_out_the_forecast_and_the_direct_line()
    {
        Assert.Contains(nameof(Opportunity.ValueMinorUnits), Opportunity.AuditExcludes);
        Assert.Contains(nameof(Opportunity.ValueCurrency), Opportunity.AuditExcludes);
        Assert.Contains(nameof(Contact.Phone), Contact.AuditExcludes);

        // The reason it was lost is the point of keeping a lost one, so it stays.
        Assert.DoesNotContain(nameof(Opportunity.Outcome), Opportunity.AuditExcludes);
    }
}
