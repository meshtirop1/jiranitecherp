using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.Notices;
using JiranisokoTech.Application.Support;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.Notices;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Support;
using JiranisokoTech.Infrastructure.Work;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace JiranisokoTech.Tests.Support;

/// <summary>
/// When the firm is late answering somebody, somebody senior hears about it — once.
/// </summary>
/// <remarks>
/// Section 26's escalation, and it is the only part of the help desk that acts without anybody
/// opening a page. That is the whole reason it exists: the person waiting is outside the firm and
/// cannot ring the bell twice.
///
/// Almost every test here is about the word <em>once</em>. The condition being swept for — nobody
/// has answered and the time to do it has gone — stays true until somebody answers, and the sweep
/// runs every half hour, so an escalation with no memory is forty-eight notices a day about one
/// ticket. That does not read as a bug. It reads as a system whose notices are worth filtering
/// into a folder, and after that the firm has no escalation at all while believing it has one.
/// </remarks>
public class EscalationTests
{
    private static readonly DateTimeOffset Nine =
        new(2026, 9, 21, 9, 0, 0, TimeSpan.FromHours(3));

    /// <summary>An author for the pure-domain cases, which need no database.</summary>
    private static readonly Guid Anna = Guid.CreateVersion7();

    [Fact]
    public async Task A_promise_still_being_kept_escalates_to_nobody()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        await ATicketAsync(fixture, firm.Anna);

        // One hour in, against a four-hour promise.
        fixture.Clock.Now = Nine.AddHours(1);

