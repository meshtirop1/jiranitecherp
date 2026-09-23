using System.Diagnostics;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JiranisokoTech.Tests.Mail;

/// <summary>
/// The letters that leave this firm.
/// </summary>
/// <remarks>
/// Driven through the real application and the real outbox rather than by
/// calling a handler, because the thing worth knowing is that an application
/// arriving on the public careers page ends with a letter — and every part of
/// that chain is somewhere a message can be dropped: the event may not be
/// raised, the row may not reach the outbox, the handler may not be registered.
/// Calling the handler directly tests the one link that was never in doubt.
/// </remarks>
public class CandidateNotificationTests
{
    /// <summary>
    /// Somebody who applies is told their application arrived.
    /// </summary>
    /// <remarks>
    /// The whole point. Before this, an application arrived, a CV was stored,
    /// and the candidate heard nothing at any point — so anybody applying
    /// concluded within a week that the advert was stale or the form broken.
    /// </remarks>
    [Fact]
    public async Task Applying_is_acknowledged()
    {
        await using var application = new MailingFactory();

        var posting = await AdvertiseAsync(application, "Delivery engineer");

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            await recruitment.ApplyAsync(
                posting, "Duncan Kiptoo", "duncan@example.com", null, null, null, null);
        });

        var letter = await WaitForAsync(application, "duncan@example.com");

        Assert.NotNull(letter);
        Assert.Contains("Delivery engineer", letter!.Subject);
        Assert.Contains("Thank you for applying", letter.TextBody);
    }

    /// <summary>
    /// A rejection says no, and says nothing else.
    /// </summary>
    /// <remarks>
    /// The reasons live in scorecards written for colleagues about a person.
    /// An email is the one artefact certain to be forwarded, so a letter that
    /// carried any of that would turn a private assessment into a document the
    /// subject holds — and invite an argument the firm cannot win and is not
    /// obliged to have.
    /// </remarks>
    [Fact]
    public async Task A_rejection_carries_no_reason()
    {
        await using var application = new MailingFactory();

        var posting = await AdvertiseAsync(application, "Delivery engineer");
        var applied = await ApplyAsync(application, posting, "rejected@example.com");

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            await recruitment.MoveApplicationAsync(
                applied, ApplicationStatus.Rejected, "Weak on the fundamentals.");
        });

        var letter = await WaitForAsync(application, "rejected@example.com", "About your application");

        Assert.NotNull(letter);
        Assert.Contains("not taking your application further", letter!.TextBody);

        // The internal note must not have travelled with it.
        Assert.DoesNotContain("fundamentals", letter.TextBody);
        Assert.DoesNotContain("Weak", letter.TextBody);
    }

    [Fact]
    public async Task An_offer_says_the_terms_follow_separately()
    {
        await using var application = new MailingFactory();

        var posting = await AdvertiseAsync(application, "Delivery engineer");
        var applied = await ApplyAsync(application, posting, "offered@example.com");

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            await recruitment.MoveApplicationAsync(applied, ApplicationStatus.Screening);
            await recruitment.MoveApplicationAsync(applied, ApplicationStatus.Interviewing);
            await recruitment.MoveApplicationAsync(applied, ApplicationStatus.Offered);
        });

        var letter = await WaitForAsync(application, "offered@example.com", "An offer");

        Assert.NotNull(letter);

        // Not a contract and careful not to read like one: an email that does
        // is an email somebody will later say they accepted.
        Assert.Contains("nothing is settled", letter!.TextBody);
    }

    /// <summary>
    /// The internal stages are silent.
    /// </summary>
    /// <remarks>
    /// "Screening" means nothing to a candidate, and a letter for every step
    /// trains people to ignore the letters that matter.
    /// </remarks>
    [Fact]
    public async Task Moving_through_the_internal_stages_writes_to_nobody()
    {
        await using var application = new MailingFactory();

        var posting = await AdvertiseAsync(application, "Delivery engineer");
        var applied = await ApplyAsync(application, posting, "quiet@example.com");

        // The acknowledgement is expected; wait for it so the assertion below
        // is about what came after rather than about timing.
        Assert.NotNull(await WaitForAsync(application, "quiet@example.com"));

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            await recruitment.MoveApplicationAsync(applied, ApplicationStatus.Screening);
            await recruitment.MoveApplicationAsync(applied, ApplicationStatus.Interviewing);
        });

        Assert.True(await SettledAsync(application, TimeSpan.FromSeconds(10)));

        var letters = application.Sent.AllTo("quiet@example.com");

        Assert.Single(letters);
    }

    private static async Task<Guid> AdvertiseAsync(MailingFactory application, string title)
    {
        var posting = Guid.Empty;

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();
            var people = services.GetRequiredService<PeopleService>();
            var database = services.GetRequiredService<AppDbContext>();

            // A requisition has to be raised by somebody on the staff list, so
            // there has to be somebody on it.
            var head = await people.HireAsync(
                "Charity Jepchirchir", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

            await people.StartAsync(head.Id);

            var requisition = await recruitment.RaiseRequisitionAsync(
                title, null, 1, "The delivery team is one short.", head.Id);

            // Submitted, then approved. A requisition refuses to go straight
            // from draft to approved — there is no decision outstanding on a
            // draft — and the approval chain itself is another module's
            // business, so it is settled directly on the aggregate here.
            await recruitment.SubmitAsync(requisition.Id);

            var stored = await database.Requisitions.FirstAsync(
                one => one.Id == requisition.Id);

            stored.Approved(DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();

            var advert = await recruitment.DraftPostingAsync(
                requisition.Id,
                title,
                "Come and build delivery software.",
                "The long version of the same.",
                "Nairobi");

            await recruitment.PublishAsync(advert.Id);

            posting = advert.Id;
        });

        return posting;
    }

    private static async Task<Guid> ApplyAsync(
        MailingFactory application, Guid posting, string address)
    {
        var applied = Guid.Empty;

        await application.InScopeAsync(async services =>
        {
            var recruitment = services.GetRequiredService<RecruitmentService>();

            var made = await recruitment.ApplyAsync(
                posting, "A Candidate", address, null, null, null, null);

            applied = made.Id;
        });

        return applied;
    }

    private static async Task<EmailMessage?> WaitForAsync(
        MailingFactory application, string address, string? containing = null)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var found = application.Sent.FirstOrDefault(message =>
                message.ToAddress == address
                && (containing is null || message.Subject.Contains(containing)));

            if (found is not null)
            {
                return found;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<bool> SettledAsync(MailingFactory application, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < patience)
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

    private sealed class MailingFactory : ApplicationFactory
    {
        public Recorder Sent { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Outbox:PollInterval", "00:00:00.100");
            builder.UseSetting("Mail:BaseAddress", "https://erp.example");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMailer>();
                services.AddSingleton<IMailer>(Sent);
            });
        }
    }

    private sealed class Recorder : IMailer
    {
        private readonly List<EmailMessage> _sent = [];
        private readonly Lock _lock = new();

        public EmailMessage? FirstOrDefault(Func<EmailMessage, bool> match)
        {
            lock (_lock)
            {
                return _sent.FirstOrDefault(match);
            }
        }

        public IReadOnlyList<EmailMessage> AllTo(string address)
        {
            lock (_lock)
            {
                return [.. _sent.Where(message => message.ToAddress == address)];
            }
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sent.Add(message);
            }

            return Task.CompletedTask;
        }
    }
}
