using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// The brief's recruitment workflow, from a job that does not exist to somebody being onboarded.
/// </summary>
/// <remarks>
/// Section 46: create job, candidate applies, screening, interview, offer, accept, employee
/// created, onboarding. The chain crosses three kinds of people — staff who need somebody, HR
/// who run the process, and a candidate who has no account — and two approvals up a reporting
/// line, which is where it most often fails: an approval that opens on a step nobody can
/// answer leaves the job waiting forever and says nothing.
///
/// Through the pages wherever the step has one: raising the requisition, both approvals,
/// the advert, the application on the public careers page, screening, scheduling the
/// interview, and the candidate accepting on the offer link. Four steps are on interactive
/// screens, which a posted form cannot press — sending the requisition for approval, marking
/// the interview held and scoring it, writing and sending the offer, and making the staff
/// record — and those go through the service each screen calls. Between approvals the outbox
/// is drained by hand, as the delivery chain does, rather than raced.
/// </remarks>
public static class HiringWorkflow
{
    public static async Task WalkAsync(ApplicationFactory factory)
    {
        var tag = Guid.CreateVersion7().ToString("N")[^6..];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // --- the people: a head who needs somebody, the two above them, and a panel member ----
        var head = Guid.Empty;
        var director = Guid.Empty;
        var chair = Guid.Empty;
        var panel = Guid.Empty;
        var headEmail = $"head-{tag}@jiranisokotech.co.ke";
        var directorEmail = $"director-{tag}@jiranisokotech.co.ke";
        var chairEmail = $"chair-{tag}@jiranisokotech.co.ke";
        var panelEmail = $"panel-{tag}@jiranisokotech.co.ke";

        await factory.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();

            async Task<Guid> StaffAsync(string name)
            {
                var one = await people.HireAsync($"{name} {tag}", today.AddYears(-3));
                await people.StartAsync(one.Id);
                return one.Id;
            }

            chair = await StaffAsync("Chair");
            director = await StaffAsync("Director");
            head = await StaffAsync("Head");
            panel = await StaffAsync("Panel");

            await people.SetReportingLineAsync(director, chair);
            await people.SetReportingLineAsync(head, director);
        });

        var asHead = await LinkedAsync(factory, headEmail, head, Roles.DepartmentHead);
        var asDirector = await LinkedAsync(factory, directorEmail, director, Roles.DepartmentHead);
        var asChair = await LinkedAsync(factory, chairEmail, chair, Roles.DepartmentHead);
        await LinkedAsync(factory, panelEmail, panel, Roles.TechLead);
        var hr = await Browsing.SignedInAsync(factory, $"hr-{tag}@jiranisokotech.co.ke", Roles.HumanResources);

        // --- create the job, and have it approved up the line --------------------------------
        var jobTitle = $"Backend engineer {tag}";

        Browsing.Accepted(await Browsing.PressAsync(asHead, "/hiring", "raise", new Dictionary<string, string>
        {
            ["Input.JobTitle"] = jobTitle,
            ["Input.Headcount"] = "1",
            ["Input.Justification"] = "Payments work has doubled since the M-Pesa integration went live.",
        }));

        var requisition = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            requisition = (await services.GetRequiredService<AppDbContext>().Requisitions.AsNoTracking()
                .SingleAsync(one => one.JobTitle == jobTitle)).Id;

            // The send-for-approval button is on an interactive panel.
            await services.GetRequiredService<RecruitmentService>().SubmitAsync(requisition);
        });

        await DrainAsync(factory);

        var chain = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var approval = await services.GetRequiredService<AppDbContext>().Approvals.AsNoTracking()
                .Include(one => one.Steps)
                .SingleAsync(one => one.SubjectId == requisition);

            // Up the line, in order: the head's manager, then theirs.
            Assert.Equal([director, chair], approval.Steps.OrderBy(step => step.Order).Select(step => step.DeciderId));
            chain = approval.Id;
        });

        // The raiser cannot decide their own; each decider approves on their own screen.
        Browsing.Accepted(await Browsing.PressAsync(asDirector, "/approvals", $"approve-{chain}"));
        Browsing.Accepted(await Browsing.PressAsync(asChair, "/approvals", $"approve-{chain}"));

        await DrainAsync(factory);
        await DrainAsync(factory);

        await factory.InScopeAsync(async services =>
            Assert.Equal(RequisitionStatus.Approved, (await services.GetRequiredService<AppDbContext>()
                .Requisitions.AsNoTracking().SingleAsync(one => one.Id == requisition)).Status));

        // --- HR advertises it ------------------------------------------------------------
        var slug = $"backend-engineer-{tag}";

        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/postings", "draft", new Dictionary<string, string>
        {
            ["Input.RequisitionId"] = requisition.ToString(),
            ["Input.Title"] = "Backend engineer",
            ["Input.Summary"] = "Build the payment services a growing client list depends on.",
            ["Input.Description"] = "You will own the M-Pesa and card integrations end to end.",
            ["Input.Location"] = "Nairobi",
            ["Input.Slug"] = slug,
        }));

        var posting = Guid.Empty;

        await factory.InScopeAsync(async services =>
            posting = (await services.GetRequiredService<AppDbContext>().Postings.AsNoTracking()
                .SingleAsync(one => one.Slug == slug)).Id);

        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/postings", $"publish-{posting}"));

        // --- the candidate applies, with no account, on the public page ------------------------
        var candidateEmail = $"achieng-{tag}@example.com";

        using (var candidate = factory.CreateBrowser())
        {
            var applied = await Browsing.PressAsync(candidate, $"/careers/{slug}", "apply", new Dictionary<string, string>
            {
                ["Input.FullName"] = $"Achieng Odhiambo {tag}",
                ["Input.Email"] = candidateEmail,
                ["Input.Consents"] = "true",
            });

            Assert.Contains("That is with us", await applied.Content.ReadAsStringAsync());
        }

        var application = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var who = await database.Candidates.AsNoTracking().SingleAsync(one => one.Email == candidateEmail);
            var theirs = await database.Applications.AsNoTracking().SingleAsync(one => one.CandidateId == who.Id);

            Assert.Equal(ApplicationStatus.Received, theirs.Status);
            Assert.NotNull(theirs.ConsentedAt);
            application = theirs.Id;
        });

        // --- a second applicant, turned down with a reason -------------------------------
        // The Reject button used to post no reason, which the application refuses, so nobody
        // could be turned down from the page at all. Pressed here the way HR presses it.
        var otherEmail = $"kamau-{tag}@example.com";

        using (var other = factory.CreateBrowser())
        {
            await Browsing.PressAsync(other, $"/careers/{slug}", "apply", new Dictionary<string, string>
            {
                ["Input.FullName"] = $"Kamau Njoroge {tag}",
                ["Input.Email"] = otherEmail,
                ["Input.Consents"] = "true",
            });
        }

        var turnedDown = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var who = await database.Candidates.AsNoTracking().SingleAsync(one => one.Email == otherEmail);
            turnedDown = (await database.Applications.AsNoTracking().SingleAsync(one => one.CandidateId == who.Id)).Id;
        });

        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/applications", $"reject-{turnedDown}",
            new Dictionary<string, string> { ["Rejecting.Reason"] = "No backend experience yet; kept for the graduate intake." }));

        await factory.InScopeAsync(async services =>
        {
            var rejected = await services.GetRequiredService<AppDbContext>().Applications.AsNoTracking()
                .SingleAsync(one => one.Id == turnedDown);

            Assert.Equal(ApplicationStatus.Rejected, rejected.Status);
            Assert.Equal("No backend experience yet; kept for the graduate intake.", rejected.RejectionReason);
        });

        // --- screening, and an interview with a panel ------------------------------------
        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/applications", $"move-{application}-Screening"));
        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/applications", $"move-{application}-Interviewing"));

        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/interviews", "schedule",
            new Dictionary<string, string>
            {
                ["Input.ApplicationId"] = application.ToString(),
                ["Input.Kind"] = nameof(InterviewKind.Technical),
                ["Input.On"] = today.AddDays(3).ToString("yyyy-MM-dd") + "T10:00",
                ["Input.Where"] = "Westlands office",
            },
            extra: [new("panel", panel.ToString())]));

        await factory.InScopeAsync(async services =>
        {
            var interview = await services.GetRequiredService<AppDbContext>().Interviews.AsNoTracking()
                .SingleAsync(one => one.ApplicationId == application);

            // Held and scored on an interactive panel, by the person who sat on it.
            var interviews = services.GetRequiredService<InterviewService>();
            await interviews.HeldAsync(interview.Id);
            await interviews.ScoreAsync(interview.Id, panel, Recommendation.StrongYes, "Designed the retry queue on the whiteboard.");
        });

        Browsing.Accepted(await Browsing.PressAsync(hr, "/hiring/applications", $"move-{application}-Offered"));

        // --- the offer, and the candidate accepting it -----------------------------------
        var secret = string.Empty;
        var offer = Guid.Empty;

        await factory.InScopeAsync(async services =>
        {
            var offers = services.GetRequiredService<OfferService>();

            var written = await offers.WriteAsync(
                application, "Backend engineer", Money.Of(320_000_00, "KES"), PayFrequency.Monthly,
                today.AddDays(30), today.AddDays(7), "Permanent, with a three-month probation.", head);

            await offers.SendAsync(written.Offer.Id);

            secret = written.Secret;
            offer = written.Offer.Id;
        });

        using (var candidate = factory.CreateBrowser())
        {
            var accepted = await Browsing.PressAsync(candidate, $"/offer/{secret}", "accept", new Dictionary<string, string>
            {
                ["Accepting.Name"] = $"Achieng Odhiambo {tag}",
            });

            Assert.Contains("that is accepted", await accepted.Content.ReadAsStringAsync());
        }

        // --- the staff record, and onboarding -------------------------------------------
        var employee = Guid.Empty;

        await factory.InScopeAsync(async services =>
            employee = (await services.GetRequiredService<OfferService>().TakeOnAsync(offer, reportsToId: head)).Id);

        await factory.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            var person = await database.Employees.AsNoTracking().SingleAsync(one => one.Id == employee);
            Assert.Equal("Backend engineer", person.JobTitle);
            Assert.Equal(320_000_00, person.Terms.SalaryMinorUnits);
            Assert.Equal(head, person.ReportsToId);

            Assert.Equal(ApplicationStatus.Hired,
                (await database.Applications.AsNoTracking().SingleAsync(one => one.Id == application)).Status);
            Assert.Equal(RequisitionStatus.Filled,
                (await database.Requisitions.AsNoTracking().SingleAsync(one => one.Id == requisition)).Status);
            Assert.True(await database.Onboardings.AnyAsync(one => one.EmployeeId == employee));
        });

        // HR opens the new starter's onboarding, which is where the chain hands over.
        var onboarding = await (await hr.GetAsync($"/onboarding/{employee}")).Content.ReadAsStringAsync();

        Assert.Contains($"Achieng Odhiambo {tag}", onboarding);
    }

    private static async Task<HttpClient> LinkedAsync(
        ApplicationFactory factory, string email, Guid employee, string role)
    {
        var browser = await Browsing.SignedInAsync(factory, email, role);

        await factory.InScopeAsync(async services =>
        {
            var account = await services.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
            await services.GetRequiredService<PeopleService>().LinkAccountAsync(employee, account!.Id);
        });

        return browser;
    }

    private static Task DrainAsync(ApplicationFactory factory) =>
        factory.InScopeAsync(services => services.GetRequiredService<OutboxDispatcher>().RunOnceAsync());
}

public class HiringWorkflowTests
{
    [Fact]
    public async Task From_a_job_nobody_has_asked_for_to_somebody_onboarding()
    {
        using var factory = new WorkflowFactory();

        await HiringWorkflow.WalkAsync(factory);
    }

    [PostgresFact]
    public async Task From_a_job_nobody_has_asked_for_to_somebody_onboarding_on_postgres()
    {
        using var factory = new WorkflowPostgresFactory();

        await HiringWorkflow.WalkAsync(factory);
    }
}
