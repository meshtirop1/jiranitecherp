using JiranisokoTech.Application.Knowledge;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Knowledge;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Infrastructure.Knowledge;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Infrastructure.Search;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Knowledge;

/// <summary>
/// What the firm knows, and whether anybody can still believe it.
/// </summary>
/// <remarks>
/// Section 25. Almost every test here is about the review date rather than about writing,
/// reading or filing, and that is the design being asserted rather than an accident of what was
/// easy to test: an article nobody has confirmed is the failure this section exists to prevent,
/// and everything else in it is ordinary.
/// </remarks>
public class KnowledgeTests
{
    [Fact]
    public async Task An_article_starts_as_a_draft_at_an_address_made_from_its_title()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync(
            "Deploying by hand",
            "The steps to follow when the pipeline is unavailable",
            "Take the last green build and copy it across.",
            me,
            "Deployment, runbooks");

        Assert.Equal("deploying-by-hand", article.Key);
        Assert.Equal(ArticleState.Draft, article.State);
        Assert.Null(article.ReviewBy);

        // Lower-cased and de-duplicated, so one label is one label however it was typed.
        Assert.Equal(["deployment", "runbooks"], article.Labelled());
    }

    /// <summary>
    /// Two articles may share a title, and they may not share an address.
    /// </summary>
    /// <remarks>
    /// One about the pipeline and one about doing it by hand is an ordinary pair for a firm to
    /// have, and the second must not fail to save with a unique index violation somebody has to
    /// read a stack trace to understand.
    /// </remarks>
    [Fact]
    public async Task A_second_article_with_the_same_title_gets_its_own_address()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var first = await Service(fixture).StartAsync("Deploying", "The pipeline", "…", me);
        var second = await Service(fixture).StartAsync("Deploying", "By hand", "…", me);
        var third = await Service(fixture).StartAsync("Deploying", "Rolling back", "…", me);

        Assert.Equal("deploying", first.Key);
        Assert.Equal("deploying-2", second.Key);
        Assert.Equal("deploying-3", third.Key);
    }

    /// <summary>
    /// The address does not move when the title does.
    /// </summary>
    /// <remarks>
    /// The one here that costs something if it is got wrong, and nothing on the screen would
    /// say so: somebody pastes a link into a ticket, somebody else corrects a typo in the title
    /// two months later, and the link in the ticket opens nothing.
    /// </remarks>
    [Fact]
    public async Task Correcting_the_title_does_not_move_the_address()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync(
            "Deploing by hand", "Typo in the title", "…", me);

        await Service(fixture).RewriteAsync(
            article.Id, "Deploying by hand", "Typo in the title", "…", null);

        await using var context = fixture.NewContext();
        var stored = await context.Articles.SingleAsync();

        Assert.Equal("Deploying by hand", stored.Title);
        Assert.Equal("deploing-by-hand", stored.Key);
    }

    [Fact]
    public async Task Publishing_records_the_version_the_firm_stood_behind()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync(
            "Deploying", "How a release goes out", "Press the button.", me);

        await Service(fixture).PublishAsync(
            article.Id, me, fixture.Clock.Today.AddMonths(6), "First version");

        await Service(fixture).RewriteAsync(
            article.Id, "Deploying", "How a release goes out", "Press the other button.", null);

        await Service(fixture).PublishAsync(
            article.Id, me, fixture.Clock.Today.AddMonths(6), "The button moved");

        await using var context = fixture.NewContext();
        var stored = await context.Articles.SingleAsync();

        Assert.Equal(2, stored.Revisions.Count);
        Assert.Equal("Press the other button.", stored.Revisions[0].Body);
        Assert.Equal("Press the button.", stored.Revisions[1].Body);
    }

    /// <summary>
    /// An edit is not a version. Only publishing is.
    /// </summary>
    /// <remarks>
    /// The decision that keeps the revision list short enough to read. A revision per save is a
    /// table of half-finished sentences, and nobody has ever usefully read one.
    /// </remarks>
    [Fact]
    public async Task Saving_an_article_writes_no_version()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "One.", me);

        await Service(fixture).RewriteAsync(article.Id, "Deploying", "How", "Two.", null);
        await Service(fixture).RewriteAsync(article.Id, "Deploying", "How", "Three.", null);

        await using var context = fixture.NewContext();

        Assert.Empty((await context.Articles.SingleAsync()).Revisions);
    }

    [Fact]
    public async Task A_review_date_that_has_already_passed_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fixture).PublishAsync(
                article.Id, me, fixture.Clock.Today.AddDays(-1)));

        Assert.Contains("already passed", refused.Message);
    }

    /// <summary>
    /// And so is one nobody will reach.
    /// </summary>
    /// <remarks>
    /// The other half, and the one somebody reaches for when they want the box to go away. A
    /// five-year promise is not a promise; it is a way of publishing without making one.
    /// </remarks>
    [Fact]
    public async Task A_review_date_further_off_than_two_years_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fixture).PublishAsync(
                article.Id, me, fixture.Clock.Today.AddYears(5)));

        Assert.Contains("Two years", refused.Message);
    }

    /// <summary>
    /// Confirming an article moves the clock and writes no version.
    /// </summary>
    /// <remarks>
    /// Kept separate from publishing on purpose. If saying "this is still true" meant
    /// republishing, nobody would ever say it, and the review date would come to measure how
    /// recently somebody fixed a typo.
    /// </remarks>
    [Fact]
    public async Task Confirming_moves_the_clock_and_writes_no_version()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);
        await Service(fixture).PublishAsync(article.Id, me, fixture.Clock.Today.AddDays(1));

        await Service(fixture).StillTrueAsync(
            article.Id, me, fixture.Clock.Today.AddMonths(12));

        await using var context = fixture.NewContext();
        var stored = await context.Articles.SingleAsync();

        Assert.Equal(fixture.Clock.Today.AddMonths(12), stored.ReviewBy);
        Assert.Equal(me, stored.LastCheckedById);
        Assert.Single(stored.Revisions);
    }

    [Fact]
    public void An_article_past_its_review_date_is_stale()
    {
        var me = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var article = Article.Start(
            Slug.From("Deploying"), "Deploying", "How", "…", me, now);

        article.Publish(me, today.AddDays(30), now);

        Assert.False(article.IsStaleOn(today.AddDays(29)));
        Assert.False(article.IsStaleOn(today.AddDays(30)));
        Assert.True(article.IsStaleOn(today.AddDays(31)));
    }

    /// <summary>
    /// A retired article cannot be stale, because there is nothing left to be wrong.
    /// </summary>
    /// <remarks>
    /// Leaving the date on it would put every article the firm has ever taken down on the list
    /// of things somebody owes a review, which is how that list stops being read.
    /// </remarks>
    [Fact]
    public async Task Retiring_an_article_takes_it_off_the_list_of_things_owed_a_check()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);
        await Service(fixture).PublishAsync(article.Id, me, fixture.Clock.Today.AddDays(1));
        await Service(fixture).RetireAsync(article.Id, "The pipeline does this now.");

        await using var context = fixture.NewContext();
        var stored = await context.Articles.SingleAsync();

        Assert.Equal(ArticleState.Retired, stored.State);
        Assert.Null(stored.ReviewBy);
        Assert.False(stored.IsStaleOn(fixture.Clock.Today.AddYears(1)));

        Assert.Equal(
            0,
            await new KnowledgeQueries(context).StaleCountAsync(
                fixture.Clock.Today.AddYears(1)));
    }

    [Fact]
    public async Task A_retired_article_cannot_be_edited_or_put_back_up()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);
        await Service(fixture).RetireAsync(article.Id, "Superseded.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fixture).RewriteAsync(article.Id, "Deploying", "How", "…", null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(fixture).PublishAsync(
                article.Id, me, fixture.Clock.Today.AddMonths(6)));
    }

    [Fact]
    public async Task Taking_an_article_down_without_saying_why_is_refused()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var article = await Service(fixture).StartAsync("Deploying", "How", "…", me);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(fixture).RetireAsync(article.Id, "   "));
    }

    /// <summary>The list shows what is up, and nothing half-written.</summary>
    [Fact]
    public async Task Drafts_and_retired_articles_stay_off_the_ordinary_list()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var up = await Service(fixture).StartAsync("Deploying", "How", "…", me);
        await Service(fixture).PublishAsync(up.Id, me, fixture.Clock.Today.AddMonths(6));

        await Service(fixture).StartAsync("Half a thought", "Not finished", "…", me);

        var gone = await Service(fixture).StartAsync("The old way", "Superseded", "…", me);
        await Service(fixture).RetireAsync(gone.Id, "We do it differently.");

        await using var context = fixture.NewContext();
        var queries = new KnowledgeQueries(context);

        var ordinary = await queries.ListAsync(fixture.Clock.Today);
        Assert.Equal("Deploying", Assert.Single(ordinary).Title);

        var everything = await queries.ListAsync(
            fixture.Clock.Today, includeUnpublished: true);
        Assert.Equal(3, everything.Count);
    }

    /// <summary>
    /// A search finds a published article and never a draft.
    /// </summary>
    /// <remarks>
    /// Through the global search rather than a box of its own, which is the integration this
    /// section turns on: the moment worth catching is the one before somebody has decided the
    /// answer is written down, when they type a word into the box they already use.
    ///
    /// The draft half is the part with a cost attached. A search result is read as an answer,
    /// and somebody's half-written thought presented as one is worse than no result.
    /// </remarks>
    [Fact]
    public async Task The_search_box_finds_published_articles_and_not_drafts()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var me = await GiveStaff(fixture);

        var up = await Service(fixture).StartAsync(
            "Deploying by hand", "When the pipeline is unavailable", "…", me, "deployment");
        await Service(fixture).PublishAsync(up.Id, me, fixture.Clock.Today.AddMonths(6));

        await Service(fixture).StartAsync("Deploying, the rewrite", "Half written", "…", me);

        await using var context = fixture.NewContext();
        var found = await new SearchQueries(context, Reaches(context))
            .FindAsync("deploying", new HashSet<string>());

        var article = Assert.Single(found, one => one.Kind == ResultKind.Article);

        Assert.Equal("Deploying by hand", article.Title);
        Assert.Equal("/knowledge/deploying-by-hand", article.Href);
    }

    private static KnowledgeService Service(DatabaseFixture fixture) =>
        new(new KnowledgeRepository(fixture.NewContext()), fixture.Clock);

    private static JiranisokoTech.Infrastructure.Authorization.Reaches Reaches(
        AppDbContext context) => new(context);

    private static async Task<Guid> GiveStaff(DatabaseFixture fixture)
    {
        await using var context = fixture.NewContext();

        var person = Employee.Hire("Mesh Tirop", fixture.Clock.Today);

        context.Employees.Add(person);
        await context.SaveChangesAsync();

        return person.Id;
    }
}
