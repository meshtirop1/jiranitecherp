using System.Diagnostics;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Messaging;

/// <summary>
/// That the dispatcher is actually running in the application.
/// </summary>
/// <remarks>
/// The dispatcher itself is covered in detail elsewhere, against a context built
/// by hand. What is left, and what no unit test can see, is whether the running
/// application ever calls it: a hosted service that was never registered, a
/// scope that cannot resolve it, options that never bound. Each of those leaves
/// a container that is up, healthy, serving pages, and quietly delivering
/// nothing at all.
/// </remarks>
public class OutboxProcessorTests
{
    [Fact]
    public async Task The_running_application_drains_its_outbox()
    {
        await using var application = new PromptFactory();

        // A message naming an event this build does not have. It cannot be
        // delivered, and that is the point: abandoning it is work, and work is
        // proof that something is reading the table. Using a real event would
        // need a handler in the application, which is the next module along.
        var id = await PostAsync(application);

        var settled = await WaitForAsync(application, id, TimeSpan.FromSeconds(10));

        Assert.True(settled, "The outbox was never read: nothing settled the message.");
    }

    private static async Task<Guid> PostAsync(PromptFactory application)
    {
        using var scope = application.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var message = new OutboxMessage(
            "AnEventThisBuildDoesNotHave", "{}", DateTimeOffset.UtcNow);

        database.Outbox.Add(message);
        await database.SaveChangesAsync();

        return message.Id;
    }

    /// <summary>
    /// Poll for the outcome rather than sleeping for a fixed spell.
    /// </summary>
    /// <remarks>
    /// A sleep long enough to be reliable on a loaded build agent is a sleep
    /// wasted on every run that was never going to need it, and one short enough
    /// to be quick is a test that fails for no reason once a fortnight.
    /// </remarks>
    private static async Task<bool> WaitForAsync(
        PromptFactory application, Guid id, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < patience)
        {
            using var scope = application.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var settled = await database.Outbox
                .AnyAsync(message => message.Id == id && message.AbandonedAt != null);

            if (settled)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>The application with a dispatcher that does not dawdle.</summary>
    private sealed class PromptFactory : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Outbox:PollInterval", "00:00:00.100");
        }
    }
}