        Assert.Contains("still standing", await Sweep(fixture).RunAsync());
        Assert.Empty(await NoticesAsync(fixture));
    }

    /// <summary>
    /// An internal note is not an answer here either.
    /// </summary>
    /// <remarks>
    /// The sweep uses the same three conditions the navigation's count uses, and this is the one
    /// somebody would get wrong by reaching for "has anything been written on it lately". A
    /// colleague writing "looking into it" to other colleagues is the commonest thing on a late
    /// ticket and is exactly not the thing owed.
    /// </remarks>
    [Fact]
    public async Task A_ticket_with_notes_on_it_and_no_answer_is_still_escalated()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(2);
        await Service(fixture).NoteAsync(ticket.Id, "Looks like their DNS.", firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);

        Assert.Contains("escalated", await Sweep(fixture).RunAsync());
        Assert.NotEmpty(await NoticesAsync(fixture));
    }

    /// <summary>
    /// A ticket the firm can act on is escalated even while others have nobody to tell.
    /// </summary>
    /// <remarks>
    /// The starvation this arrangement exists to prevent. Fifty untellable tickets at the front of
    /// a fifty-row batch would mean the one the firm could act on was never reached — so the
    /// untellable ones are left out, and this proves the tellable one still goes out beside them.
    /// </remarks>
    [Fact]
    public async Task An_assigned_ticket_is_escalated_even_beside_ones_nobody_can_be_told_about()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        Guid anna, brian;

        await using (var context = fixture.NewContext())
        {
            var one = Employee.Hire("Anna Wanjiru", fixture.Clock.Today, null, "Designer");
            var two = Employee.Hire("Brian Kimani", fixture.Clock.Today, null, "Engineer");

            context.Employees.AddRange(one, two);
            await context.SaveChangesAsync();

            (anna, brian) = (one.Id, two.Id);
        }

        fixture.Clock.Now = Nine;

        // Three nobody has picked up, and one that Brian has.
        for (var i = 0; i < 3; i++)
        {
            await ATicketAsync(fixture, anna);
        }

        var his = await ATicketAsync(fixture, anna);
        await Service(fixture).AssignAsync(his.Id, brian);

        fixture.Clock.Now = Nine.AddHours(5);

        var said = await Sweep(fixture).RunAsync();

        Assert.Contains("One missed promise was escalated", said);
        Assert.Contains("3 more have nobody to tell about them", said);

        // Brian heard, and nobody heard about the other three.
        var told = await NoticesAsync(fixture);

        Assert.Equal(brian, Assert.Single(told).ForEmployeeId);
    }

    /// <summary>
    /// The person answering hears, and so does the person they answer to.
    /// </summary>
    /// <remarks>
    /// Both, because they are two different messages arriving at once: the assignee may simply
    /// not have looked at the desk today, and their manager needs to know before the client tells
    /// them. Telling only the manager would make an escalation a complaint about somebody who had
    /// not yet been asked.
    /// </remarks>
    [Fact]
    public async Task An_assigned_ticket_tells_whoever_has_it_and_whoever_they_answer_to()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);
        await Service(fixture).AssignAsync(ticket.Id, firm.Brian);

        fixture.Clock.Now = Nine.AddHours(5);
        await Sweep(fixture).RunAsync();

        var told = await NoticesAsync(fixture);

        Assert.Equal(2, told.Count);
        Assert.Contains(told, one => one.ForEmployeeId == firm.Brian);
        Assert.Contains(told, one => one.ForEmployeeId == firm.Maria);

        // Its own kind, so somebody can mute it without silencing the work handed to them.
        Assert.All(told, one => Assert.Equal(NoticeKind.PromiseMissed, one.Kind));

        // And it carries the reference and the way back to the ticket.
        Assert.All(told, one => Assert.Contains(ticket.Reference, one.Subject));
        Assert.All(told, one => Assert.Equal($"/support/{ticket.Number}", one.Link));
    }

    /// <summary>
    /// With nobody on it, the heads of the firm's departments hear.
    /// </summary>
    /// <remarks>
    /// An unanswered request that nobody has picked up is a failure of the desk rather than of a
    /// person, so there is no one person to go above. This is the smallest set that certainly
    /// contains somebody who can act, and it is used rather than nothing because there is no
    /// support lead anywhere in this schema — inventing one would be a setting nobody configures.
    /// </remarks>
    [Fact]
    public async Task An_unassigned_ticket_tells_the_heads_of_the_departments()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);
        await Sweep(fixture).RunAsync();

        var told = await NoticesAsync(fixture);

        Assert.Equal(firm.Maria, Assert.Single(told).ForEmployeeId);
    }

    /// <summary>
    /// <b>Nobody is told twice about the same missed promise.</b>
    /// </summary>
    /// <remarks>
    /// The test this whole design exists for. Two sweeps an hour apart, with nothing changed in
    /// between, and the second must find nothing — because the condition it looks for is still
    /// exactly as true as it was.
    /// </remarks>
    [Fact]
    public async Task A_missed_promise_is_escalated_once_and_not_again()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);
        Assert.Contains("One missed promise", await Sweep(fixture).RunAsync());

        fixture.Clock.Now = Nine.AddHours(6);
        Assert.Contains("still standing", await Sweep(fixture).RunAsync());

        Assert.Single(await NoticesAsync(fixture));

        await using var context = fixture.NewContext();
        var stored = await context.Tickets.AsNoTracking().SingleAsync(one => one.Id == ticket.Id);

        Assert.Equal(Nine.AddHours(5), stored.EscalatedAt);
    }

    /// <summary>
    /// Escalating changes nothing about the ticket except that it was escalated.
    /// </summary>
    /// <remarks>
    /// No state, no priority. Both are the same mistake in different clothes — they would make
    /// the record say the firm had acted when all that happened was that a clock ran out. The
    /// status says where the ticket is and the priority says what the requester cannot do, and a
    /// promise being missed changes neither.
    /// </remarks>
    [Fact]
    public async Task Escalating_does_not_move_the_ticket_or_change_what_it_promises()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);
        await Sweep(fixture).RunAsync();

        await using var context = fixture.NewContext();
        var stored = await context.Tickets.AsNoTracking().SingleAsync(one => one.Id == ticket.Id);

        Assert.Equal(TicketStatus.Open, stored.Status);
        Assert.Equal(TicketPriority.Blocking, stored.Priority);
        Assert.Equal(ticket.RespondBy, stored.RespondBy);
        Assert.Null(stored.AssigneeId);
    }

    /// <summary>
    /// Answering somebody stops them being escalated about.
    /// </summary>
    [Fact]
    public async Task A_ticket_answered_before_the_sweep_is_never_escalated()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(3);
        await Service(fixture).ReplyAsync(ticket.Id, "We can see it.", firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);

        Assert.Contains("still standing", await Sweep(fixture).RunAsync());
        Assert.Empty(await NoticesAsync(fixture));
    }

    /// <summary>
    /// Correcting the priority forgets the escalation, because the promise has changed.
    /// </summary>
    /// <remarks>
    /// Both directions matter and this is the one with the cost. A ticket typed in as blocking
    /// and escalated after four hours was escalated about a promise the firm never owed once
    /// somebody corrects it to a question — and leaving the mark would hide that, because the
    /// column is the only record that anybody was ever told.
    /// </remarks>
    [Fact]
    public async Task Correcting_the_priority_forgets_an_escalation_about_a_promise_that_moved()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);
        await Sweep(fixture).RunAsync();

        // Two days rather than four hours, counted from when it arrived — so not late at all.
        await Service(fixture).ReprioritiseAsync(
            ticket.Id, TicketPriority.Asking, firm.Anna);

        await using (var context = fixture.NewContext())
        {
            var stored = await context.Tickets.AsNoTracking()
                .SingleAsync(one => one.Id == ticket.Id);

            Assert.Null(stored.EscalatedAt);
        }

        // And it is not escalated again, because the promise it now carries is being kept.
        fixture.Clock.Now = Nine.AddHours(6);

        Assert.Contains("still standing", await Sweep(fixture).RunAsync());
        Assert.Single(await NoticesAsync(fixture));
    }

    /// <summary>
    /// The aggregate refuses a second escalation rather than ignoring it.
    /// </summary>
    /// <remarks>
    /// Refused rather than quietly accepted, because the caller is a sweep that will run again in
    /// half an hour: a second call that did nothing would look identical to one that worked, and
    /// the notice storm this column exists to prevent would arrive the first time somebody wrote
    /// the loop slightly differently.
    /// </remarks>
    [Fact]
    public void A_ticket_refuses_to_be_escalated_twice()
    {
        var ticket = Ticket.Raise(
            1, "Nobody can log in", "Since eight this morning.", TicketPriority.Blocking,
            Requester.Colleague, Guid.CreateVersion7(), Nine);

        ticket.Escalated(Nine.AddHours(5));

        Assert.Throws<InvalidOperationException>(() => ticket.Escalated(Nine.AddHours(6)));
    }

    /// <summary>
    /// With nobody to tell, the sweep says so and leaves the ticket for later.
    /// </summary>
    /// <remarks>
    /// Two things have to be true at once here, and the first attempt at this got the second wrong.
    ///
    /// The ticket is not marked, so it escalates the moment somebody records a head — marking it
    /// would turn "our escalations reach nobody" into silence that looks like success, with the job
    /// history reporting work done for ever afterwards. And it is left OUT of the batch, because
    /// the batch is fifty rows ordered by how late they are: fifty untellable ones at the front
    /// would push every ticket the firm could act on out of every sweep. Excluding them without
    /// counting them would be the silence again, so the sentence the sweep returns says how many
    /// there are.
    /// </remarks>
    [Fact]
    public async Task With_nobody_to_tell_the_sweep_says_so_and_leaves_the_ticket()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();

        Guid anna;

        await using (var context = fixture.NewContext())
        {
            var person = Employee.Hire("Anna Wanjiru", fixture.Clock.Today, null, "Engineer");
            context.Employees.Add(person);
            await context.SaveChangesAsync();
            anna = person.Id;
        }

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, anna);

        fixture.Clock.Now = Nine.AddHours(5);

        Assert.Contains("nobody to tell about them", await Sweep(fixture).RunAsync());
        Assert.Empty(await NoticesAsync(fixture));

        await using (var context = fixture.NewContext())
        {
            var stored = await context.Tickets.AsNoTracking()
                .SingleAsync(one => one.Id == ticket.Id);

            Assert.Null(stored.EscalatedAt);
        }
    }

    /// <summary>
    /// <b>A requester who had to chase is escalated about too.</b>
    /// </summary>
    /// <remarks>
    /// The hole this section shipped with, found by reading rather than by anything failing.
    ///
    /// The sweep and the count beside the navigation link both asked "has nobody answered yet",
    /// which they read from <see cref="Ticket.FirstRespondedAt"/> — and that is set exactly once,
    /// on purpose, so that a customer coming back cannot improve the number measuring how fast the
    /// firm answers. The consequence was that a ticket answered, chased, and then left was
    /// invisible to both, for ever. <b>The one person who had to chase was the one the desk's
    /// alarms could not see.</b>
    ///
    /// Fixed with a second clock rather than by spoiling the first: AnswerOwedBy says when the
    /// answer currently owed is due and is null when none is, and every query about lateness reads
    /// that. The first-response figure is untouched, which the last assertion here insists on.
    /// </remarks>
    [Fact]
    public async Task A_requester_who_came_back_and_was_left_waiting_is_escalated_about()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        // Answered in an hour, well inside the four-hour promise.
        fixture.Clock.Now = Nine.AddHours(1);
        await Service(fixture).ReplyAsync(ticket.Id, "We can see it.", firm.Anna);

        fixture.Clock.Now = Nine.AddHours(2);
        Assert.Contains("still standing", await Sweep(fixture).RunAsync());

        // A week later they say it is happening again, and nobody answers.
        fixture.Clock.Now = Nine.AddDays(7);
        await Service(fixture).TheySaidAsync(ticket.Id, "It is back.");

        fixture.Clock.Now = Nine.AddDays(7).AddHours(5);

        Assert.Contains("One missed promise", await Sweep(fixture).RunAsync());
        Assert.Single(await NoticesAsync(fixture));

        await using var context = fixture.NewContext();
        var stored = await context.Tickets.AsNoTracking().SingleAsync(one => one.Id == ticket.Id);

        // The record of how fast the firm answered the first time does not move.
        Assert.Equal(Nine.AddHours(1), stored.FirstRespondedAt);
        Assert.False(stored.ResponseOverdue(fixture.Clock.Now));

        // And the answer owed now is measured from when they came back.
        Assert.Equal(Nine.AddDays(7), stored.AwaitingSince);
        Assert.Equal(Nine.AddDays(7).AddHours(4), stored.AnswerOwedBy);
    }

    /// <summary>
    /// A reopened ticket nobody answers again is escalated about.
    /// </summary>
    /// <remarks>
    /// The other route back into the queue, and it had the same hole for the same reason.
    /// </remarks>
    [Fact]
    public async Task A_reopened_ticket_nobody_answers_again_is_escalated_about()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(1);
        await Service(fixture).ResolveAsync(ticket.Id, "Renewed the certificate.", firm.Anna);

        fixture.Clock.Now = Nine.AddDays(2);
        await Service(fixture).ReopenAsync(ticket.Id, "They rang: still failing.", firm.Anna);

        fixture.Clock.Now = Nine.AddDays(2).AddHours(5);

        Assert.Contains("One missed promise", await Sweep(fixture).RunAsync());
        Assert.Single(await NoticesAsync(fixture));
    }

    /// <summary>
    /// Answering clears what is owed and leaves the escalation on the record.
    /// </summary>
    /// <remarks>
    /// Two opposite decisions in one place. What is owed is cleared, because it has been given.
    /// The escalation mark is not, because somebody was told about a promise that was broken and
    /// answering does not undo that — and the ticket page shows it, so whoever picks it up next
    /// knows the conversation about it started elsewhere.
    /// </remarks>
    [Fact]
    public async Task Answering_clears_what_is_owed_and_leaves_the_escalation_recorded()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var firm = await AFirmAsync(fixture);

        fixture.Clock.Now = Nine;
        var ticket = await ATicketAsync(fixture, firm.Anna);

        fixture.Clock.Now = Nine.AddHours(5);
        await Sweep(fixture).RunAsync();

        await Service(fixture).ReplyAsync(ticket.Id, "Sorry for the delay.", firm.Anna);

        await using var context = fixture.NewContext();
        var stored = await context.Tickets.AsNoTracking().SingleAsync(one => one.Id == ticket.Id);

        Assert.Null(stored.AnswerOwedBy);
        Assert.Null(stored.AwaitingSince);
        Assert.Equal(Nine.AddHours(5), stored.EscalatedAt);

        // And it stays out of the count, which is the whole point of that count clearing.
        await using var again = fixture.NewContext();
        Assert.Equal(0, await new SupportQueries(again).UnansweredAsync(fixture.Clock.Now));
    }

    /// <summary>
    /// Reopening refuses a reason it cannot keep, and changes nothing when it does.
    /// </summary>
    /// <remarks>
    /// The reason used to be trimmed and length-checked inside the Note call on the last line,
    /// after Status and ResolvedAt had already been written — so a reason over a thousand
    /// characters left the aggregate reopened in memory with no line in the thread saying why, and
    /// whether that reached the database depended on whether the caller happened to save
    /// afterwards.
    /// </remarks>
    [Fact]
    public void Reopening_with_a_reason_it_cannot_keep_leaves_the_ticket_alone()
    {
        var ticket = Ticket.Raise(
            1, "Nobody can log in", "Since eight this morning.", TicketPriority.Blocking,
            Requester.Colleague, Guid.CreateVersion7(), Nine);

        ticket.Resolve("Renewed it.", Anna, Nine.AddHours(1));

        Assert.Throws<ArgumentException>(
            () => ticket.Reopen(new string('x', 1_001), Anna, Nine.AddHours(2)));

        Assert.True(ticket.IsResolved);
        Assert.Equal(Nine.AddHours(1), ticket.ResolvedAt);
        Assert.Null(ticket.AnswerOwedBy);

        Assert.Throws<ArgumentException>(() => ticket.Reopen("   ", Anna, Nine.AddHours(2)));

        Assert.True(ticket.IsResolved);
    }

    private static Task<Ticket> ATicketAsync(DatabaseFixture fixture, Guid requester) =>
        Service(fixture).RaiseAsync(
            "Nobody can log in",
            "Since about eight this morning every sign-in is refused.",
            TicketPriority.Blocking,
            Requester.Colleague,
            requester);

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

    /// <summary>
    /// The sweep, with a mailer that does nothing.
    /// </summary>
    /// <remarks>
    /// A notice may be emailed as well as recorded, and this asserts the record rather than the
    /// letter — the letter is section 33's and has its own tests. The none-mailer is what keeps
    /// the two questions separate.
    /// </remarks>
    private static EscalateMissedPromises Sweep(DatabaseFixture fixture)
    {
        var context = fixture.NewContext();

        return new EscalateMissedPromises(
            context,
            new PeopleQueries(context),
            new NoticeService(
                new NoticeRepository(context),
                new Silence(),
                new Somewhere(),
                fixture.Clock,
                NullLogger<NoticeService>.Instance),
            fixture.Clock,
            NullLogger<EscalateMissedPromises>.Instance);
    }

    private static async Task<List<Notice>> NoticesAsync(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        return await context.Notices.AsNoTracking().ToListAsync();
    }

    /// <summary>
    /// A department with a head, an engineer in it, and a manager above the engineer.
    /// </summary>
    /// <remarks>
    /// Three people rather than one, because the two recipient rules this job has are "whoever
    /// answers for the assignee" and "the heads of the departments", and a fixture with one
    /// person cannot tell those apart.
    /// </remarks>
    private static async Task<(Guid Anna, Guid Brian, Guid Maria)> AFirmAsync(
        DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var maria = Employee.Hire("Maria Njeri", fixture.Clock.Today, null, "Head of delivery");
        var brian = Employee.Hire("Brian Kimani", fixture.Clock.Today, null, "Engineer");
        var anna = Employee.Hire("Anna Wanjiru", fixture.Clock.Today, null, "Designer");

        context.Employees.AddRange(maria, brian, anna);
        await context.SaveChangesAsync();

        var delivery = Department.Open("Delivery");

        context.Departments.Add(delivery);
        await context.SaveChangesAsync();

        delivery.AppointHead(maria.Id);
        brian.ReportsTo(maria.Id);

        await context.SaveChangesAsync();

        return (anna.Id, brian.Id, maria.Id);
    }

    /// <summary>A mailer that posts nothing.</summary>
    /// <remarks>
    /// These tests assert the record and not the letter. Whether a notice is also emailed is
    /// section 33's question and has its own tests; mixing the two here would mean a change to
    /// the mail templates could fail a test about the help desk.
    /// </remarks>
    private sealed class Silence : IMailer
    {
        public Task SendAsync(
            EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class Somewhere : IWhereThisLives
    {
        public string? Reachable(string? path) =>
            path is null ? null : "https://erp.jiranisokotech.co.ke" + path;
    }
}
