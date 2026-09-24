using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Engineering;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Naming a version, saying what is in it, and taking it back.
/// </summary>
/// <remarks>
/// Section 67, and the last link in section 91's chain. Three things in here are worth more than
/// the rest, and all three are cases where something plausible is wrong.
///
/// <b>Ordering.</b> Every question this feature answers is an ordering question — what is out,
/// what came before this, what should the next version be — and text ordering gets all of them
/// wrong at the point where a project has had ten minor versions. 1.10.0 before 1.9.0 is not an
/// edge case; it is the eleventh release.
///
/// <b>What "live" means.</b> The highest version still out, not the most recently declared one.
/// They differ exactly when a patch to an older line ships after a newer release, which is a
/// normal Tuesday, and picking the wrong one puts a false answer on the one screen whose whole
/// job is to say what the firm is running.
///
/// <b>The changelog window.</b> Assembled from commits between two releases, with the boundaries
/// on the right side of one another — off by one at either end means a commit in two changelogs
/// or in none, and nothing anywhere would say so.
/// </remarks>
public class ReleaseTests
{
    private static readonly Guid Lead = Guid.CreateVersion7();

    [Theory]
    [InlineData("1.4.0", 1, 4, 0, null)]
    [InlineData("v1.4.0", 1, 4, 0, null)]
    [InlineData("1.4", 1, 4, 0, null)]
    [InlineData("1.4.0-rc.1", 1, 4, 0, "rc.1")]
    [InlineData("2026.9.24", 2026, 9, 24, null)]
    public void A_version_is_read_the_way_people_write_it(
        string typed, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(ReleaseVersion.TryParse(typed, out var version));

        Assert.Equal(new ReleaseVersion(major, minor, patch, prerelease), version);
    }

    /// <summary>
    /// A version that cannot be ordered is refused.
    /// </summary>
    /// <remarks>
    /// Because the alternative is a release list that cannot be sorted, and a "what is out"
    /// answer that is whichever row came back first.
    /// </remarks>
    [Theory]
    [InlineData("September release")]
    [InlineData("final-final")]
    [InlineData("1")]
    [InlineData("1.4.0.1")]
    [InlineData("1.4.0-")]
    [InlineData("")]
    public void A_version_that_cannot_be_ordered_is_refused(string typed) =>
        Assert.False(ReleaseVersion.TryParse(typed, out _));

    /// <summary>
    /// The eleventh release sorts after the tenth.
    /// </summary>
    /// <remarks>
    /// The whole reason this is a type. Sorted as text, 1.10.0 precedes 1.9.0, so the release
    /// list reverses itself the moment a project reaches ten minor versions and the live version
    /// shown is the wrong one — with nothing failing, nothing logged and no reason to look.
    /// </remarks>
    [Fact]
    public void Versions_order_by_number_and_not_by_text()
    {
        var ordered = new[] { "1.9.0", "1.10.0", "1.4.0-rc.2", "1.4.0", "1.4.0-rc.10", "2.0.0" }
            .Select(ReleaseVersion.Parse)
            .OrderBy(one => one)
            .Select(one => one.ToString())
            .ToArray();

        Assert.Equal(
            ["1.4.0-rc.2", "1.4.0-rc.10", "1.4.0", "1.9.0", "1.10.0", "2.0.0"],
            ordered);
    }

    /// <summary>
    /// A release out of two candidates is the one that is out.
    /// </summary>
    /// <remarks>
    /// Kept as its own test rather than folded into the ordering above, because this is the rule
    /// a hand-rolled comparison always gets backwards: 1.4.0 is greater than 1.4.0-rc.2, even
    /// though its text is shorter and it has fewer parts.
    /// </remarks>
    [Fact]
    public void A_release_beats_its_own_candidates() =>
        Assert.True(ReleaseVersion.Parse("1.4.0") > ReleaseVersion.Parse("1.4.0-rc.9"));

    /// <summary>
    /// One version per repository, and the database is what enforces it.
    /// </summary>
    /// <remarks>
    /// The service checks first and gives a better sentence, but the check reads before it
    /// writes: two people preparing 1.4.0 in the same minute both pass it. This asserts the
    /// index underneath, because two rows claiming to be the same version would put two answers
    /// on the screen that exists to give one.
    /// </remarks>
    [Fact]
    public async Task Two_releases_cannot_claim_the_same_version()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();

        context.Releases.Add(Release.Prepare(
            repository, ReleaseVersion.Parse("1.4.0"), Sha(1), Lead, fixture.Clock.Now));

