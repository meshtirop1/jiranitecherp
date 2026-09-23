using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Repository = JiranisokoTech.Domain.Engineering.Repository;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// What the repositories can and cannot say about a person's day.
/// </summary>
/// <remarks>
/// Section 21 asked for time to be detected from Git rather than typed. The most
/// important test in this file is the one asserting that it is <em>not</em>: these
/// hours are approved and then billed to a client, and a number derived from commit
/// timestamps would be a guess wearing the clothes of a measurement.
/// </remarks>
public class EngineeringEvidenceTests
{
    /// <summary>
    /// A day of work is evidence, and never a number of hours.
    /// </summary>
    /// <remarks>
    /// Asserted on the shape of the type itself, because this is a decision somebody
    /// will be tempted to undo. A commit is a moment, not a duration: the gap between
    /// the first and the last holds lunch, a meeting and an afternoon on somebody
    /// else's problem, and the hour spent thinking before the first one is not in the
    /// record at all.
    /// </remarks>
    [Fact]
    public void A_day_of_work_reports_a_span_and_never_a_total()
    {
        var properties = typeof(DayOfWork)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.Contains(nameof(DayOfWork.Span), properties);

        Assert.DoesNotContain(properties, name =>
            name.Contains("Hours", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Minutes", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Duration", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A person with no claimed login sees nothing, and is told why.
    /// </summary>
    /// <remarks>
    /// The two must be distinguishable. "You committed nothing on Tuesday" and "this
    /// system does not know which login is yours" look identical on a screen that only
    /// shows an empty list, and only the second is something somebody can fix.
    /// </remarks>
    [Fact]
    public async Task Somebody_with_no_claimed_login_is_told_so()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var person = await GivePerson(fixture);

        await using var context = fixture.NewContext();

        var day = await new EngineeringQueries(context)
            .DayOfWorkAsync(person, new DateOnly(2026, 9, 22));

        Assert.True(day.IsEmpty);
        Assert.False(day.HasClaimedHandle);
    }

    /// <summary>
    /// Commits reach the right person through the claimed login, and only through it.
    /// </summary>
    /// <remarks>
    /// Never matched on a name that resembles theirs. A login that looks like
    /// somebody's name is not evidence that it is them, and the cost of guessing wrong
    /// is a timesheet showing work another person did.
    /// </remarks>
    [Fact]
    public async Task Commits_reach_the_person_who_claimed_the_login()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var person = await GivePerson(fixture);
        var on = new DateOnly(2026, 9, 22);

        await using (var context = fixture.NewContext())
        {
            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "erp", null,
                new string('0', 64), fixture.Clock.Now);

            context.Repositories.Add(repository);
            context.Contributors.Add(Contributor.Claim(
                GitProvider.GitHub, "meshtirop1", person, fixture.Clock.Now));

            await context.SaveChangesAsync();

            context.Commits.Add(Commit.Record(
                repository.Id, new string('a', 40), "Mine", "meshtirop1", "feature/1-x",
                null, At(on, 9, 14)));

            // Somebody else's, on the same day, in the same repository.
            context.Commits.Add(Commit.Record(
                repository.Id, new string('b', 40), "Not mine", "vincent", "feature/2-y",
                null, At(on, 11, 0)));

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        var day = await new EngineeringQueries(after).DayOfWorkAsync(person, on);

        Assert.True(day.HasClaimedHandle);
        Assert.Equal("Mine", Assert.Single(day.Commits).Message);
    }

    /// <summary>
    /// The span is the first and last commit, stated as a range.
    /// </summary>
    /// <remarks>
    /// "09:14 to 17:32" is a fact. "8h 18m" is an assertion about how somebody spent
    /// their day that nothing here is entitled to make, and it would end up on an
    /// invoice.
    /// </remarks>
    [Fact]
    public async Task The_span_is_stated_as_a_range_not_a_total()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var person = await GivePerson(fixture);
        var on = new DateOnly(2026, 9, 22);

        await using (var context = fixture.NewContext())
        {
            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "erp", null,
                new string('0', 64), fixture.Clock.Now);

            context.Repositories.Add(repository);
            context.Contributors.Add(Contributor.Claim(
                GitProvider.GitHub, "meshtirop1", person, fixture.Clock.Now));

            await context.SaveChangesAsync();

            context.Commits.Add(Commit.Record(
                repository.Id, new string('a', 40), "First", "meshtirop1", "b", null,
                At(on, 9, 14)));
            context.Commits.Add(Commit.Record(
                repository.Id, new string('c', 40), "Last", "meshtirop1", "b", null,
                At(on, 17, 32)));

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        var day = await new EngineeringQueries(after).DayOfWorkAsync(person, on);

        Assert.Equal(2, day.Commits.Count);
        Assert.Contains(" to ", day.Span);
    }

    /// <summary>
    /// A day's work names the projects it touched.
    /// </summary>
    /// <remarks>
    /// The part of a timesheet that is genuinely hard to remember two days later, and
    /// the whole value of showing this beside the form.
    /// </remarks>
    [Fact]
    public async Task A_day_of_work_names_the_projects_it_touched()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var person = await GivePerson(fixture);
        var on = new DateOnly(2026, 9, 22);

        await using (var context = fixture.NewContext())
        {
            var project = Project.Begin("Delivery note printer", "printer");
            context.Projects.Add(project);

            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "printer", project.Id,
                new string('0', 64), fixture.Clock.Now);

