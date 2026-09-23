using System.Diagnostics;
using JiranisokoTech.Application.Approvals;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.People;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AppDbContext = JiranisokoTech.Infrastructure.Persistence.AppDbContext;

namespace JiranisokoTech.Tests.Mail;

/// <summary>
/// An approval raised, and an email arriving because of it.
/// </summary>
/// <remarks>
/// Every piece is tested on its own — the chain, the dispatcher, the words. What
/// none of those can show is that they are joined: a handler registered against
/// the right event, resolvable in the scope the dispatcher makes, with a mailbox
/// it can actually find behind a staff record.
/// </remarks>
public class ApprovalNotificationTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task Asking_for_a_decision_emails_the_person_it_waits_on()
    {
        await using var application = new MailingFactory();

        var asker = await StaffAsync(application, "Tirop Meshack", "tirop@jiranisokotech.co.ke");
        var head = await StaffAsync(application, "Charity Jepchirchir", "charity@jiranisokotech.co.ke");

        await RequestAsync(application, asker, [head], "requisition.open");

        var sent = await WaitForAsync(application, "charity@jiranisokotech.co.ke");

        Assert.NotNull(sent);
        Assert.Contains("requisition open", sent!.Subject);

        // The message says who asked, because "somebody wants something" is not
        // a thing anybody acts on.
        Assert.Contains("Tirop Meshack", sent.TextBody);
    }

    /// <summary>
    /// A request that simply stops being mentioned is one the asker chases for a
    /// fortnight, so a refusal is told as plainly as an approval — with the
    /// reason.
    /// </summary>
    [Fact]
    public async Task A_refusal_reaches_the_person_who_asked_with_the_reason()
    {
        await using var application = new MailingFactory();

        var asker = await StaffAsync(application, "Duncan", "duncan@jiranisokotech.co.ke");
        var head = await StaffAsync(application, "Vincent Bungei", "vincent@jiranisokotech.co.ke");

        var request = await RequestAsync(application, asker, [head], "expense.claim");

        await application.InScopeAsync(services =>
            services.GetRequiredService<ApprovalService>()
                .RefuseAsync(request, head, "no budget this quarter"));

        var sent = await WaitForAsync(application, "duncan@jiranisokotech.co.ke", "expense claim");

        Assert.NotNull(sent);
        Assert.Contains("not approved", sent!.TextBody);
        Assert.Contains("no budget this quarter", sent.TextBody);
    }

    /// <summary>
    /// Somebody with no account has no mailbox this system knows, which is
    /// ordinary rather than a fault — so the handler lets it go instead of
    /// retrying eight times and burying the failures that could be recovered.
    /// </summary>
    [Fact]
    public async Task A_decider_with_no_account_is_not_a_failure()
    {
        await using var application = new MailingFactory();

        var asker = await StaffAsync(application, "Purity", "purity@jiranisokotech.co.ke");

        Guid head = default;

        await application.InScopeAsync(async services =>
        {
            var people = services.GetRequiredService<PeopleService>();
            var person = await people.HireAsync("Precious", Monday);

            await people.StartAsync(person.Id);

            head = person.Id;
        });

        var request = await RequestAsync(application, asker, [head], "leave.request");

        // The outbox settles it rather than leaving it to retry.
        var settled = await SettledAsync(application, TimeSpan.FromSeconds(10));

        Assert.True(settled, "The message was left pending instead of being let go.");
    }

    private static async Task<Guid> StaffAsync(
        MailingFactory application, string name, string email)
    {
        await application.CreateAccountAsync(email, "a-long-enough-password", name);

        Guid id = default;

        await application.InScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var people = services.GetRequiredService<PeopleService>();

            var account = await users.FindByEmailAsync(email);
            var person = await people.HireAsync(name, Monday);

            await people.StartAsync(person.Id);
            await people.LinkAccountAsync(person.Id, account!.Id);

            id = person.Id;
        });

        return id;
    }

    private static async Task<Guid> RequestAsync(
        MailingFactory application, Guid asker, IReadOnlyList<Guid> deciders, string action)
    {
        Guid id = default;

        await application.InScopeAsync(async services =>
        {
            var approvals = services.GetRequiredService<ApprovalService>();

            var request = await approvals.RequestAsync(
                "JobRequisition", Guid.CreateVersion7(), action, asker, deciders);

            id = request.Id;
        });

        return id;
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

    /// <summary>
    /// The application, with a mailer that keeps what it was given.
    /// </summary>
    /// <remarks>
    /// A recording double rather than the file mailer, because the assertion is
    /// about what was composed and sent — not about a file appearing, which
    /// would make this a test of the filesystem.
    /// </remarks>
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
            // Locked: the dispatcher sends from a background thread while the
            // test reads from its own, and a list read mid-write throws.
            lock (_lock)
            {
                return _sent.FirstOrDefault(match);
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
