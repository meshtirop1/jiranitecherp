using JiranisokoTech.Application.Support;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Search;
using JiranisokoTech.Infrastructure.Support;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Priority = JiranisokoTech.Domain.Work.Priority;

namespace JiranisokoTech.Tests.Support;

/// <summary>
/// Somebody asked for help, and whether the firm can be shown to have answered.
/// </summary>
/// <remarks>
/// Section 26. Almost every test here is about one of two things — what stops the response
/// clock, and which words leave the building — and that is the section being asserted rather
/// than an accident of what was easy to write.
///
/// Both have the same shape: the cheap mistake and the expensive mistake look identical on the
/// screen. A note to colleagues and an answer to a client are two sentences in one list, and a
/// help desk that lets the first one stop the customer's clock reports answering people it has
/// not answered. Nothing about the rendered page could tell the difference.
/// </remarks>
public class TicketTests
{
    private static readonly DateTimeOffset Nine =
        new(2026, 9, 21, 9, 0, 0, TimeSpan.FromHours(3));

    private static readonly Guid Anna = Guid.CreateVersion7();

    [Fact]
    public void A_ticket_carries_the_promise_that_was_made_when_it_arrived()
    {
        var ticket = Blocking();

        Assert.Equal("S1", ticket.Reference);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(Nine.AddHours(4), ticket.RespondBy);
        Assert.Equal(Nine.AddDays(2), ticket.ResolveBy);

        // The opening line is the requester's own words, so it carries no author.
        var opening = Assert.Single(ticket.Messages);

        Assert.Equal(Audience.Requester, opening.Audience);
        Assert.Null(opening.ByEmployeeId);
    }

    /// <summary>
    /// The promise does not move when the rules do.
    /// </summary>
    /// <remarks>
    /// The reason <see cref="Ticket.RespondBy"/> is stored rather than computed on read. A firm
    /// that recomputed it would silently re-promise every open ticket the moment somebody edited
    /// the targets — in whichever direction suited the firm, and with nothing anywhere saying it
    /// had happened.
    ///
    /// Asserted by changing the priority, which is the only route by which the stored promise is
    /// allowed to move at all.
    /// </remarks>
    [Fact]
    public void Nothing_but_a_reprioritisation_moves_the_promise()
    {
        var ticket = Blocking();
        var promised = ticket.RespondBy;

        ticket.Note("Looking at it.", Anna, Nine.AddMinutes(10));
        ticket.Assign(Anna);
        ticket.Reply("We are on it.", Anna, Nine.AddMinutes(20));

        Assert.Equal(promised, ticket.RespondBy);
    }

    /// <summary>
    /// <b>An internal note is not an answer.</b>
    /// </summary>
    /// <remarks>
    /// The single most important test in this section, and the fault it guards is the one most
    /// help desks ship: a queue where writing "looking into it" to your colleagues stops the
    /// customer's clock is a queue that reports answering people it has not answered. Nobody
    /// notices, because the report is flattering and the thread looks busy.
    /// </remarks>
    [Fact]
    public void An_internal_note_is_not_an_answer()
    {
        var ticket = Blocking();

        ticket.Note("Looks like their DNS. Asking Brian.", Anna, Nine.AddMinutes(5));
        ticket.Note("Brian agrees.", Anna, Nine.AddHours(1));

        Assert.Null(ticket.FirstRespondedAt);
        Assert.True(ticket.ResponseOverdue(Nine.AddHours(5)));

        // And it is still open with the firm: a note moves nothing.
        Assert.Equal(TicketStatus.Open, ticket.Status);
    }

    [Fact]
    public void Saying_something_to_the_person_who_asked_is_what_stops_the_clock()
    {
        var ticket = Blocking();

        ticket.Reply("We can see it, and we are changing the record now.", Anna, Nine.AddHours(1));

        Assert.Equal(Nine.AddHours(1), ticket.FirstRespondedAt);
        Assert.False(ticket.ResponseOverdue(Nine.AddHours(5)));
    }

