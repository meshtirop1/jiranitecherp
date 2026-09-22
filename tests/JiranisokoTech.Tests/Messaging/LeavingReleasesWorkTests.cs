using System.Diagnostics;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace JiranisokoTech.Tests.Messaging;

/// <summary>
/// One departure, carried across to the board by the outbox.
/// </summary>
/// <remarks>
/// Every piece of this is tested on its own — the domain event, the dispatcher,
/// the handler, the release. What none of those can show is that they are joined
/// up: the event registered under a name the registry knows, the handler
/// resolvable in the scope the dispatcher creates, the running application
/// actually reading the table.
///
/// This is also the test that would notice the whole arrangement being
/// pointless. If People simply called Work directly, it would pass — and the
/// outbox would be machinery nothing goes through.
/// </remarks>
public class LeavingReleasesWorkTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task Somebody_leaving_takes_their_open_work_off_them()
    {
        await using var application = new PromptFactory();

        Guid leaver;
        Guid open;

        using (var scope = application.Services.CreateScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PeopleService>();
            var work = scope.ServiceProvider.GetRequiredService<WorkService>();

            var duncan = await people.HireAsync("Duncan", Monday);
            await people.StartAsync(duncan.Id);

            var item = await work.RaiseAsync("Fit the delivery note printer", Guid.CreateVersion7());
            await work.AssignAsync(item.Id, duncan.Id);

            leaver = duncan.Id;
            open = item.Id;

            await people.RecordLeavingAsync(duncan.Id, Monday, "resigned");
        }

        var released = await WaitForAsync(application, open, TimeSpan.FromSeconds(15));

        Assert.True(
            released,
            "The departure never reached the board: the work is still assigned to somebody who "
            + "has left.");

        using (var scope = application.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // And the departure itself stands, whatever the handler did.
            var person = await database.Employees.SingleAsync(e => e.Id == leaver);
            Assert.False(person.IsEmployed);
        }
    }

    private static async Task<bool> WaitForAsync(
        PromptFactory application, Guid workItemId, TimeSpan patience)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < patience)
        {
            using (var scope = application.Services.CreateScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var unassigned = await database.WorkItems
                    .AnyAsync(item => item.Id == workItemId && item.AssigneeId == null);

                if (unassigned)
                {
                    return true;
                }
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
        }
    }
}
