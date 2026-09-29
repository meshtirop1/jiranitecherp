using System.Net;
using JiranisokoTech.Application.Ai;
using JiranisokoTech.Infrastructure.Ai;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Ai;

/// <summary>
/// The AI pages, driven by posting their real forms, with the fake model in place of the provider.
/// </summary>
/// <remarks>
/// Page tests here assert on stored rows and on what was sent to the model as well as on the page,
/// because several faults in this codebase reported success on screen while doing nothing. For
/// these pages the screen matters in its own right too: section 37's labelling is a promise about
/// what the page says, so it is asserted on the page.
/// </remarks>
public class AiPageTests(AiFactory factory) : IClassFixture<AiFactory>
{
    [Theory]
    [InlineData("/assistant")]
    [InlineData("/projects/readings")]
    [InlineData("/ai/usage")]
    public async Task A_stranger_is_sent_to_sign_in(string path)
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    /// <summary>A developer does not hold ai.ask, so the page refuses them whatever the model could do.</summary>
    [Fact]
    public async Task Somebody_without_the_permission_is_refused_the_page()
    {
        var developer = await Browsing.SignedInAsync(factory, "dev-page@jiranisokotech.co.ke", Roles.Developer);

        var response = await developer.GetAsync("/assistant");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task With_no_key_the_page_says_so_and_offers_no_question_box()
    {
        factory.Model.Reset();
        factory.Model.IsConfigured = false;

        var lead = await AiSetup.PersonAsync(factory, "lead-page-nokey@jiranisokotech.co.ke", Roles.TechLead);

        var html = await (await lead.Browser.GetAsync("/assistant")).Content.ReadAsStringAsync();

        Assert.Contains("The assistant is not configured on this copy of the system", html);
        Assert.DoesNotContain("Input.Question", html);

        factory.Model.Reset();
    }

    /// <summary>
    /// Asked through the real form: the answer is labelled as the model's, the lookups are listed,
    /// and the one this person may not make is marked refused on the page and never sent.
    /// </summary>
    [Fact]
    public async Task A_question_posted_through_the_form_is_answered_labelled_and_its_refusals_shown()
    {
        factory.Model.Reset();
        await AiSetup.InvoiceAsync(factory, "Tana River Traders Page");
        var lead = await AiSetup.PersonAsync(factory, "lead-page-ask@jiranisokotech.co.ke", Roles.TechLead);

        factory.Model
            .ThenLooksUp("invoices", """{"unpaid_only":true,"client":""}""")
            .ThenSays("You do not have access to invoices, so I cannot say which are unpaid.");

        var response = await Browsing.PressAsync(lead.Browser, "/assistant", "ask",
            extra: [new("Input.Question", "Show me unpaid invoices")]);
        var html = await response.Content.ReadAsStringAsync();

        Browsing.Accepted(response);
        Assert.Contains("Written by a language model", html);
        Assert.Contains("You do not have access to invoices", html);
        Assert.Contains("Refused: you may not see this", html);
        Assert.DoesNotContain("Tana River Traders Page", factory.Model.Everything);

        // The question in the person's words reached the model, with today's date beside it.
        Assert.Contains("Show me unpaid invoices", factory.Model.Everything);
    }

    /// <summary>
    /// The assistant proposes; the person creates. The button posts the real confirmation form, and
    /// the task exists afterwards — and not before.
    /// </summary>
    [Fact]
    public async Task A_proposed_task_is_created_only_when_the_person_presses_the_button()
    {
        factory.Model.Reset();
        var head = await AiSetup.PersonAsync(factory, "head-page-propose@jiranisokotech.co.ke", Roles.DepartmentHead);
        var project = await AiSetup.ProjectAsync(factory, "Callback hardening page");

        factory.Model
            .ThenLooksUp("propose_task", """{"title":"Retry failed callbacks from the page","project":"Callback hardening page"}""")
            .ThenSays("I have proposed a task for you to create.");

        var asked = await Browsing.PressAsync(head.Browser, "/assistant", "ask",
            extra: [new("Input.Question", "Create a task to retry failed callbacks")]);
        var html = await asked.Content.ReadAsStringAsync();

        Assert.Contains("It proposes a task. Nothing has been created.", html);

        await factory.InScopeAsync(async services => Assert.False(
            await services.GetRequiredService<AppDbContext>().WorkItems
                .AnyAsync(one => one.Title == "Retry failed callbacks from the page")));

        var fields = HtmlForm.Fill(html).ToList();
        fields.RemoveAll(field => field.Key == "_handler");
        fields.Add(new("_handler", "create-task"));

        var created = await head.Browser.PostAsync("/assistant", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Found, created.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var item = await services.GetRequiredService<AppDbContext>().WorkItems
                .SingleAsync(one => one.Title == "Retry failed callbacks from the page");

            Assert.Equal(project, item.ProjectId);
            Assert.Equal(head.Employee, item.RaisedById);
        });
    }

