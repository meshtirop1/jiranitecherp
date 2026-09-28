using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Tests.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = JiranisokoTech.Infrastructure.Persistence.AppDbContext;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// What an applicant agreed to is on their application.
/// </summary>
/// <remarks>
/// A class of its own rather than beside the other careers tests, and not for tidiness: the
/// careers form allows five posts per address per ten minutes, the tests in one class share one
/// application and so one address, and adding this test's two posts to that class's four made
/// whichever ran sixth fail with a 429. A class gets its own application and its own limiter.
/// </remarks>
public class CareersConsentTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>
    /// An application nobody agreed to is refused, and one they did agree to records what.
    /// </summary>
    /// <remarks>
    /// Section 55's consent records. The refusal is on the server: a browser that ignores the
    /// checkbox's "required", or a script posting the form, reaches the same answer.
    /// </remarks>
    [Fact]
    public async Task Applying_needs_the_applicants_agreement_and_records_the_words_agreed_to()
    {
        await OpeningAsync("support-engineer");

        using var applicant = factory.CreateBrowser();

        var page = await applicant.GetAsync("/careers/support-engineer");
        var refused = await applicant.PostAsync("/careers/support-engineer", Multipart(HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "Wanjiru Kamau",
                ["Input.Email"] = "wanjiru@example.com",
            })));

        Assert.Contains("agreed to us using", await refused.Content.ReadAsStringAsync());

        await factory.InScopeAsync(async services =>
            Assert.False(await services.GetRequiredService<AppDbContext>().Candidates
                .AnyAsync(one => one.Email == "wanjiru@example.com")));

        page = await applicant.GetAsync("/careers/support-engineer");
        await applicant.PostAsync("/careers/support-engineer", Multipart(HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.FullName"] = "Wanjiru Kamau",
                ["Input.Email"] = "wanjiru@example.com",
                ["Input.Consents"] = "true",
            })));

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var candidate = await database.Candidates.SingleAsync(one => one.Email == "wanjiru@example.com");
            var application = await database.Applications.SingleAsync(one => one.CandidateId == candidate.Id);

            Assert.NotNull(application.ConsentedAt);
            Assert.Equal(JiranisokoTech.Application.Recruitment.Consent.Careers, application.ConsentWording);
        });
    }

    private static MultipartFormDataContent Multipart(Dictionary<string, string> fields)
    {
        var form = new MultipartFormDataContent();

        foreach (var (name, value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        return form;
    }

    private async Task OpeningAsync(string slug)
    {
        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var raiser = await people.HireAsync("Raiser", new DateOnly(2026, 1, 5));
            await people.StartAsync(raiser.Id);

            var recruitment = services.GetRequiredService<RecruitmentService>();
            var requisition = await recruitment.RaiseRequisitionAsync(
                "Support Engineer", null, 1, "We need somebody.", raiser.Id);

            await recruitment.SubmitAsync(requisition.Id);
            await recruitment.RecordDecisionAsync(requisition.Id, true, null);

            var draft = await recruitment.DraftPostingAsync(
                requisition.Id, "Support Engineer", "Support Engineer at Jiranisoko Tech Solutions.",
                "What the job involves, at length.", "Nairobi", slug);

            await recruitment.PublishAsync(draft.Id);
        });
    }
}