    /// <summary>
    /// The second answer does not improve the first.
    /// </summary>
    /// <remarks>
    /// <see cref="Ticket.FirstRespondedAt"/> is set once. A ticket answered late and then
    /// answered again must not come out of the report looking prompt.
    /// </remarks>
    [Fact]
    public void Only_the_first_answer_counts_as_the_first_answer()
    {
        var ticket = Blocking();

        ticket.Reply("Sorry for the delay.", Anna, Nine.AddDays(1));
        ticket.Reply("Fixed now.", Anna, Nine.AddDays(1).AddHours(1));

        Assert.Equal(Nine.AddDays(1), ticket.FirstRespondedAt);
        Assert.True(Nine.AddDays(1) > ticket.RespondBy);
    }

    /// <summary>
    /// Reopening does not give the firm a second first answer.
    /// </summary>
    /// <remarks>
    /// The number measuring how fast the firm answers must not be improved by the customer
    /// having to come back. It is the one direction in which this clock could be gamed without
    /// anybody writing a line of dishonest code.
    /// </remarks>
    [Fact]
    public void Reopening_does_not_give_the_firm_a_second_first_answer()
    {
        var ticket = Blocking();

        ticket.Reply("Try it now.", Anna, Nine.AddHours(1));
        ticket.Resolve("It was the certificate. Renewed.", Anna, Nine.AddHours(2));

        ticket.Reopen("They rang to say it is still happening.", Anna, Nine.AddDays(1));
        ticket.Reply("Looking again.", Anna, Nine.AddDays(1).AddHours(6));

        Assert.Equal(Nine.AddHours(1), ticket.FirstRespondedAt);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ResolvedAt);
    }

    /// <summary>
    /// Moving a ticket to the requester does not pause anything.
    /// </summary>
    /// <remarks>
    /// A clock that stopped when somebody moved a ticket to "with the requester" is a clock
    /// stopped by moving tickets to "with the requester". So the state is a statement about who
    /// is expected to act, and the promise to settle it goes on running underneath.
    /// </remarks>
    [Fact]
    public void A_ticket_waiting_on_the_requester_is_still_running_out_of_time()
    {
        var ticket = Blocking();

        ticket.Reply("Which browser?", Anna, Nine.AddHours(1), waitingOnThem: true);

        Assert.Equal(TicketStatus.WithRequester, ticket.Status);
        Assert.True(ticket.ResolutionOverdue(Nine.AddDays(3)));
    }

    /// <summary>
    /// <b>What the requester may read is only what was said to them.</b>
    /// </summary>
    /// <remarks>
    /// The other half of this section's whole reason for existing. A work item's comments have
    /// one audience and nowhere to record that something was said to somebody outside the firm;
    /// here the difference decides what leaves the building, so it is which of two methods was
    /// called rather than a checkbox somebody forgets to tick at three in the afternoon.
    /// </remarks>
    [Fact]
    public void What_the_requester_may_read_is_only_what_was_said_to_them()
    {
        var ticket = Blocking();

        ticket.Note("Their own script is doing this. Do not say so yet.", Anna, Nine.AddHours(1));
        ticket.Reply("We have found the cause and are writing it up.", Anna, Nine.AddHours(2));

        Assert.Equal(3, ticket.Messages.Count);

        var theirs = ticket.AsTheySeeIt;

        Assert.Equal(2, theirs.Count);
        Assert.DoesNotContain(theirs, one => one.Audience == Audience.Inside);
        Assert.DoesNotContain(theirs, one => one.Text.Contains("Do not say so"));
    }

    /// <summary>
    /// Changing the priority recomputes the promise from when it arrived.
    /// </summary>
    /// <remarks>
    /// Not from now. Correcting a priority somebody typed wrongly must give the promise that
    /// should have been made — a fresh four hours starting from the correction would mean a
    /// ticket already late became on time because somebody fixed a dropdown.
    ///
    /// And it writes a line in the thread, because the targets are stored: moving them silently
    /// would leave a ticket answered late looking answered on time, with nothing anywhere to say
    /// the goalposts had shifted.
    /// </remarks>
    [Fact]
    public void Changing_the_priority_recomputes_the_promise_from_when_it_arrived_and_says_so()
    {
        var ticket = Ticket.Raise(
            7, "How do I export this?", "Asking about the CSV.",
            TicketPriority.Asking, Requester.Colleague, Anna, Nine);

        Assert.Equal(Nine.AddDays(2), ticket.RespondBy);

        ticket.Reprioritise(TicketPriority.Blocking, Anna, Nine.AddHours(3));

        Assert.Equal(Nine.AddHours(4), ticket.RespondBy);
        Assert.Equal(Nine.AddDays(2), ticket.ResolveBy);

        var said = Assert.Single(ticket.Messages, one => one.Audience == Audience.Inside);

        Assert.Contains("blocking", said.Text);
        Assert.Equal(Anna, said.ByEmployeeId);
    }

    [Fact]
    public void Changing_the_priority_to_the_one_it_already_has_writes_nothing()
    {
        var ticket = Blocking();

        ticket.Reprioritise(TicketPriority.Blocking, Anna, Nine.AddHours(3));

        Assert.DoesNotContain(ticket.Messages, one => one.Audience == Audience.Inside);
    }

    /// <summary>
    /// Settling a ticket means telling the person who asked.
    /// </summary>
    /// <remarks>
    /// A resolve button that closes a ticket without saying anything is the one control in a
    /// help desk that makes somebody certain they were ignored — and a month later it is
    /// indistinguishable from somebody clearing the queue before going on holiday.
    /// </remarks>
    [Fact]
    public void Settling_a_ticket_says_something_to_the_person_who_asked()
    {
        var ticket = Blocking();

        Assert.Throws<ArgumentException>(() => ticket.Resolve("   ", Anna, Nine.AddHours(1)));

        ticket.Resolve("The certificate had expired. Renewed, and set to renew itself.",
            Anna, Nine.AddHours(2));

        Assert.True(ticket.IsResolved);
        Assert.Equal(Nine.AddHours(2), ticket.ResolvedAt);

        var last = ticket.AsTheySeeIt[^1];

        Assert.Equal(Audience.Requester, last.Audience);
        Assert.Contains("certificate", last.Text);

        // Settling it is also the first answer, when nothing was said before it.
        Assert.Equal(Nine.AddHours(2), ticket.FirstRespondedAt);
    }

    [Fact]
    public void A_ticket_cannot_be_settled_twice()
    {
        var ticket = Blocking();

        ticket.Resolve("Done.", Anna, Nine.AddHours(1));

        Assert.Throws<InvalidOperationException>(
            () => ticket.Resolve("Done again.", Anna, Nine.AddHours(2)));
    }

    /// <summary>A resolved ticket the requester answers is open again.</summary>
    /// <remarks>
    /// There is no Closed beside Resolved. It exists in other tools to stop the clock and to
    /// stop the requester replying, and this design refuses both — which is only true if their
    /// reply actually reopens it.
    /// </remarks>
    [Fact]
    public void A_resolved_ticket_the_requester_answers_is_open_again()
    {
        var ticket = Blocking();

        ticket.Resolve("Renewed the certificate.", Anna, Nine.AddHours(1));
        ticket.TheySaid("It is still failing on the mobile app.", Nine.AddHours(4));

        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Null(ticket.ResolvedAt);

        // Their words carry no author, because there is no account for a person on a telephone.
        Assert.Null(ticket.AsTheySeeIt[^1].ByEmployeeId);
    }

    /// <summary>
    /// A colleague's ticket is not filed against a client.
    /// </summary>
    /// <remarks>
    /// The identifier on a ticket points at one of two tables, so a caller holding both a
    /// requester and a client can hand over a pair that does not belong together. The aggregate
    /// drops the client rather than trusting it, because a colleague's ticket appearing on a
    /// client's account is the kind of wrongness somebody finds while reading it out loud to
    /// that client.
    /// </remarks>
    [Fact]
    public void A_colleagues_ticket_is_never_filed_against_a_client()
    {
        var client = Guid.CreateVersion7();

        var mine = Ticket.Raise(
            1, "Laptop is slow", "It takes ten minutes to boot.",
            TicketPriority.Slowing, Requester.Colleague, Anna, Nine, client);

        var theirs = Ticket.Raise(
            2, "Portal is down", "We cannot log in.",
            TicketPriority.Blocking, Requester.ClientContact, Guid.CreateVersion7(), Nine, client);

        Assert.Null(mine.ClientId);
        Assert.Equal(client, theirs.ClientId);
    }

    [Fact]
    public void Answering_the_requester_raises_the_event_that_carries_the_words()
    {
        var ticket = Blocking();

        ticket.Note("Nothing to see here.", Anna, Nine.AddMinutes(5));

        // A note raises nothing. Only what leaves the building is worth an event.
        Assert.Empty(ticket.Events);

        ticket.Reply("We have renewed the certificate.", Anna, Nine.AddHours(1));

        var answered = Assert.Single(ticket.Events.OfType<TicketAnswered>());

        Assert.Equal("S1", answered.Reference);
        Assert.Contains("certificate", answered.Said);
        Assert.Equal(Anna, answered.ByEmployeeId);
    }

    [Fact]
    public void A_ticket_refuses_to_become_a_second_piece_of_work()
    {
        var ticket = Blocking();
        var item = Guid.CreateVersion7();

        ticket.BecameWork(item);

        Assert.Equal(item, ticket.WorkItemId);
        Assert.Throws<InvalidOperationException>(() => ticket.NotAlreadyWork());
        Assert.Throws<InvalidOperationException>(() => ticket.BecameWork(Guid.CreateVersion7()));
    }

    [Fact]
    public void Every_priority_promises_something()
    {
        foreach (var priority in Enum.GetValues<TicketPriority>())
        {
            var (respond, resolve) = Ticket.TargetsFor(priority);

            Assert.True(respond > TimeSpan.Zero);
            Assert.True(resolve > respond);
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Ticket.TargetsFor((TicketPriority)99));
    }

    /// <summary>
    /// The number comes from the highest one used, not from a count.
    /// </summary>
    /// <remarks>
    /// The discipline every other numbered thing here follows. A count is wrong the moment a row
    /// is ever removed, and it is wrong quietly: the next ticket takes a number that is already
    /// on somebody's telephone note.
    /// </remarks>
    [Fact]
    public async Task Ticket_numbers_come_from_the_highest_used_and_not_from_a_count()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        await Raise(fixture, anna, "First");
        var second = await Raise(fixture, anna, "Second");
        await Raise(fixture, anna, "Third");

        await using (var context = fixture.NewContext())
        {
            context.Tickets.Remove(
                await context.Tickets.SingleAsync(one => one.Id == second.Id));

            await context.SaveChangesAsync();
        }

        var next = await Raise(fixture, anna, "Fourth");

        Assert.Equal(4, next.Number);
    }

    /// <summary>
    /// The queue is ordered by the promise, not by when things arrived.
    /// </summary>
    /// <remarks>
    /// The one decision the page is arranged around. Somebody sitting down to the desk needs the
    /// order the promises run out in; the order things were typed is the order that makes the
    /// oldest unanswered ticket the hardest one to see.
    /// </remarks>
    [Fact]
    public async Task The_queue_puts_the_soonest_promise_first_whatever_arrived_first()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        await Raise(fixture, anna, "A question about exports", TicketPriority.Asking);

        fixture.Clock.Now = Nine.AddHours(1);
        await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);

        await using var context = fixture.NewContext();

        var queue = await new SupportQueries(context).QueueAsync(Nine.AddHours(2));

        Assert.Equal("Nobody can log in", queue[0].Subject);
        Assert.Equal("A question about exports", queue[1].Subject);
    }

    /// <summary>
    /// The count in the navigation clears when somebody answers.
    /// </summary>
    /// <remarks>
    /// A number beside a link that does not clear trains people to ignore it, and then it is
    /// ignored on the day it matters. This one counts only promises already broken, so it clears
    /// the moment somebody says anything to whoever asked — which is exactly the act it exists
    /// to provoke.
    /// </remarks>
    [Fact]
    public async Task The_count_beside_the_link_clears_when_somebody_answers()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        var ticket = await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);

        var late = Nine.AddHours(5);

        await using (var context = fixture.NewContext())
        {
            Assert.Equal(1, await new SupportQueries(context).UnansweredAsync(late));
        }

        fixture.Clock.Now = late;
        await Service(fixture).ReplyAsync(ticket.Id, "We can see it.", anna);

        await using (var context = fixture.NewContext())
        {
            Assert.Equal(0, await new SupportQueries(context).UnansweredAsync(late));
        }
    }

    /// <summary>
    /// The queue names a client's contact, and a contact who has left is not offered.
    /// </summary>
    /// <remarks>
    /// The contact's name comes from a second query, because the column it is keyed on has no
    /// foreign key behind it — the price of one column pointing at two tables. A join is not
    /// available, so the thing worth asserting is that the name arrives at all.
    /// </remarks>
    [Fact]
    public async Task A_client_contacts_ticket_carries_their_name_and_their_clients()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var (client, contact, gone) = await AClientWithTwoContacts(fixture);

        fixture.Clock.Now = Nine;

        await Service(fixture).RaiseAsync(
            "Portal is down", "We cannot log in at all.", TicketPriority.Blocking,
            Requester.ClientContact, contact, client);

        await using var context = fixture.NewContext();
        var queries = new SupportQueries(context);

        var row = Assert.Single(await queries.QueueAsync(Nine.AddHours(1)));

        Assert.Equal("Grace Otieno", row.Who);
        Assert.Equal("Mombasa Freight", row.Client);

        // And the contact who has left is not somebody a new ticket can be promised to.
        var offered = await queries.RequestersAsync();

        Assert.Contains(offered, one => one.Id == contact);
        Assert.DoesNotContain(offered, one => one.Id == gone);
    }

    /// <summary>
    /// Turning a request into work raises one card, and never two.
    /// </summary>
    /// <remarks>
    /// The one hand-off in this section and the place it would most easily become a second
    /// board. Asserted on the number of work items rather than on the exception, because the
    /// failure being prevented is an orphan card: raising the work first and letting the ticket
    /// refuse afterwards leaves something on the board with nothing pointing at it.
    /// </remarks>
    [Fact]
    public async Task Turning_a_request_into_work_raises_one_card_and_never_two()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        var ticket = await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);

        var item = await Service(fixture).MakeWorkAsync(ticket.Id, anna);

        Assert.Contains("S1", item.Title);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fixture).MakeWorkAsync(ticket.Id, anna));

        await using var context = fixture.NewContext();

        Assert.Equal(1, await context.WorkItems.CountAsync());

        var stored = await context.Tickets.AsNoTracking().SingleAsync();

        Assert.Equal(item.Id, stored.WorkItemId);

        // The ticket goes on owing an answer. Making work is not answering anybody.
        Assert.Null(stored.FirstRespondedAt);
        Assert.Equal(TicketStatus.Open, stored.Status);
    }

    /// <summary>
    /// A client's complaint cannot set the priority that stops everything.
    /// </summary>
    /// <remarks>
    /// <c>Priority.Dropping</c> exists on the board because a board needs one card that stops
    /// the firm, and that decision belongs to somebody inside it looking at everything else
    /// running. So the hand-off tops out below it, and blocking arrives as high.
    /// </remarks>
    [Fact]
    public async Task A_blocking_request_arrives_on_the_board_as_high_and_not_as_dropping()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        var ticket = await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);

        var item = await Service(fixture).MakeWorkAsync(ticket.Id, anna);

        Assert.Equal(Priority.High, item.Priority);
    }

    /// <summary>
    /// The search box finds a ticket by its reference, and never by what is in the thread.
    /// </summary>
    /// <remarks>
    /// The reference half is why the prefix exists: a client reads "S1" down a telephone and the
    /// person holding it types that into the box they already have open.
    ///
    /// The thread half is the one with a cost. A search result is a fragment torn out of its
    /// context, and this box has no way to carry "who was this said to" into the result — so a
    /// private note about a client must not be matchable from it at all.
    /// </remarks>
    [Fact]
    public async Task A_ticket_is_found_by_its_reference_and_never_by_what_is_in_the_thread()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        var ticket = await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);

        await Service(fixture).NoteAsync(
            ticket.Id, "Their own certificate is expired. Do not say gormless.", anna);

        var mayRead = new HashSet<string>
        {
            JiranisokoTech.Application.Authorization.Permissions.SupportView,
        };

        await using var context = fixture.NewContext();
        var finder = new SearchQueries(context, new JiranisokoTech.Infrastructure.Authorization.Reaches(context));

        var byReference = Assert.Single(await finder.FindAsync("S1", mayRead));
        Assert.Equal(ResultKind.Ticket, byReference.Kind);
        Assert.Equal("/support/1", byReference.Href);

        /*
         * And the bare number, which is what somebody types who did not hear the letter. It
         * needs two digits to be looked for at all: the box refuses a one-character term,
         * because one character matches half the database — which is the reason the page prints
         * the prefixed form. "S1" clears that floor and "1" cannot, so the prefix is not
         * decoration, it is what makes the first few tickets findable.
         */
        await using (var seed = fixture.NewContext())
        {
            seed.Tickets.Add(Ticket.Raise(
                12, "Invoices are not arriving", "They rang about it.",
                TicketPriority.Slowing, Requester.Colleague, anna, Nine));

            await seed.SaveChangesAsync();
        }

        Assert.Empty(await finder.FindAsync("1", mayRead));

        var byNumber = Assert.Single(await finder.FindAsync("12", mayRead));
        Assert.Equal("/support/12", byNumber.Href);

        Assert.Empty(await finder.FindAsync("gormless", mayRead));

        // And nothing at all for somebody who cannot read the desk.
        Assert.Empty(await finder.FindAsync("S1", new HashSet<string>()));
    }

    /// <summary>
    /// A promise broken and then kept late is still a promise broken.
    /// </summary>
    /// <remarks>
    /// The count beside the navigation link clears the moment somebody answers, which is the whole
    /// point of it — and the queue's heading was made of that number alone, so it announced
    /// "everybody who has asked has been answered in time" on a desk where every single answer had
    /// been late. A late answer is still an answer and stops being counted, so the heading needs
    /// the other figure too.
    /// </remarks>
    [Fact]
    public async Task A_promise_broken_and_then_kept_late_is_still_counted_as_broken()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var anna = await AColleague(fixture, "Anna Wanjiru");

        fixture.Clock.Now = Nine;
        var late = await Raise(fixture, anna, "Nobody can log in", TicketPriority.Blocking);
        var prompt = await Raise(fixture, anna, "How do I export?", TicketPriority.Asking);

        // One answered five hours in against a four-hour promise, one answered straight away.
        fixture.Clock.Now = Nine.AddHours(5);
        await Service(fixture).ReplyAsync(late.Id, "Sorry for the delay.", anna);

        fixture.Clock.Now = Nine.AddHours(5).AddMinutes(1);
        await Service(fixture).ReplyAsync(prompt.Id, "File, then Export.", anna);

        await using var context = fixture.NewContext();
        var queries = new SupportQueries(context);

        // Nobody is waiting, so the number beside the link is clear — and it is not the whole story.
        Assert.Equal(0, await queries.UnansweredAsync(fixture.Clock.Now));
        Assert.Equal(1, await queries.AnsweredLateAsync());
        Assert.Equal(2, await queries.HowManyAsync());
    }

    private static Ticket Blocking() => Ticket.Raise(
        1,
        "Nobody can log in",
        "Since about eight this morning every sign-in says the certificate is wrong.",
        TicketPriority.Blocking,
        Requester.ClientContact,
        Guid.CreateVersion7(),
        Nine);

    private static Task<Ticket> Raise(
        DatabaseFixture fixture,
        Guid requester,
        string subject,
        TicketPriority priority = TicketPriority.Slowing) =>
        Service(fixture).RaiseAsync(
            subject, "They rang about it.", priority, Requester.Colleague, requester);

    private static SupportService Service(DatabaseFixture fixture)
    {
        var context = fixture.NewContext();

        return new SupportService(
            new SupportRepository(context),
            new WorkService(
                new WorkRepository(context),
                new PeopleRepository(context),
                new PlanningRepository(context),
                fixture.Clock),
            fixture.Clock);
    }

    private static async Task<Guid> AColleague(DatabaseFixture fixture, string name)
    {
        await using var context = fixture.NewContext();

        var person = Employee.Hire(name, fixture.Clock.Today, null, "Engineer");

        context.Employees.Add(person);
        await context.SaveChangesAsync();

        return person.Id;
    }

    private static async Task<(Guid Client, Guid Here, Guid Gone)> AClientWithTwoContacts(
        DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var client = Client.TakeOn("Mombasa Freight", "MFL");

        var here = Contact.At(
            client.Id, "Grace Otieno", "Operations lead", "grace@mombasafreight.co.ke",
            at: fixture.Clock.Now);

        /*
         * One contact who has left, because that is the case with a cost attached: offering
         * somebody who has gone in a list of people to promise an answer to means promising an
         * answer to nobody, and the promise would still show as missed on the desk.
         */
        var gone = Contact.At(
            client.Id, "Peter Mwangi", "Finance", "peter@mombasafreight.co.ke",
            at: fixture.Clock.Now);

        gone.Gone(fixture.Clock.Now);

        context.Clients.Add(client);
        context.Contacts.AddRange(here, gone);

        await context.SaveChangesAsync();

        return (client.Id, here.Id, gone.Id);
    }
}