    /// <summary>
    /// The hidden project field is an input like any other. A project the person cannot see is
    /// refused even if the post names it.
    /// </summary>
    [Fact]
    public async Task A_confirmation_naming_a_project_out_of_reach_is_refused()
    {
        var lead = await AiSetup.PersonAsync(factory, "lead-page-forge@jiranisokotech.co.ke", Roles.TechLead);
        var hidden = await AiSetup.ProjectAsync(factory, "Not the leads project");

        var page = await (await lead.Browser.GetAsync("/assistant")).Content.ReadAsStringAsync();
        var fields = HtmlForm.Fill(page).ToList();
        fields.RemoveAll(field => field.Key == "_handler");
        fields.Add(new("_handler", "create-task"));
        fields.Add(new("Proposed.Title", "Forged onto somebody elses project"));
        fields.Add(new("Proposed.ProjectId", hidden.ToString()));

        var response = await lead.Browser.PostAsync("/assistant", new FormUrlEncodedContent(fields));

        Assert.Contains("That project is not one you can see.", await response.Content.ReadAsStringAsync());

        await factory.InScopeAsync(async services => Assert.False(
            await services.GetRequiredService<AppDbContext>().WorkItems
                .AnyAsync(one => one.Title == "Forged onto somebody elses project")));
    }

    /// <summary>
    /// A static page dispatches a post only to a form it draws. The confirmation form is drawn
    /// when there is a title in it, so a person who clears the title and presses the button would
    /// post to a form that is no longer there — which is the error screen, not a message.
    /// </summary>
    [Fact]
    public async Task A_confirmation_with_its_title_cleared_is_told_so_rather_than_failing()
    {
        var lead = await AiSetup.PersonAsync(factory, "lead-page-blank@jiranisokotech.co.ke", Roles.TechLead);

        var page = await (await lead.Browser.GetAsync("/assistant")).Content.ReadAsStringAsync();
        var fields = HtmlForm.Fill(page).ToList();
        fields.RemoveAll(field => field.Key == "_handler");
        fields.Add(new("_handler", "create-task"));
        fields.Add(new("Proposed.Title", string.Empty));

        var response = await lead.Browser.PostAsync("/assistant", new FormUrlEncodedContent(fields));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A task needs a title.", html);
    }

    /// <summary>
    /// Section 37: facts, calculations and inference drawn apart, the first two with no model at
    /// all, and the third only when asked for and tagged as inference.
    /// </summary>
    [Fact]
    public async Task A_project_reading_keeps_facts_figures_and_inference_apart()
    {
        factory.Model.Reset();
        var head = await AiSetup.PersonAsync(factory, "head-page-reading@jiranisokotech.co.ke", Roles.DepartmentHead);
        var project = await AiSetup.ProjectAsync(factory, "Ledger migration reading", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10));
        await AiSetup.ItemAsync(factory, "Move the ledger tables reading", project, head.Employee, head.Employee);

        var before = await (await head.Browser.GetAsync($"/projects/{project}/reading")).Content.ReadAsStringAsync();

        Assert.Contains("Recorded", before);
        Assert.Contains("Move the ledger tables reading", before);
        Assert.Contains("Calculated from those records", before);
        Assert.Contains("Days to the due date", before);
        Assert.Contains("Inference, not fact", before);
        Assert.Empty(factory.Model.Received);

