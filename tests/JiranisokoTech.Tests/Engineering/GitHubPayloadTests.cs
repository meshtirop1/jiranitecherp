using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// Reading what GitHub actually sends.
/// </summary>
/// <remarks>
/// The payloads here are cut down from real deliveries: the fields this system
/// reads, in the nesting GitHub puts them in. Trimmed rather than invented,
/// because a test written against a payload somebody imagined proves only that
/// the code agrees with the imagination.
///
/// The case that earns this file is the merge. GitHub reports a merge and an
/// abandonment with the same action — "closed" — and separates them by a boolean
/// several levels down. Read carelessly, every branch anybody ever gave up on
/// arrives as shipped work.
/// </remarks>
public class GitHubPayloadTests
{
    private readonly GitHubProvider _provider = new();

    [Fact]
    public void The_repository_is_found_without_understanding_the_event() =>
        Assert.Equal(
            "jiranisokotech/erp",
            _provider.RepositoryIn("""{"repository":{"full_name":"jiranisokotech/erp"}}"""));

    [Fact]
    public void A_payload_naming_no_repository_names_nothing() =>
        Assert.Null(_provider.RepositoryIn("""{"zen":"Keep it logically awesome."}"""));

    [Fact]
    public void A_push_carries_its_branch_and_its_commits()
    {
        var read = _provider.Read("push", """
            {
              "ref": "refs/heads/feature/412-payment-api",
              "repository": { "full_name": "jiranisokotech/erp" },
              "commits": [
                {
                  "id": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                  "message": "Add the retry\n\nWith a long body nobody needs on a board.",
                  "timestamp": "2026-09-22T14:03:11+03:00",
                  "author": { "name": "Meshack Tirop", "username": "meshtirop1" }
                },
                {
                  "id": "1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e",
                  "message": "Tidy the naming",
                  "timestamp": "2026-09-22T14:20:00+03:00",
                  "author": { "name": "Meshack Tirop", "username": "meshtirop1" }
                }
              ]
            }
            """);

        var pushed = Assert.IsType<GitEvent.Pushed>(read);

        Assert.Equal("feature/412-payment-api", pushed.Branch);
        Assert.Equal(2, pushed.Commits.Count);
        Assert.Equal("meshtirop1", pushed.Commits[0].Author);

        // The body is dropped. A board column is not the place for whatever
        // somebody pasted under the subject line.
        Assert.Equal("Add the retry", pushed.Commits[0].Message);

        // The provider's time, not ours: a delivery replayed next week must not
        // claim the commit was made the day somebody pressed the button.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 14, 3, 11, TimeSpan.FromHours(3)),
            pushed.Commits[0].At);
    }

    /// <summary>A tag push is not a branch push.</summary>
    /// <remarks>
    /// Recording it would file every commit in the release a second time, under
    /// a branch name nobody typed.
    /// </remarks>
    [Fact]
    public void A_tag_push_is_left_alone()
    {
        var read = _provider.Read("push", """
            {
              "ref": "refs/tags/v1.4.0",
              "commits": [ { "id": "9a1c0ff", "message": "Release" } ]
            }
            """);

        Assert.IsType<GitEvent.Uninteresting>(read);
    }

    [Fact]
    public void A_branch_deletion_carrying_no_commits_is_left_alone()
    {
        var read = _provider.Read("push", """
            { "ref": "refs/heads/feature/412-payment-api", "deleted": true, "commits": [] }
            """);

        Assert.IsType<GitEvent.Uninteresting>(read);
    }

    [Fact]
    public void An_opened_pull_request_is_read()
    {
        var change = Change("opened", merged: false);

        Assert.Equal(PullRequestAction.Opened, change.Action);
        Assert.Equal(412, change.Number);
        Assert.Equal("Payment API retries", change.Title);
        Assert.Equal("feature/412-payment-api", change.Branch);
        Assert.Equal("meshtirop1", change.Author);
    }

    /// <summary>
    /// A merge is a merge.
    /// </summary>
    /// <remarks>
    /// The test this file exists for. GitHub says "closed" either way, and if
    /// the merged flag is not read then abandoning a branch and shipping it are
    /// the same event as far as this system is concerned — which would move work
    /// to review on the strength of somebody giving up on it.
    /// </remarks>
    [Fact]
    public void A_closed_pull_request_that_merged_is_a_merge()
    {
        var change = Change("closed", merged: true);

        Assert.Equal(PullRequestAction.Merged, change.Action);

        // The moment it merged, which is not the moment it was opened.
        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 16, 45, 0, TimeSpan.Zero), change.At);
    }

    [Fact]
    public void A_closed_pull_request_that_did_not_merge_is_only_closed() =>
        Assert.Equal(PullRequestAction.Closed, Change("closed", merged: false).Action);

    [Fact]
    public void An_approval_is_read()
    {
        var read = _provider.Read("pull_request_review", """
            {
              "action": "submitted",
              "review": {
                "id": 2748193,
                "state": "approved",
                "user": { "login": "vincent" }
              },
              "pull_request": { "number": 412 }
            }
            """);

        var reviewed = Assert.IsType<GitEvent.Reviewed>(read);

        Assert.Equal(412, reviewed.Number);
        Assert.Equal(ReviewVerdict.Approved, reviewed.Verdict);
        Assert.Equal("vincent", reviewed.Reviewer);

        // The provider's own identifier for the review, which is what makes a
        // redelivered approval one approval rather than two.
        Assert.Equal("2748193", reviewed.ExternalId);
    }

    [Fact]
    public void Changes_requested_is_not_an_approval()
    {
        var read = _provider.Read("pull_request_review", """
            {
              "action": "submitted",
              "review": {
                "id": 2748194,
                "state": "changes_requested",
                "user": { "login": "vincent" }
              },
              "pull_request": { "number": 412 }
            }
            """);

        Assert.Equal(
            ReviewVerdict.ChangesRequested,
            Assert.IsType<GitEvent.Reviewed>(read).Verdict);
    }

    /// <summary>
    /// A ping says so, rather than being lumped in with the noise.
    /// </summary>
    /// <remarks>
    /// It is the first delivery anybody setting a webhook up will see, and
    /// "GitHub checking the webhook" on the screen is the difference between
    /// believing it works and guessing.
    /// </remarks>
    [Fact]
    public void A_ping_says_the_connection_works()
    {
        var read = _provider.Read("ping", """{"zen":"Non-blocking is better."}""");

        Assert.Contains("works", Assert.IsType<GitEvent.Uninteresting>(read).Why);
    }

    [Theory]
    [InlineData("star")]
    [InlineData("fork")]
    [InlineData("watch")]
    [InlineData("gollum")]
    public void Events_nothing_here_acts_on_are_ignored_rather_than_failed(string name) =>
        Assert.IsType<GitEvent.Uninteresting>(_provider.Read(name, "{}"));

    /// <summary>
    /// A pull request being labelled is not a change worth recording.
    /// </summary>
    /// <remarks>
    /// The noisiest action GitHub sends on a busy repository. Treating it as an
    /// update would rewrite the mirror dozens of times a day to say nothing.
    /// </remarks>
    [Fact]
    public void A_pull_request_being_labelled_is_ignored() =>
        Assert.IsType<GitEvent.Uninteresting>(
            _provider.Read("pull_request", Payload("labeled", merged: false)));

    private PullRequestChange Change(string action, bool merged) =>
        Assert.IsType<GitEvent.PullRequestChanged>(
            _provider.Read("pull_request", Payload(action, merged))).Change;

    private static string Payload(string action, bool merged) => $$"""
        {
          "action": "{{action}}",
          "repository": { "full_name": "jiranisokotech/erp" },
          "pull_request": {
            "number": 412,
            "title": "Payment API retries",
            "merged": {{(merged ? "true" : "false")}},
            "created_at": "2026-09-22T14:00:00Z",
            "closed_at": "2026-09-22T16:45:00Z",
            "merged_at": {{(merged ? "\"2026-09-22T16:45:00Z\"" : "null")}},
            "head": { "ref": "feature/412-payment-api" },
            "user": { "login": "meshtirop1" }
          }
        }
        """;
}
