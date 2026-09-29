using System.Collections.Concurrent;
using JiranisokoTech.Application.Automation;
using JiranisokoTech.Application.Mail;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Messaging;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using JiranisokoTech.Tests.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JiranisokoTech.Tests.Automation;

/// <summary>
/// A mailer that keeps what it was given and can be told to fail.
/// </summary>
/// <remarks>
/// Failing is what the retry tests need: a mail server that does not answer is the ordinary
/// way an action fails in a way that might pass next time, which is the case the outbox's
/// retries exist for.
/// </remarks>
public sealed class RecordingMailer : IMailer
{
    private int _failures;

    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public void FailNext(int times) => _failures = times;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new IOException("The mail server did not answer.");
        }

        Sent.Enqueue(message);

        return Task.CompletedTask;
    }
}

/// <summary>What the automation tests need from the host.</summary>
/// <remarks>
/// The workflow settings, so no background loop races the test for the outbox; a first retry
/// with no wait, so a retried run can be driven in the same test; and two attempts before the
/// outbox gives up, so giving up can be reached without an hour of backoff.
/// </remarks>
public static class Harness
{
    public static void Configure(IWebHostBuilder builder, RecordingMailer mail)
    {
        Workflow.Configure(builder);

        builder.UseSetting("Outbox:FirstRetryDelay", "00:00:00");
        builder.UseSetting("Outbox:MaxAttempts", "2");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMailer>();
            services.AddSingleton<IMailer>(mail);
        });
    }

    /// <summary>One pass of the outbox, as the hosted loop would make it.</summary>
    public static async Task<int> PassAsync(ApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().RunOnceAsync();
    }

    /// <summary>Passes until nothing is left to settle, or ten of them.</summary>
    public static async Task DrainAsync(ApplicationFactory factory)
    {
        for (var pass = 0; pass < 10 && await PassAsync(factory) > 0; pass++)
        {
        }
    }

    public static async Task<T> InScopeAsync<T>(
        ApplicationFactory factory, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();

        return await work(scope.ServiceProvider);
    }

    public static Task InScopeAsync(ApplicationFactory factory, Func<IServiceProvider, Task> work) =>
        factory.InScopeAsync(work);

    /// <summary>Somebody on the staff list, with a sign-in holding these roles.</summary>
    public static async Task<Guid> PersonAsync(
        ApplicationFactory factory, string name, params string[] roles)
    {
        var email = $"{name.ToLowerInvariant().Replace(' ', '.')}-{Guid.CreateVersion7():N}@jiranisokotech.co.ke";
        var account = await factory.CreateAccountAsync(email, Browsing.Password, name);

        return await InScopeAsync(factory, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = (await users.FindByIdAsync(account.Id.ToString()))!;

            foreach (var role in roles)
            {
                await users.AddToRoleAsync(stored, role);
            }

            var people = services.GetRequiredService<PeopleService>();
            var employee = await people.HireAsync(name, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

            await people.StartAsync(employee.Id);
            await people.LinkAccountAsync(employee.Id, account.Id);

            return employee.Id;
        });
    }

    /// <summary>Write a rule through the service, as the page does, and switch it on.</summary>
    public static async Task<Guid> RuleAsync(
        ApplicationFactory factory,
        string name,
        string trigger,
        Func<AutomationService, Guid, Task> build,
        bool on = true)
    {
        return await InScopeAsync(factory, async services =>
        {
            var automation = services.GetRequiredService<AutomationService>();
            var rule = await automation.WriteAsync(name, trigger, null);

            await build(automation, rule.Id);

            if (on)
            {
                await automation.SwitchOnAsync(rule.Id);
            }

            return rule.Id;
        });
    }

    public static Task<List<AutomationRun>> RunsAsync(ApplicationFactory factory, Guid ruleId) =>
        InScopeAsync(factory, services => services.GetRequiredService<AppDbContext>()
            .AutomationRuns.AsNoTracking()
            .Where(one => one.RuleId == ruleId)
            .OrderBy(one => one.MatchedAt)
            .ToListAsync());
}

public sealed class AutomationFactory : ApplicationFactory
{
    public RecordingMailer Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Harness.Configure(builder, Mail);
    }
}

public sealed class AutomationPostgresFactory : PostgresApplicationFactory
{
    public RecordingMailer Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Harness.Configure(builder, Mail);
    }
}