        factory.Model.ThenSays("""
            {"summary":"One item, well ahead of its date.",
             "judgements":[{"area":"Schedule","assessment":"Healthy","because":"#1 is the only work and the due date is ten days away."}],
             "blockers":[],"main_risk":"Only one person holds the work."}
            """);

        var response = await Browsing.PressAsync(head.Browser, $"/projects/{project}/reading", "read");
        var html = await response.Content.ReadAsStringAsync();

        Browsing.Accepted(response);
        Assert.Contains("One item, well ahead of its date.", html);
        Assert.Contains("Only one person holds the work.", html);

        // The model's words sit inside the inference box, after its tag — not among the facts.
        var box = html.IndexOf("class=\"inference\"", StringComparison.Ordinal);
        Assert.True(box > 0 && html.IndexOf("One item, well ahead of its date.", StringComparison.Ordinal) > box);
        Assert.True(html.IndexOf("Calculated from those records", StringComparison.Ordinal) < box);

        // It was handed the calculated figures, so it did not have to count.
        Assert.Contains("Days to the due date", factory.Model.Everything);
    }

    /// <summary>
    /// A reading in the wrong shape is not shown at all. Half a reading laid out as if whole would
    /// say the model had no view on the schedule, which is a different statement from "could not
    /// be read".
    /// </summary>
    [Fact]
    public async Task A_reading_that_cannot_be_laid_out_is_not_shown()
    {
        factory.Model.Reset();
        var head = await AiSetup.PersonAsync(factory, "head-page-garbled@jiranisokotech.co.ke", Roles.DepartmentHead);
        var project = await AiSetup.ProjectAsync(factory, "Garbled reading");

        factory.Model.ThenSays("The project looks fine to me.");

        var html = await (await Browsing.PressAsync(head.Browser, $"/projects/{project}/reading", "read"))
            .Content.ReadAsStringAsync();

        Assert.Contains("cannot lay out", html);
        Assert.DoesNotContain("The project looks fine to me.", html);
    }

    /// <summary>A project out of reach is "no such project", on the page as in the assistant.</summary>
    [Fact]
    public async Task A_lead_cannot_read_a_project_they_are_not_on()
    {
        var lead = await AiSetup.PersonAsync(factory, "lead-page-outside@jiranisokotech.co.ke", Roles.TechLead);
        var project = await AiSetup.ProjectAsync(factory, "Somebody elses reading");

        var html = await (await lead.Browser.GetAsync($"/projects/{project}/reading")).Content.ReadAsStringAsync();

        Assert.Contains("No such project", html);
        Assert.DoesNotContain("Somebody elses reading", html);

        var list = await (await lead.Browser.GetAsync("/projects/readings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Somebody elses reading", list);
    }

    /// <summary>
    /// The CV summary sends the CV and the advert, and not the candidate's name, address or phone;
    /// the CV going out is recorded as a read of it; and what comes back is labelled.
    /// </summary>
    [Fact]
    public async Task A_cv_summary_sends_the_cv_and_not_the_person_and_records_that_it_went()
    {
        factory.Model.Reset();
        var hr = await AiSetup.PersonAsync(factory, "hr-page-cv@jiranisokotech.co.ke", Roles.HumanResources);
        var application = await AiSetup.ApplicationAsync(
            factory, hr.Employee, "achieng-cv@example.com",
            "Seven years of C# at a Nairobi bank. Ran PostgreSQL in production."u8.ToArray(), "achieng.txt");

        factory.Model.ThenSays("""
            {"summary":"A backend engineer with banking experience.","skills":["C#","PostgreSQL"],
             "experience":"Seven years","education":"Not stated","missing_information":["Education"],
             "against_the_advert":[{"requirement":"Five years of C#","evidence":"Seven years of C#","shown":"Shown"}]}
            """);

        var response = await Browsing.PressAsync(hr.Browser, $"/hiring/applications/{application}/assist", "summary");
        var html = await response.Content.ReadAsStringAsync();

        Browsing.Accepted(response);
        Assert.Contains("A backend engineer with banking experience.", html);
        Assert.Contains("Inference, not fact", html);

        var sent = factory.Model.Everything;
        Assert.Contains("Ran PostgreSQL in production", sent);
        Assert.Contains("five years of C#", sent);
        Assert.DoesNotContain("Achieng", sent);
        Assert.DoesNotContain("achieng-cv@example.com", sent);
        Assert.DoesNotContain("700 111 222", sent);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            Assert.True(await database.AuditEntries.AnyAsync(entry =>
                entry.Action == "job_application.cv_sent_to_ai" && entry.SubjectId == application));
            Assert.True(await database.AiExchanges.AnyAsync(one =>
                one.Feature == AiFeature.CvSummary && one.SubjectId == application));
        });
    }

    /// <summary>
    /// "Where readable" kept honest: a format that cannot be read is said to be unreadable, and
    /// nothing is sent rather than a summary of the form fields passed off as one of the CV.
    /// </summary>
    [Fact]
    public async Task A_cv_that_cannot_be_read_sends_nothing_and_says_why()
    {
        factory.Model.Reset();
        var hr = await AiSetup.PersonAsync(factory, "hr-page-rtf@jiranisokotech.co.ke", Roles.HumanResources);
        var application = await AiSetup.ApplicationAsync(
            factory, hr.Employee, "rtf-cv@example.com", "{\\rtf1 hello}"u8.ToArray(), "old.rtf");

        var html = await (await Browsing.PressAsync(hr.Browser, $"/hiring/applications/{application}/assist", "summary"))
            .Content.ReadAsStringAsync();

        Assert.Contains("cannot be read here", html);
        Assert.Empty(factory.Model.Received);
    }

    [Fact]
    public async Task A_drafted_letter_is_shown_for_editing_and_nothing_is_sent_or_changed()
    {
        factory.Model.Reset();
        var hr = await AiSetup.PersonAsync(factory, "hr-page-draft@jiranisokotech.co.ke", Roles.HumanResources);
        var application = await AiSetup.ApplicationAsync(factory, hr.Employee, "draft-cv@example.com", null, null);

        factory.Model.ThenSays("""{"subject":"Your application","body":"Dear Achieng, thank you for applying."}""");

        var html = await (await Browsing.PressAsync(hr.Browser, $"/hiring/applications/{application}/assist", "draft",
            new Dictionary<string, string> { ["Drafting.Kind"] = nameof(LetterKind.Rejection) },
            extra: [new("Drafting.Points", "We chose somebody with more payments experience.")]))
            .Content.ReadAsStringAsync();

        Assert.Contains("Dear Achieng, thank you for applying.", html);
        Assert.Contains("A draft. Nothing has been sent.", html);
        Assert.Contains("more payments experience", factory.Model.Everything);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var stored = await database.Applications.AsNoTracking().SingleAsync(one => one.Id == application);

            Assert.Equal(JiranisokoTech.Domain.Recruitment.ApplicationStatus.Received, stored.Status);
        });
    }

    /// <summary>A recruiter may use the aids; a tech lead, who may read candidates, may not.</summary>
    [Fact]
    public async Task The_recruitment_aids_are_refused_to_somebody_without_ai_recruit()
    {
        var lead = await Browsing.SignedInAsync(factory, "lead-page-recruit@jiranisokotech.co.ke", Roles.TechLead);

        var response = await lead.GetAsync($"/hiring/applications/{Guid.NewGuid()}/assist");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/denied", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_usage_page_lists_who_asked_what_and_what_was_refused()
    {
        factory.Model.Reset();
        var lead = await AiSetup.PersonAsync(factory, "lead-page-usage@jiranisokotech.co.ke", Roles.TechLead);

        factory.Model
            .ThenLooksUp("invoices", """{"unpaid_only":true,"client":""}""")
            .ThenSays("No access.");

        await Browsing.PressAsync(lead.Browser, "/assistant", "ask",
            extra: [new("Input.Question", "Which invoices are overdue for the usage page?")]);

        var owner = await Browsing.SignedInAsync(factory, "owner-page-usage@jiranisokotech.co.ke", Roles.Owner);
        var html = await (await owner.GetAsync("/ai/usage")).Content.ReadAsStringAsync();

        Assert.Contains("Which invoices are overdue for the usage page?", html);
        Assert.Contains("invoices (refused)", html);
    }
}
