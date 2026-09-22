using System.Diagnostics;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AppDbContext = JiranisokoTech.Infrastructure.Persistence.AppDbContext;
using ApprovalRequest = JiranisokoTech.Domain.Approvals.ApprovalRequest;

namespace JiranisokoTech.Tests.Recruitment;

/// <summary>
/// A hire, from the request to the offer, across three modules.
/// </summary>
/// <remarks>
/// Recruitment, approvals and people share nothing but event names and
/// identifiers. That is the design, and it is also the thing that can silently
/// stop working: a handler registered against the wrong event, an action name
/// spelled differently at the two ends, a chain opened against somebody with
/// nobody above them. None of it shows up in a unit test of any one module.
/// </remarks>
public class HiringChainTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    /// <summary>
    /// The whole point of the module, and of the approval engine before it.
    /// </summary>
    [Fact]
    public async Task A_submitted_requisition_opens_a_chain_and_the_decision_comes_back()
    {
        await using var application = new PromptFactory();

        var (engineer, head, director) = await FirmAsync(application);

        var requisition = await RaiseAsync(application, engineer);

        await application.InScopeAsync(services =>
            services.GetRequiredService<RecruitmentService>().SubmitAsync(requisition));

        // The chain opens by itself, up the reporting line, two levels.
        var chain = await WaitForChainAsync(application, requisition);

        Assert.NotNull(chain);
        Assert.Equal(2, chain!.Steps.Count);
        Assert.Equal(head, chain.Steps[0].DeciderId);
        Assert.Equal(director, chain.Steps[1].DeciderId);

        await application.InScopeAsync(async services =>
        {
            var approvals = services.GetRequiredService<ApprovalService>();

            await approvals.ApproveAsync(chain.Id, head);
            await approvals.ApproveAsync(chain.Id, director);
        });

        // And the answer comes back to the requisition without recruitment ever
        // having asked.
        var approved = await WaitForStatusAsync(
            application, requisition, RequisitionStatus.Approved);

        Assert.True(approved, "The approval never reached the requisition.");
    }

    [Fact]
    public async Task A_refusal_comes_back_with_its_reason()
    {
        await using var application = new PromptFactory();

        var (engineer, head, _) = await FirmAsync(application);

        var requisition = await RaiseAsync(application, engineer);

        await application.InScopeAsync(services =>
            services.GetRequiredService<RecruitmentService>().SubmitAsync(requisition));

        var chain = await WaitForChainAsync(application, requisition);

        await application.InScopeAsync(services =>
            services.GetRequiredService<ApprovalService>()
                .RefuseAsync(chain!.Id, head, "no budget this quarter"));

        var refused = await WaitForStatusAsync(
            application, requisition, RequisitionStatus.Refused);

        Assert.True(refused, "The refusal never reached the requisition.");

        await application.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var stored = await database.Requisitions.SingleAsync(one => one.Id == requisition);

            Assert.Equal("no budget this quarter", stored.Outcome);
        });
    }

    /// <summary>
    /// An advert for a post nobody agreed to pay for is a promise the firm has
    /// not made, and the person reading it cannot tell the difference.
    /// </summary>
    [Fact]
    public async Task Nothing_is_advertised_before_the_approvers_agree()
    {
        await using var application = new PromptFactory();

        var (engineer, _, _) = await FirmAsync(application);

        var requisition = await RaiseAsync(application, engineer);

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            var posting = await recruitment.DraftPostingAsync(
                requisition, "Delivery Engineer", "Build things", "A longer description.");

            await recruitment.SubmitAsync(requisition);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => recruitment.PublishAsync(posting.Id));

            Assert.Contains("still with the approvers", refused.Message);
        });
    }

    /// <summary>
    /// Somebody at the top of the firm has nobody to approve their request, and
    /// that has to be visible rather than silently retried for ever.
    /// </summary>
    [Fact]
    public async Task A_requisition_from_the_top_of_the_firm_stays_awaiting_approval()
    {
        await using var application = new PromptFactory();

        Guid director = default;

        await application.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var person = await people.HireAsync("Vincent Bungei", Monday);

            await people.StartAsync(person.Id);

            director = person.Id;
        });

        var requisition = await RaiseAsync(application, director);

        await application.InScopeAsync(services =>
            services.GetRequiredService<RecruitmentService>().SubmitAsync(requisition));

        // The outbox settles the message rather than retrying: nothing about
        // trying again in thirty seconds gives that person a manager.
        var settled = await SettledAsync(application);

        Assert.True(settled, "The message was left retrying instead of being let go.");

        await application.InScopeAsync(async services =>
        {
            var database = services.GetRequiredService<AppDbContext>();

            // No chain, and the requisition still shows as waiting, which is
            // where somebody will see it and name an approver.
            Assert.False(await database.Approvals.AnyAsync(
                one => one.SubjectId == requisition));

            var stored = await database.Requisitions.SingleAsync(one => one.Id == requisition);

            Assert.Equal(RequisitionStatus.AwaitingApproval, stored.Status);
        });
    }

    /// <summary>Engineer answers to a head, who answers to a director.</summary>
    private static async Task<(Guid Engineer, Guid Head, Guid Director)> FirmAsync(
        PromptFactory application)
    {
        Guid engineer = default;
        Guid head = default;
        Guid director = default;

        await application.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();

            var top = await people.HireAsync("Vincent Bungei", Monday);
            var middle = await people.HireAsync("Charity Jepchirchir", Monday);
            var bottom = await people.HireAsync("Tirop Meshack", Monday);

            await people.StartAsync(top.Id);
            await people.StartAsync(middle.Id);
            await people.StartAsync(bottom.Id);

            await people.SetReportingLineAsync(middle.Id, top.Id);
            await people.SetReportingLineAsync(bottom.Id, middle.Id);

            engineer = bottom.Id;
            head = middle.Id;
            director = top.Id;
        });

        return (engineer, head, director);
    }

    private static async Task<Guid> RaiseAsync(PromptFactory application, Guid raisedBy)
    {
        Guid id = default;

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            var requisition = await recruitment.RaiseRequisitionAsync(
                "Delivery Engineer",
                null,
                1,
                "Two projects starting in November and nobody free.",
                raisedBy);

            id = requisition.Id;
        });

        return id;
    }

    private static async Task<ApprovalRequest?> WaitForChainAsync(
        PromptFactory application, Guid requisition)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            ApprovalRequest? found = null;

            await application.InScopeAsync(async services =>
            {
                var database = services.GetRequiredService<AppDbContext>();

                found = await database.Approvals
                    .Include(one => one.Steps)
                    .FirstOrDefaultAsync(one => one.SubjectId == requisition);
            });

            if (found is not null)
            {
                return found;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<bool> WaitForStatusAsync(
        PromptFactory application, Guid requisition, RequisitionStatus expected)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var reached = false;

            await application.InScopeAsync(async services =>
            {
                var database = services.GetRequiredService<AppDbContext>();

                reached = await database.Requisitions.AnyAsync(
                    one => one.Id == requisition && one.Status == expected);
            });

            if (reached)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<bool> SettledAsync(PromptFactory application)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var pending = 0;

            await application.InScopeAsync(async services =>
            {
                var database = services.GetRequiredService<AppDbContext>();

                pending = await database.Outbox.CountAsync(
                    message => message.DispatchedAt == null && message.AbandonedAt == null);
            });

            if (pending == 0)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private sealed class PromptFactory : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Outbox:PollInterval", "00:00:00.100");
            builder.UseSetting("Mail:Transport", "None");
        }
    }
}
