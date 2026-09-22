using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = JiranisokoTech.Infrastructure.Persistence.AppDbContext;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// The careers pages: the only part of this system a stranger is meant to read.
/// </summary>
/// <remarks>
/// Everything else in the suite tests a page behind a sign-in. These are open to
/// the internet, which makes the interesting questions different ones: what is
/// visible without an account, what a stranger can put into the database, and
/// what they cannot get back out.
/// </remarks>
public class CareersPageTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task Anybody_can_read_the_openings_without_an_account()
    {
        await OpeningAsync("Delivery Engineer", "delivery-engineer");

        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync("/careers");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Delivery Engineer", html);
    }

    /// <summary>
    /// The headcount and the justification are an internal argument for
    /// spending money. Nothing about them belongs on a page a stranger reads.
    /// </summary>
    [Fact]
    public async Task The_public_page_shows_nothing_about_the_requisition()
    {
        await OpeningAsync(
            "Field Technician", "field-technician",
            justification: "Otieno is suspended and the Nairobi route has no cover.");

        using var browser = factory.CreateBrowser();

        var html = await (await browser.GetAsync("/careers/field-technician"))
            .Content.ReadAsStringAsync();

        Assert.Contains("Field Technician", html);
        Assert.DoesNotContain("Otieno", html);
        Assert.DoesNotContain("suspended", html);
        Assert.DoesNotContain("headcount", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An advert that was never published, or has been taken down, is not an
    /// advert. It must not be readable by guessing its address.
    /// </summary>
    [Fact]
    public async Task An_unpublished_advert_cannot_be_read()
    {
        Guid posting = default;

        await factory.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();
            var raiser = await StaffAsync(services, "Charity Jepchirchir");

            var requisition = await recruitment.RaiseRequisitionAsync(
                "Secret Role", null, 1, "Not advertised yet.", raiser);

            var draft = await recruitment.DraftPostingAsync(
                requisition.Id, "Secret Role", "Not up yet", "Nor this.", slug: "secret-role");

            posting = draft.Id;
        });

        using var browser = factory.CreateBrowser();

        var html = await (await browser.GetAsync("/careers/secret-role"))
            .Content.ReadAsStringAsync();

        Assert.Contains("That opening has gone", html);
        Assert.DoesNotContain("Nor this.", html);
        Assert.NotEqual(Guid.Empty, posting);
    }

    /// <summary>
    /// The whole point of the page: somebody outside the firm applies, and it
    /// arrives.
    /// </summary>
    [Fact]
    public async Task A_stranger_can_apply_and_is_told_what_happens_next()
    {
        await OpeningAsync("Data Engineer", "data-engineer");

        using var browser = factory.CreateBrowser();

        var page = await browser.GetAsync("/careers/data-engineer");
        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "Amina Hassan",
                ["Input.Email"] = "amina@example.com",
                ["Input.Phone"] = "0700 000 000",
                ["Input.Note"] = "I have done routing work before.",
            });

        var applied = await browser.PostAsync(
            "/careers/data-engineer", new FormUrlEncodedContent(fields));

        var html = await applied.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, html is null ? HttpStatusCode.OK : applied.StatusCode);
        Assert.Contains("That is with us", html);

        // Told what happens next, by name. "Thank you for your interest" reads
        // as an automated brush-off and answers nothing.
        Assert.Contains("Amina", html);
        Assert.Contains("three weeks", html);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var candidate = await database.Candidates
                .SingleAsync(one => one.Email == "amina@example.com");

            Assert.Equal("Amina Hassan", candidate.FullName);

            Assert.True(await database.Applications
                .AnyAsync(one => one.CandidateId == candidate.Id
                    && one.Status == ApplicationStatus.Received));
        });
    }

    /// <summary>
    /// A double-click, or an impatient second attempt a week later. They have
    /// applied; nothing is wrong; there is nothing for them to do.
    /// </summary>
    [Fact]
    public async Task Applying_twice_is_told_as_good_news_rather_than_an_error()
    {
        await OpeningAsync("Route Planner", "route-planner");

        using var browser = factory.CreateBrowser();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var page = await browser.GetAsync("/careers/route-planner");
            var fields = HtmlForm.Fill(
                await page.Content.ReadAsStringAsync(),
                new Dictionary<string, string>
                {
                    ["Input.FullName"] = "Joseph Kimani",
                    ["Input.Email"] = "joseph@example.com",
                });

            var applied = await browser.PostAsync(
                "/careers/route-planner", new FormUrlEncodedContent(fields));

            Assert.Contains("That is with us", await applied.Content.ReadAsStringAsync());
        }

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var candidate = await database.Candidates
                .SingleAsync(one => one.Email == "joseph@example.com");

            Assert.Equal(
                1, await database.Applications.CountAsync(one => one.CandidateId == candidate.Id));
        });
    }

    [Fact]
    public async Task A_cv_arrives_with_the_application_and_is_kept()
    {
        await OpeningAsync("Warehouse Lead", "warehouse-lead");

        using var browser = factory.CreateBrowser();

        var page = await browser.GetAsync("/careers/warehouse-lead");
        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "Grace Wanjiku",
                ["Input.Email"] = "grace@example.com",
            });

        using var form = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        var cv = new ByteArrayContent(Encoding.UTF8.GetBytes("Grace Wanjiku, warehouse lead."));
        cv.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(cv, "cv", "grace-cv.txt");

        var applied = await browser.PostAsync("/careers/warehouse-lead", form);

        Assert.Contains("That is with us", await applied.Content.ReadAsStringAsync());

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var candidate = await database.Candidates
                .SingleAsync(one => one.Email == "grace@example.com");

            var stored = await database.Applications
                .SingleAsync(one => one.CandidateId == candidate.Id);

            Assert.True(stored.HasCv);

            // What they called it is kept for whoever downloads it; what it is
            // stored as is a name this system chose.
            Assert.Equal("grace-cv.txt", stored.CvFileName);
            Assert.NotEqual("grace-cv.txt", stored.CvStoredName);
            Assert.EndsWith(".txt", stored.CvStoredName);
        });
    }

    /// <summary>
    /// A CV is a document belonging to somebody outside this firm. A stranger
    /// guessing an address must not get one.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_download_a_cv()
    {
        using var browser = factory.CreateBrowser();

        var response = await browser.GetAsync($"/cv/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("/sign-in", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Somebody_who_may_see_candidates_can_download_one()
    {
        await OpeningAsync("Stock Controller", "stock-controller");

        using var applicant = factory.CreateBrowser();

        var page = await applicant.GetAsync("/careers/stock-controller");
        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "Peter Omondi",
                ["Input.Email"] = "peter@example.com",
            });

        using var form = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Peter Omondi, stock controller.")),
            "cv", "peter.txt");

        await applicant.PostAsync("/careers/stock-controller", form);

        Guid application = default;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var candidate = await database.Candidates
                .SingleAsync(one => one.Email == "peter@example.com");

            application = (await database.Applications
                .SingleAsync(one => one.CandidateId == candidate.Id)).Id;
        });

        var staff = await SignedInAsync("cv-reader@jiranisokotech.co.ke", Roles.HumanResources);

        var download = await staff.GetAsync($"/cv/{application}");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Contains("Peter Omondi", await download.Content.ReadAsStringAsync());

        // Handed over as a download rather than rendered. A document opened in
        // the browser runs on this application origin.
        Assert.Equal(
            "application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
    }

    private async Task<Guid> OpeningAsync(
        string title, string slug, string justification = "We need somebody.")
    {
        Guid posting = default;

        await factory.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();
            var raiser = await StaffAsync(services, $"Raiser for {slug}");

            var requisition = await recruitment.RaiseRequisitionAsync(
                title, null, 1, justification, raiser);

            await recruitment.SubmitAsync(requisition.Id);
            await recruitment.RecordDecisionAsync(requisition.Id, true, null);

            var draft = await recruitment.DraftPostingAsync(
                requisition.Id,
                title,
                $"{title} at Jiranisoko Tech Solutions.",
                "What the job involves, at length.",
                "Nairobi",
                slug);

            await recruitment.PublishAsync(draft.Id);

            posting = draft.Id;
        });

        return posting;
    }

    private static async Task<Guid> StaffAsync(IServiceProvider services, string name)
    {
        var people = services.GetRequiredService<PeopleService>();

        var person = await people.HireAsync(name, Monday);
        await people.StartAsync(person.Id);

        return person.Id;
    }

    private async Task<HttpClient> SignedInAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);

                var stored = await users.FindByEmailAsync(email);
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