        context.Releases.Add(Release.Prepare(
            repository, ReleaseVersion.Parse("1.4.0"), Sha(2), Lead, fixture.Clock.Now));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// A version that was prepared and cut can be used again.
    /// </summary>
    /// <remarks>
    /// The other half of the same index, and the reason it is filtered. A number that was never
    /// announced is not spent, and a firm that cut 1.4.0 on Monday releases 1.4.0 on Thursday —
    /// forcing them to 1.4.1 would make the version history a record of this system's
    /// bookkeeping rather than of the firm's software.
    /// </remarks>
    [Fact]
    public async Task A_version_that_was_abandoned_can_be_used_again()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using (var context = fixture.NewContext())
        {
            var cut = Release.Prepare(
                repository, ReleaseVersion.Parse("1.4.0"), Sha(1), Lead, fixture.Clock.Now);

            cut.Abandon("the migration was not ready");

            context.Releases.Add(cut);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.NewContext())
        {
            var service = Service(fixture, context);

            var again = await service.PrepareAsync(
                repository, "1.4.0", Sha(2), Lead);

            Assert.Equal(ReleaseVersion.Parse("1.4.0"), again.Version);
        }
    }

    /// <summary>
    /// Preparing a version that is already out says which release has it.
    /// </summary>
    /// <remarks>
    /// Asserted on the sentence rather than only on the refusal, because somebody who typed a
    /// version that exists nearly always wants to know what happened to it. "1.4.0 already
    /// exists here — it was rolled back" ends the question; "that version is taken" starts
    /// another one.
    /// </remarks>
    [Fact]
    public async Task Preparing_a_version_that_exists_says_what_happened_to_it()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using (var context = fixture.NewContext())
        {
            var service = Service(fixture, context);
            var first = await service.PrepareAsync(repository, "1.4.0", Sha(1), Lead);

            await service.DeclareAsync(first.Id, Lead);
            await service.RollBackAsync(first.Id, Lead, "the PDF came out blank");
        }

        await using (var context = fixture.NewContext())
        {
            var service = Service(fixture, context);

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.PrepareAsync(repository, "1.4.0", Sha(2), Lead));

            Assert.Contains("rolled back", refusal.Message);
        }
    }

    /// <summary>
    /// What is out is the highest version still out, not the one declared most recently.
    /// </summary>
    /// <remarks>
    /// The case: 1.4.0 goes out, and on Tuesday a patch to the old line goes out as 1.3.3. The
    /// firm is still running 1.4.0. Ordering by when each was declared would say 1.3.3, which is
    /// a false statement on the one screen anybody checks in an incident.
    /// </remarks>
    [Fact]
    public async Task The_live_version_is_the_highest_one_out_and_not_the_newest_declared()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var newer = await service.PrepareAsync(repository, "1.4.0", Sha(1), Lead);
        await service.DeclareAsync(newer.Id, Lead);

        fixture.Clock.Advance(TimeSpan.FromDays(1));

        var patch = await service.PrepareAsync(repository, "1.3.3", Sha(2), Lead);
        await service.DeclareAsync(patch.Id, Lead);

        var history = await service.HistoryAsync(repository);

        Assert.Equal(ReleaseVersion.Parse("1.4.0"), history.Live!.Version);
    }

    /// <summary>
    /// A version rolled back is not what the firm is running.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_version_is_not_live_and_the_one_under_it_is()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var older = await service.PrepareAsync(repository, "1.3.2", Sha(1), Lead);
        await service.DeclareAsync(older.Id, Lead);

        var broken = await service.PrepareAsync(repository, "1.4.0", Sha(2), Lead);
        await service.DeclareAsync(broken.Id, Lead);
        await service.RollBackAsync(broken.Id, Lead, "the invoice PDF came out blank in USD");

        var history = await service.HistoryAsync(repository);

        Assert.Equal(ReleaseVersion.Parse("1.3.2"), history.Live!.Version);
        Assert.Equal(
            "the invoice PDF came out blank in USD",
            history.Releases.Single(one => one.Version == ReleaseVersion.Parse("1.4.0")).Outcome);
    }

    /// <summary>
    /// Rolling back without a reason is refused.
    /// </summary>
    /// <remarks>
    /// The reason is the most useful field on the aggregate. A history of versions says what the
    /// firm shipped; a history of versions with the withdrawals explained says what it learned,
    /// and only one of those is worth keeping.
    /// </remarks>
    [Fact]
    public async Task A_rollback_has_to_say_why()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var release = await service.PrepareAsync(repository, "1.4.0", Sha(1), Lead);
        await service.DeclareAsync(release.Id, Lead);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RollBackAsync(release.Id, Lead, "   "));
    }

    /// <summary>
    /// Once a version is out, its notes are added to rather than rewritten.
    /// </summary>
    /// <remarks>
    /// Both halves, because either one alone is the wrong design. Rewriting would mean the
    /// record of what shipped can be changed after people have read it; refusing every change
    /// would mean something left out stays out forever, so somebody keeps the real changelog
    /// somewhere else.
    /// </remarks>
    [Fact]
    public async Task Notes_freeze_when_a_version_goes_out_and_can_still_be_added_to()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var release = await service.PrepareAsync(repository, "1.4.0", Sha(1), Lead);

        await service.WriteAsync(release.Id, "- Invoices can be sent in USD");
        await service.DeclareAsync(release.Id, Lead);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteAsync(release.Id, "- Something else entirely"));

        await service.AmendAsync(release.Id, "the CSV export was in this too");

        var after = await context.Releases.SingleAsync(one => one.Id == release.Id);

        Assert.Contains("- Invoices can be sent in USD", after.Notes);
        Assert.Contains("the CSV export was in this too", after.Notes);
    }

    /// <summary>
    /// A changelog is the commits since the last release, and only those.
    /// </summary>
    /// <remarks>
    /// The test that says the window is a window. Three commits: one before the previous
    /// release, one after it, and a merge. The first belongs to the previous release's
    /// changelog, the merge says nothing about what changed, and only the middle one should
    /// come out — off by one at either boundary puts a commit in two changelogs or in none, and
    /// nobody reading the result would know.
    /// </remarks>
    [Fact]
    public async Task A_changelog_holds_the_commits_between_two_releases()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);
        var work = await GiveWork(fixture, 412, "Send invoices in USD");

        var first = fixture.Clock.Now;

        await Push(fixture, repository, Sha(1), "Add the exchange rate table", null, first);

        var second = first.AddDays(2);

        await Push(fixture, repository, Sha(2), "Send invoices in USD", work, second);

        var third = first.AddDays(3);

        await Push(fixture, repository, Sha(3), "Merge pull request #41 from usd", work, third);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var earlier = await service.PrepareAsync(repository, "1.3.0", Sha(1), Lead);
        await service.DeclareAsync(earlier.Id, Lead);

        var later = await service.PrepareAsync(repository, "1.4.0", Sha(3), Lead);

        var draft = await service.DraftNotesAsync(later.Id);

        Assert.Contains("#412 Send invoices in USD", draft.Text);
        Assert.Contains("Send invoices in USD", draft.Text);
        Assert.DoesNotContain("exchange rate table", draft.Text);
        Assert.DoesNotContain("Merge pull request", draft.Text);
    }

    /// <summary>
    /// The first release of a repository says what its changelog actually is.
    /// </summary>
    /// <remarks>
    /// With nothing before it the window has no floor, so what comes back is every commit ever
    /// pushed — which is the project's history rather than a changelog. The caveat is the whole
    /// point of the test: an unexplained wall of two hundred lines gets published.
    /// </remarks>
    [Fact]
    public async Task The_first_release_says_that_its_window_has_no_start()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await Push(fixture, repository, Sha(1), "Begin", null, fixture.Clock.Now);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var first = await service.PrepareAsync(repository, "1.0.0", Sha(1), Lead);
        var draft = await service.DraftNotesAsync(first.Id);

        Assert.Contains("Nothing has been released here before", draft.Caveat);
        Assert.Contains("Begin", draft.Text);
    }

    /// <summary>
    /// A release of a commit this system never saw says so rather than drafting nothing.
    /// </summary>
    /// <remarks>
    /// Which happens whenever a repository is connected after the fact, or released from a
    /// branch nobody pushed here. An empty changelog with no explanation is indistinguishable
    /// from a fortnight in which nothing happened.
    /// </remarks>
    [Fact]
    public async Task A_release_of_an_unknown_commit_says_the_commit_is_unknown()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var repository = await Connect(fixture);

        await using var context = fixture.NewContext();
        var service = Service(fixture, context);

        var release = await service.PrepareAsync(repository, "1.0.0", Sha(9), Lead);
        var draft = await service.DraftNotesAsync(release.Id);

        Assert.Contains("has not been recorded here", draft.Caveat);
        Assert.Empty(draft.Text);
    }

    private static ReleaseService Service(DatabaseFixture fixture, TestDbContext context) =>
        new(new ReleaseRepository(context), fixture.Clock);

    private static async Task<Guid> Connect(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var repository = JiranisokoTech.Domain.Engineering.Repository.Connect(
            GitProvider.GitHub, "jiranisoko", "erp", null, "hash", fixture.Clock.Now);

        context.Repositories.Add(repository);
        await context.SaveChangesAsync();

        return repository.Id;
    }

    private static async Task<Guid> GiveWork(DatabaseFixture fixture, int number, string title)
    {
        await using var context = fixture.NewContext();

        var item = WorkItem.Raise(number, title, Guid.CreateVersion7());

        context.WorkItems.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    private static async Task Push(
        DatabaseFixture fixture,
        Guid repository,
        string sha,
        string message,
        Guid? work,
        DateTimeOffset at)
    {
        await using var context = fixture.NewContext();

        context.Commits.Add(Commit.Record(
            repository, sha, message, "meshtirop1", "main", work, at));

        await context.SaveChangesAsync();
    }

    /// <summary>A plausible forty-character hash, distinct per number.</summary>
    private static string Sha(int which) =>
        new string((char)('a' + which), 1) + new string('0', 38) + which;
}
