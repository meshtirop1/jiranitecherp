using System.Net;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Support;
using JiranisokoTech.Domain.Support;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The help desk, driven through the screen rather than through the service.
/// </summary>
/// <remarks>
/// Section 26. <see cref="Support.TicketTests"/> asserts the rules; these assert that a person
/// pressing the buttons reaches them, which is a different question and the one this codebase has
/// been wrong about most often. Two faults it has shipped before are both live here: a select
/// seeded from the stored row overwriting what was posted (see <c>FirstLook</c>), and a form
/// whose empty option makes Blazor refuse the whole post without saying anything.
///
/// The test that matters most is the pair at the top. A note and a reply are two forms on one
/// page that look the same and differ in what leaves the building — so one is posted and the
/// clock must NOT stop, and the other is posted and it must.
/// </remarks>
public class SupportPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    /// <summary>
    /// <b>A note posted from the page does not answer anybody.</b>
    /// </summary>
    /// <remarks>
    /// Asserted on the stored row rather than on the screen, because the screen says "Noted."
    /// either way. If these two forms were ever collapsed into one with a checkbox, this is the
    /// test that would fail.
    /// </remarks>
    [Fact]
    public async Task A_note_from_the_page_leaves_the_clock_running()
    {
        var (browser, ticket, _) = await ATicketAsync("notes");

        await PostAsync(browser, $"/support/{ticket.Number}", "note", new()
        {
            ["Inside.Text"] = "Looks like their own certificate. Asking Brian.",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.Null(stored.FirstRespondedAt);
        Assert.Equal(TicketStatus.Open, stored.Status);

        var note = Assert.Single(stored.Messages, one => one.Audience == Audience.Inside);

        Assert.Contains("Asking Brian", note.Text);
    }

    /// <summary>
    /// <b>An answer posted from the page stops the clock.</b>
    /// </summary>
    /// <remarks>
    /// The other half, and it is the half that would go unnoticed if the button did nothing: the
    /// page would say the words had been sent and the report would go on calling the ticket
    /// unanswered, which reads as a reporting bug rather than as a lost reply.
    /// </remarks>
    [Fact]
    public async Task An_answer_from_the_page_stops_the_clock_and_carries_the_words()
    {
        var (browser, ticket, _) = await ATicketAsync("answers");

        await PostAsync(browser, $"/support/{ticket.Number}", "reply", new()
        {
            ["Out.Text"] = "We have renewed the certificate — try it now.",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.NotNull(stored.FirstRespondedAt);

        var said = Assert.Single(
            stored.AsTheySeeIt, one => one.Text.Contains("renewed the certificate"));

        Assert.Equal(Audience.Requester, said.Audience);
        Assert.NotNull(said.ByEmployeeId);
    }

    /// <summary>
    /// Ticking the box moves the ticket to the requester and does not pause anything.
    /// </summary>
    /// <remarks>
    /// A checkbox is the one control <c>HtmlForm.Fill</c> cannot carry for you — a browser sends
    /// nothing at all for an unticked one — so it has to be posted by name, and posting it is the
    /// only way to know the binding works.
    /// </remarks>
    [Fact]
    public async Task Saying_it_needs_something_back_moves_the_ticket_to_them()
    {
        var (browser, ticket, _) = await ATicketAsync("waiting");

        await PostAsync(browser, $"/support/{ticket.Number}", "reply", new()
        {
            ["Out.Text"] = "Which browser are they using?",
            ["Out.WaitingOnThem"] = "true",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.Equal(TicketStatus.WithRequester, stored.Status);
        Assert.NotNull(stored.FirstRespondedAt);
    }

    /// <summary>
    /// Raising a ticket from the queue page records the requester's own words.
    /// </summary>
    /// <remarks>
    /// The select posting who asked carries the kind and the identifier as one value, because the
    /// column it fills points at one of two tables. <c>HtmlForm.Fill</c> reads inputs and not
    /// selects, so that value is posted by name here — which is exactly the gap that hid thirteen
    /// broken selects until <c>BlankChoices</c> was written.
    /// </remarks>
    [Fact]
    public async Task Raising_a_ticket_from_the_page_records_what_the_requester_said()
    {
        var browser = await SignedInAsync("raising@jiranisokotech.co.ke", Roles.Administrator);
        var (_, anna) = await AColleagueAsync("Anna");
        var subject = "Despatch board is refusing my sign-in " + Suffix();

        var page = await browser.GetAsync("/support");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "raise",
                ["Input.Who"] = $"{(int)Requester.Colleague}:{anna}",
                ["Input.Subject"] = subject,
                ["Input.Body"] = "It has said the password is wrong since eight this morning.",
                ["Input.Priority"] = nameof(TicketPriority.Blocking),
            });

        var posted = await browser.PostAsync("/support", new FormUrlEncodedContent(fields));

        // The page sends the caller to the ticket it just made, so anything else is a refusal.
        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);

        using var scope = factory.Services.CreateScope();

        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tickets
            .AsNoTracking()
            .SingleAsync(one => one.Subject == subject);

        Assert.Equal(TicketPriority.Blocking, stored.Priority);
        Assert.Equal(Requester.Colleague, stored.From);
        Assert.Equal(anna, stored.RequesterId);

        // Four hours, which is what blocking promises, counted from when it arrived.
        Assert.Equal(stored.RaisedAt.AddHours(4), stored.RespondBy);

        // A colleague's ticket is filed against no client, whatever the page offered.
        Assert.Null(stored.ClientId);

        var opening = Assert.Single(stored.Messages);

        Assert.Contains("eight this morning", opening.Text);
        Assert.Null(opening.ByEmployeeId);
    }

    /// <summary>
    /// Assigning from the page assigns it.
    /// </summary>
    /// <remarks>
    /// The select is seeded from the stored row on a GET, which is the arrangement that reported
    /// success on seven pages while changing nothing: under static rendering every request is a
    /// new component instance, so a first-pass flag is false again on the POST and the seeding
    /// runs before the handler reads the model. Asserted on the row, because the screen said
    /// "Saved." the whole time it was broken.
    /// </remarks>
    [Fact]
    public async Task Assigning_a_ticket_from_the_page_assigns_it()
    {
        var (browser, ticket, _) = await ATicketAsync("assigning");
        var (_, brian) = await AColleagueAsync("Brian");

        await PostAsync(browser, $"/support/{ticket.Number}", "assign", new()
        {
            ["Hand.AssigneeId"] = brian.ToString(),
        });

        Assert.Equal(brian, (await StoredAsync(ticket.Id)).AssigneeId);

        // And back off everybody, which is the direction an "only use non-empty" fix gets wrong.
        await PostAsync(browser, $"/support/{ticket.Number}", "assign", new()
        {
            ["Hand.AssigneeId"] = string.Empty,
        });

        Assert.Null((await StoredAsync(ticket.Id)).AssigneeId);
    }

    /// <summary>
    /// Changing the priority from the page changes the promise, from when it arrived.
    /// </summary>
    /// <remarks>
    /// Posted as Asking against a ticket raised as Blocking, so the direction is unmistakable:
    /// a seeding fault would leave the promise at four hours and the test would pass on a page
    /// that does nothing.
    /// </remarks>
    [Fact]
    public async Task Changing_the_priority_from_the_page_moves_the_promise()
    {
        var (browser, ticket, _) = await ATicketAsync("priority");

        await PostAsync(browser, $"/support/{ticket.Number}", "priority", new()
        {
            ["Matters.Priority"] = nameof(TicketPriority.Asking),
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.Equal(TicketPriority.Asking, stored.Priority);
        Assert.Equal(stored.RaisedAt.AddDays(2), stored.RespondBy);

        // And the thread says the goalposts moved, because the promise is stored.
        Assert.Contains(
            stored.Messages,
            one => one.Audience == Audience.Inside && one.Text.Contains("question"));
    }

    /// <summary>
    /// Settling from the page answers the person who asked.
    /// </summary>
    [Fact]
    public async Task Settling_from_the_page_answers_them_and_closes_it()
    {
        var (browser, ticket, _) = await ATicketAsync("settling");

        await PostAsync(browser, $"/support/{ticket.Number}", "resolve", new()
        {
            ["Settle.Text"] = "The certificate had expired. Renewed, and set to renew itself.",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.True(stored.IsResolved);
        Assert.NotNull(stored.FirstRespondedAt);
        Assert.Contains(stored.AsTheySeeIt, one => one.Text.Contains("Renewed"));
    }

    /// <summary>
    /// Recording what they said back puts a settled ticket into the queue again.
    /// </summary>
    /// <remarks>
    /// The form is only on the page while the ticket is open, so this posts it before settling
    /// rather than after — which is the honest test of the page, and the reopen form is what the
    /// page offers once a ticket is closed.
    /// </remarks>
    [Fact]
    public async Task Recording_what_they_said_puts_the_ticket_back_with_us()
    {
        var (browser, ticket, _) = await ATicketAsync("theysaid");

        await PostAsync(browser, $"/support/{ticket.Number}", "reply", new()
        {
            ["Out.Text"] = "Which browser?",
            ["Out.WaitingOnThem"] = "true",
        });

        Assert.Equal(TicketStatus.WithRequester, (await StoredAsync(ticket.Id)).Status);

        await PostAsync(browser, $"/support/{ticket.Number}", "they-said", new()
        {
            ["Back.Text"] = "Chrome, and also on the phone.",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.Equal(TicketStatus.Open, stored.Status);

        var theirs = Assert.Single(
            stored.Messages, one => one.Text.Contains("also on the phone"));

        // Their own words, so no author: there is no account for somebody on a telephone.
        Assert.Null(theirs.ByEmployeeId);
    }

    [Fact]
    public async Task Reopening_from_the_page_puts_a_settled_ticket_back_in_the_queue()
    {
        var (browser, ticket, _) = await ATicketAsync("reopening");

        await PostAsync(browser, $"/support/{ticket.Number}", "resolve", new()
        {
            ["Settle.Text"] = "Renewed the certificate.",
        });

        var answered = (await StoredAsync(ticket.Id)).FirstRespondedAt;

        await PostAsync(browser, $"/support/{ticket.Number}", "reopen", new()
        {
            ["Again.Text"] = "They rang to say it is still happening on the mobile app.",
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.Equal(TicketStatus.Open, stored.Status);
        Assert.Null(stored.ResolvedAt);

        // The response time is not given back. See TicketTests for why that matters.
        Assert.Equal(answered, stored.FirstRespondedAt);
    }

    /// <summary>
    /// Turning a request into work from the page raises one card and keeps its number.
    /// </summary>
    [Fact]
    public async Task Turning_a_request_into_work_from_the_page_raises_one_card()
    {
        var (browser, ticket, _) = await ATicketAsync("aswork");

        await PostAsync(browser, $"/support/{ticket.Number}", "as-work", new()
        {
            ["AsWork.ProjectId"] = string.Empty,
        });

        var stored = await StoredAsync(ticket.Id);

        Assert.NotNull(stored.WorkItemId);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var item = await database.WorkItems
            .AsNoTracking()
            .SingleAsync(one => one.Id == stored.WorkItemId);

        Assert.Contains(ticket.Reference, item.Title);

        // The ticket goes on owing an answer. Raising work is not answering anybody.
        Assert.Null(stored.FirstRespondedAt);
    }

    /// <summary>
    /// <b>A developer may note and may not answer.</b>
    /// </summary>
    /// <remarks>
    /// The authorisation this section is arranged around, asserted on the markup because that is
    /// where a person meets it: the form that writes to a client is simply not on the page. A
    /// developer holds the desk's read permission like everybody else — that is deliberate, the
    /// person who knows why the sign-in fails is the person whose sentence belongs in the thread
    /// — and does not hold the one that lets words leave the building.
    /// </remarks>
    [Fact]
    public async Task A_developer_can_note_a_ticket_and_cannot_answer_it()
    {
        var (_, ticket, _) = await ATicketAsync("developer");

        var browser = await SignedInAsync("dev@jiranisokotech.co.ke", Roles.Developer);
        var page = await browser.GetAsync($"/support/{ticket.Number}");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("Say something to the others", html);
        Assert.DoesNotContain("These words leave the building", html);
        Assert.DoesNotContain("Answer and settle", html);
    }

    /// <summary>
    /// The thread marks what left the building, on every line.
    /// </summary>
    /// <remarks>
    /// A colour alone would be a class name a refactor could drop without failing anything, so
    /// the audience is also said in words. Both are asserted: the note carries neither the
    /// outward class nor the outward sentence, and the reply carries both.
    /// </remarks>
    [Fact]
    public async Task The_thread_says_in_words_which_lines_went_outside()
    {
        var (browser, ticket, _) = await ATicketAsync("marking");

        await PostAsync(browser, $"/support/{ticket.Number}", "note", new()
        {
            ["Inside.Text"] = "Their own script is doing this.",
        });

        await PostAsync(browser, $"/support/{ticket.Number}", "reply", new()
        {
            ["Out.Text"] = "We have found the cause.",
        });

        var html = await (await browser.GetAsync($"/support/{ticket.Number}"))
            .Content.ReadAsStringAsync();

        Assert.Contains("to the others here", html);
        Assert.Contains("said--out", html);
    }

    /// <summary>
    /// The queue page opens, and says what is waiting rather than how many rows there are.
    /// </summary>
    /// <remarks>
    /// Page tests in one class share one database, so "nothing yet" is true only for whichever
    /// test runs first. What is asserted is the shape of the page and the presence of a ticket
    /// this test made, never a total.
    /// </remarks>
    [Fact]
    public async Task The_queue_page_lists_a_ticket_by_its_reference()
    {
        var (browser, ticket, subject) = await ATicketAsync("listing");

        var html = await (await browser.GetAsync("/support")).Content.ReadAsStringAsync();

        Assert.Contains(ticket.Reference, html);
        Assert.Contains(subject, html);
        Assert.Contains("nobody yet", html);
    }

    [Fact]
    public async Task A_number_with_no_ticket_behind_it_says_so_rather_than_failing()
    {
        var browser = await SignedInAsync("missing@jiranisokotech.co.ke", Roles.Administrator);

        var page = await browser.GetAsync("/support/999999");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        Assert.Contains(
            "There is no ticket with that number",
            await page.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One ticket, raised by a colleague, with the caller signed in and on the staff list.
    /// </summary>
    /// <remarks>
    /// The account is linked to a staff record deliberately: every act on this page carries an
    /// author, and an unlinked account is refused rather than defaulted — so a helper that
    /// skipped the link would make every form here fail for a reason that has nothing to do with
    /// what is being tested.
    ///
    /// Each test passes its own word so the accounts do not collide, because the class fixture
    /// shares one database and one browser signed in as two people would be neither.
    /// </remarks>
    private async Task<(HttpClient Browser, Ticket Ticket, string Subject)> ATicketAsync(
        string who)
    {
        var email = $"{who}@jiranisokotech.co.ke";
        var browser = await SignedInAsync(email, Roles.Administrator);
        var subject = "Nobody can log in " + Suffix();

        using var scope = factory.Services.CreateScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await users.FindByEmailAsync(email))!.Id;

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();

        var me = await people.HireAsync(
            "Desk " + Suffix(), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        await people.StartAsync(me.Id);
        await people.LinkAccountAsync(me.Id, account);

        var ticket = await scope.ServiceProvider.GetRequiredService<SupportService>()
            .RaiseAsync(
                subject,
                "Since about eight this morning every sign-in says the certificate is wrong.",
                TicketPriority.Blocking,
                Requester.Colleague,
                me.Id);

        return (browser, ticket, subject);
    }

    private async Task<(string Name, Guid Id)> AColleagueAsync(string called)
    {
        using var scope = factory.Services.CreateScope();

        var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
        var name = $"{called} {Suffix()}";

        var person = await people.HireAsync(
            name, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        await people.StartAsync(person.Id);

        return (name, person.Id);
    }

    private async Task<Ticket> StoredAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tickets
            .AsNoTracking()
            .SingleAsync(one => one.Id == id);
    }

    /// <summary>
    /// Post one of this page's forms and insist it was accepted.
    /// </summary>
    /// <remarks>
    /// These forms re-render in place rather than redirecting, so OK is the success and the
    /// assertion has to be made on the stored row afterwards. A refusal also comes back as OK —
    /// which is exactly why every caller here reads the row rather than the screen.
    /// </remarks>
    private static async Task PostAsync(
        HttpClient browser, string path, string handler, Dictionary<string, string> values)
    {
        var page = await browser.GetAsync(path);

        values["_handler"] = handler;

        var fields = HtmlForm.Fill(await page.Content.ReadAsStringAsync(), values);
        var posted = await browser.PostAsync(path, new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
    }

    /// <remarks>
    /// The tail of the identifier, because a version 7 identifier starts with the time and two
    /// made in the same millisecond share their first characters.
    /// </remarks>
    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, role))
            {
                await users.AddToRoleAsync(stored!, role);
            }
        }

        var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/sign-in");

        var fields = HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            });

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(fields));

        return browser;
    }
}