            context.Repositories.Add(repository);
            context.Contributors.Add(Contributor.Claim(
                GitProvider.GitHub, "meshtirop1", person, fixture.Clock.Now));

            await context.SaveChangesAsync();

            context.Commits.Add(Commit.Record(
                repository.Id, new string('a', 40), "Lay out the note", "meshtirop1",
                "feature/1-note", null, At(on, 10, 0)));

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        var day = await new EngineeringQueries(after).DayOfWorkAsync(person, on);

        Assert.Equal("Delivery note printer", Assert.Single(day.Projects).Name);
    }

    // --- the metrics ---------------------------------------------------------

    /// <summary>
    /// Nothing on the engineering report is per person.
    /// </summary>
    /// <remarks>
    /// The test that keeps section 38 from becoming a league table. Commits per person
    /// is easy to produce and measures the wrong thing — a developer who spends a week
    /// deleting code looks idle, one who reformats a file looks heroic — and published
    /// on a report it changes behaviour within a fortnight, towards more commits
    /// rather than more delivered work.
    /// </remarks>
    [Fact]
    public void The_engineering_report_measures_work_and_not_people()
    {
        var properties = typeof(EngineeringState)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain(properties, name =>
            name.Contains("Person", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Author", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Employee", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Lines", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The typical time to merge is the median, not the mean.
    /// </summary>
    /// <remarks>
    /// One pull request left open over a holiday drags a mean into uselessness, and
    /// the question people are asking is "how long does this usually take" — which has
    /// never been the mean.
    /// </remarks>
    [Fact]
    public async Task The_typical_time_to_merge_is_the_middle_one()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var today = new DateOnly(2026, 9, 23);

        await using (var context = fixture.NewContext())
        {
            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "erp", null,
                new string('0', 64), fixture.Clock.Now);

            context.Repositories.Add(repository);
            await context.SaveChangesAsync();

            // Two quick ones and an outlier left open for a fortnight. The mean would
            // be eight days; the median is two hours.
            Merge(context, repository.Id, 1, At(today.AddDays(-3), 9, 0), hours: 2);
            Merge(context, repository.Id, 2, At(today.AddDays(-2), 9, 0), hours: 2);
            Merge(context, repository.Id, 3, At(today.AddDays(-20), 9, 0), hours: 24 * 14);

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        var state = await new EngineeringQueries(after).StateAsync(today);

        Assert.Equal(3, state.PullRequestsMerged);
        Assert.Equal(2, state.MedianHoursToMerge);
    }

    /// <summary>
    /// The share of commits tied to a task is reported.
    /// </summary>
    /// <remarks>
    /// The health of the integration rather than of the engineering. A low share means
    /// branches are not being named after the work, so the board is telling a less
    /// complete story than it appears to — which is worth seeing before somebody makes
    /// a decision on it.
    /// </remarks>
    [Fact]
    public async Task The_share_of_commits_tied_to_work_is_reported()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var today = new DateOnly(2026, 9, 23);

        await using (var context = fixture.NewContext())
        {
            var repository = Repository.Connect(
                GitProvider.GitHub, "jiranisokotech", "erp", null,
                new string('0', 64), fixture.Clock.Now);

            var item = WorkItem.Raise(1, "Something", Guid.CreateVersion7());

            context.Repositories.Add(repository);
            context.WorkItems.Add(item);
            await context.SaveChangesAsync();

            context.Commits.Add(Commit.Record(
                repository.Id, new string('a', 40), "Attached", "meshtirop1", "feature/1-x",
                item.Id, At(today.AddDays(-1), 9, 0)));

            context.Commits.Add(Commit.Record(
                repository.Id, new string('b', 40), "Loose", "meshtirop1", "main",
                null, At(today.AddDays(-1), 10, 0)));

            context.Commits.Add(Commit.Record(
                repository.Id, new string('c', 40), "Loose too", "meshtirop1", "main",
                null, At(today.AddDays(-1), 11, 0)));

            context.Commits.Add(Commit.Record(
                repository.Id, new string('d', 40), "Loose as well", "meshtirop1", "main",
                null, At(today.AddDays(-1), 12, 0)));

            await context.SaveChangesAsync();
        }

        await using var after = fixture.NewContext();

        var state = await new EngineeringQueries(after).StateAsync(today);

        Assert.Equal(4, state.Commits);
        Assert.Equal(25, state.AttachedShare);
    }

    /// <summary>A quiet period says so rather than showing zeroes.</summary>
    [Fact]
    public async Task A_period_with_nothing_in_it_says_so()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();

        var state = await new EngineeringQueries(context).StateAsync(new DateOnly(2026, 9, 23));

        Assert.True(state.NothingRecorded);
        Assert.Null(state.TypicalMerge);
    }

    // --- the scaffolding -----------------------------------------------------

    private static DateTimeOffset At(DateOnly on, int hour, int minute) =>
        new(on.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    private static void Merge(
        TestDbContext context, Guid repositoryId, int number, DateTimeOffset opened, double hours)
    {
        var pullRequest = PullRequest.Opened(
            repositoryId, number, $"Number {number}", $"feature/{number}-x", "meshtirop1",
            null, opened);

        pullRequest.Merged(opened.AddHours(hours));
        context.PullRequests.Add(pullRequest);
    }

    private static async Task<Guid> GivePerson(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var person = Employee.Hire("Meshack Tirop", new DateOnly(2026, 1, 5));

        context.Employees.Add(person);
        await context.SaveChangesAsync();

        return person.Id;
    }
}
