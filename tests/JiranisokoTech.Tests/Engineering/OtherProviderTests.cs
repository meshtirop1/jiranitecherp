using System.Security.Cryptography;
using System.Text;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Engineering;

namespace JiranisokoTech.Tests.Engineering;

/// <summary>
/// GitLab, Bitbucket and Azure DevOps.
/// </summary>
/// <remarks>
/// The payloads here are trimmed from each provider's published examples, not
/// captured from a live instance — which is exactly what these tests prove and
/// what they do not. They prove each adapter reads what its provider documents.
/// They cannot prove a particular version of that provider sends what it
/// documents, and only GitHub has been verified against real deliveries.
///
/// That distinction is worth keeping in view rather than letting four green
/// adapters imply four working integrations. The first real delivery from any of
/// these three is the test that counts, and the delivery inbox is built so that
/// getting it wrong costs nothing: the body is recorded before it is understood,
/// so a payload shape that turns out to differ dead-letters and can be replayed
/// once this file has been corrected.
/// </remarks>
public class OtherProviderTests
{
    private const string Secret = "the-shared-secret";

    // --- GitLab -------------------------------------------------------------

    private readonly GitLabProvider _gitlab = new();

    /// <summary>
    /// GitLab's token is compared, not verified.
    /// </summary>
    /// <remarks>
    /// The weakness is asserted rather than hidden, because somebody choosing a
    /// provider should be able to see it here: GitLab sends the secret in
    /// plaintext, so a correct token over a tampered body passes. GitHub's HMAC
    /// would refuse that. This is why the adapter's own remarks recommend against
    /// GitLab where there is a choice.
    /// </remarks>
    [Fact]
    public void A_gitlab_delivery_is_accepted_on_its_token_alone()
    {
        Assert.True(_gitlab.IsSigned([1, 2, 3], Secret, Secret));
        Assert.True(_gitlab.IsSigned([9, 9, 9], Secret, Secret));
        Assert.False(_gitlab.IsSigned([1, 2, 3], "somebody-elses-secret", Secret));
        Assert.False(_gitlab.IsSigned([1, 2, 3], null, Secret));
    }

    [Fact]
    public void A_gitlab_push_is_read()
    {
        var payload = """
            {
              "object_kind": "push",
              "ref": "refs/heads/feature/412-payment-api",
              "project": { "path_with_namespace": "jiranisokotech/erp" },
              "commits": [
                {
                  "id": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                  "message": "Add the retry\nand a body",
                  "timestamp": "2026-09-22T14:03:11+03:00",
                  "author": { "name": "Meshack Tirop", "email": "m@example.com" }
                }
              ]
            }
            """;

        Assert.Equal("jiranisokotech/erp", _gitlab.RepositoryIn(payload));
        Assert.Equal("push", _gitlab.EventIn(Headers(), payload));

        var pushed = Assert.IsType<GitEvent.Pushed>(_gitlab.Read("push", payload));

        Assert.Equal("feature/412-payment-api", pushed.Branch);
        Assert.Equal("Add the retry", Assert.Single(pushed.Commits).Message);
        Assert.Equal("Meshack Tirop", pushed.Commits[0].Author);
    }

    /// <summary>
    /// The event comes from the body, not the header.
    /// </summary>
    /// <remarks>
    /// GitLab sends "Merge Request Hook" in the header for opening, merging,
    /// closing and approving alike. Reading the header would put four different
    /// events under one name on the deliveries screen.
    /// </remarks>
    [Fact]
    public void A_gitlab_event_is_named_by_its_payload()
    {
        var headers = Headers(("X-Gitlab-Event", "Merge Request Hook"));

        Assert.Equal(
            "merge_request",
            _gitlab.EventIn(headers, """{"object_kind":"merge_request"}"""));

        // And falls back to the header when the body will not parse, because a
        // delivery still has to be recorded before anybody can see why.
        Assert.Equal("Merge Request Hook", _gitlab.EventIn(headers, "not json at all"));
    }

    [Fact]
    public void A_gitlab_merge_is_read_as_a_merge()
    {
        var change = Assert.IsType<GitEvent.PullRequestChanged>(
            _gitlab.Read("merge_request", MergeRequest("merge"))).Change;

        Assert.Equal(PullRequestAction.Merged, change.Action);

        // iid, not id. The iid is the number people say; id is GitLab's internal
        // row identifier and means nothing on a board.
        Assert.Equal(412, change.Number);
        Assert.Equal("feature/412-payment-api", change.Branch);
    }

    [Theory]
    [InlineData("open", PullRequestAction.Opened)]
    [InlineData("reopen", PullRequestAction.Opened)]
    [InlineData("update", PullRequestAction.Updated)]
    [InlineData("close", PullRequestAction.Closed)]
    public void A_gitlab_merge_request_action_is_mapped(string action, PullRequestAction expected) =>
        Assert.Equal(
            expected,
            Assert.IsType<GitEvent.PullRequestChanged>(
                _gitlab.Read("merge_request", MergeRequest(action))).Change.Action);

    /// <summary>
    /// GitLab folds approval into the same event as the state change.
    /// </summary>
    /// <remarks>
    /// One payload shape therefore produces either a change or a review, which is
    /// why the adapter returns the union type rather than one of them.
    /// </remarks>
    [Fact]
    public void A_gitlab_approval_is_read_as_a_review()
    {
        var reviewed = Assert.IsType<GitEvent.Reviewed>(
            _gitlab.Read("merge_request", MergeRequest("approved")));

        Assert.Equal(412, reviewed.Number);
        Assert.Equal(ReviewVerdict.Approved, reviewed.Verdict);
        Assert.Equal("vincent", reviewed.Reviewer);

        // Keyed on reviewer and merge request, because GitLab gives an approval no
        // identifier of its own — so a redelivery is the same approval.
        Assert.Contains("vincent", reviewed.ExternalId);
    }

    [Fact]
    public void A_gitlab_tag_push_is_left_alone() =>
        Assert.IsType<GitEvent.Uninteresting>(
            _gitlab.Read("tag_push", """{"object_kind":"tag_push","ref":"refs/tags/v1.4.0"}"""));

    // --- Bitbucket ----------------------------------------------------------

    private readonly BitbucketProvider _bitbucket = new();

    /// <summary>
    /// Bitbucket signs the body, and uses a header GitHub does not.
    /// </summary>
    /// <remarks>
    /// `X-Hub-Signature` without the algorithm suffix. Reading GitHub's header
    /// name here would find nothing and refuse every delivery as unsigned, which
    /// looks exactly like a wrong secret.
    /// </remarks>
    [Fact]
    public void A_bitbucket_delivery_is_verified_by_signature()
    {
        var body = Encoding.UTF8.GetBytes("""{"repository":{"full_name":"jst/erp"}}""");
        var headers = Headers(("X-Hub-Signature", Signed(body, Secret)));

        Assert.Equal(Signed(body, Secret), _bitbucket.SignatureIn(headers));
        Assert.True(_bitbucket.IsSigned(body, _bitbucket.SignatureIn(headers), Secret));
        Assert.False(_bitbucket.IsSigned(body, Signed(body, "wrong"), Secret));

        // GitHub's header name is not Bitbucket's.
        Assert.Null(_bitbucket.SignatureIn(
            Headers(("X-Hub-Signature-256", Signed(body, Secret)))));
    }

    [Fact]
    public void A_bitbucket_push_is_read_from_its_changes()
    {
        var payload = """
            {
              "repository": { "full_name": "jiranisokotech/erp" },
              "push": {
                "changes": [
                  {
                    "new": { "type": "branch", "name": "feature/412-payment-api" },
                    "commits": [
                      {
                        "hash": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                        "message": "Add the retry",
                        "date": "2026-09-22T14:03:11+03:00",
                        "author": { "user": { "nickname": "meshtirop1" } }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var pushed = Assert.IsType<GitEvent.Pushed>(_bitbucket.Read("repo:push", payload));

        Assert.Equal("feature/412-payment-api", pushed.Branch);
        Assert.Equal("meshtirop1", Assert.Single(pushed.Commits).Author);
    }

    /// <summary>A branch deletion carries a null "new" and is skipped.</summary>
    [Fact]
    public void A_bitbucket_branch_deletion_is_left_alone() =>
        Assert.IsType<GitEvent.Uninteresting>(_bitbucket.Read(
            "repo:push",
            """{"push":{"changes":[{"new":null,"old":{"type":"branch","name":"gone"}}]}}"""));

    /// <summary>
    /// Fulfilled means merged and rejected means declined.
    /// </summary>
    /// <remarks>
    /// Nothing about either word says so, which is why the mapping is asserted.
    /// Reading "rejected" as anything other than closed-without-merging would put
    /// abandoned branches on the board as shipped work, or the reverse.
    /// </remarks>
    [Theory]
    [InlineData("pullrequest:created", PullRequestAction.Opened)]
    [InlineData("pullrequest:updated", PullRequestAction.Updated)]
    [InlineData("pullrequest:fulfilled", PullRequestAction.Merged)]
    [InlineData("pullrequest:rejected", PullRequestAction.Closed)]
    public void A_bitbucket_pull_request_event_is_mapped(
        string key, PullRequestAction expected)
    {
        var change = Assert.IsType<GitEvent.PullRequestChanged>(
            _bitbucket.Read(key, BitbucketPullRequest())).Change;

        Assert.Equal(expected, change.Action);
        Assert.Equal(412, change.Number);
        Assert.Equal("feature/412-payment-api", change.Branch);
    }

    [Fact]
    public void A_bitbucket_approval_is_read_as_a_review()
    {
        var reviewed = Assert.IsType<GitEvent.Reviewed>(
            _bitbucket.Read("pullrequest:approved", BitbucketPullRequest()));

        Assert.Equal(412, reviewed.Number);
        Assert.Equal(ReviewVerdict.Approved, reviewed.Verdict);
        Assert.Equal("vincent", reviewed.Reviewer);
    }

    // --- Azure DevOps -------------------------------------------------------

    private readonly AzureDevOpsProvider _azure = new();

    /// <summary>
    /// Azure DevOps authenticates with basic credentials, not a signature.
    /// </summary>
    [Fact]
    public void An_azure_delivery_is_accepted_on_its_credentials()
    {
        var headers = Headers(("Authorization", $"Basic {Secret}"));

        Assert.Equal(Secret, _azure.SignatureIn(headers));
        Assert.True(_azure.IsSigned([1, 2, 3], _azure.SignatureIn(headers), Secret));

        // A bearer token is not basic authentication, and is not accepted as one.
        Assert.Null(_azure.SignatureIn(Headers(("Authorization", $"Bearer {Secret}"))));
    }

    /// <summary>
    /// Its delivery identifier is in the body.
    /// </summary>
    /// <remarks>
    /// The reason the adapter seam passes the payload to DeliveryIdIn at all. No
    /// identifier means no idempotency and no replay protection, so a payload that
    /// will not parse is refused rather than given a substitute.
    /// </remarks>
    [Fact]
    public void An_azure_delivery_identifier_comes_from_the_body()
    {
        Assert.Equal(
            "c1a2b3d4-0000-0000-0000-000000000001",
            _azure.DeliveryIdIn(Headers(), AzurePush()));

        Assert.Null(_azure.DeliveryIdIn(Headers(), "not json at all"));
    }

    [Fact]
    public void An_azure_push_is_read()
    {
        Assert.Equal("git.push", _azure.EventIn(Headers(), AzurePush()));
        Assert.Equal("Delivery/erp", _azure.RepositoryIn(AzurePush()));

        var pushed = Assert.IsType<GitEvent.Pushed>(_azure.Read("git.push", AzurePush()));

        Assert.Equal("feature/412-payment-api", pushed.Branch);

        // "comment" is Azure DevOps' name for a commit message.
        Assert.Equal("Add the retry", Assert.Single(pushed.Commits).Message);
    }

    /// <summary>
    /// An abandoned pull request arrives as an update, not as its own event.
    /// </summary>
    /// <remarks>
    /// There is no git.pullrequest.abandoned. Reading the status is the difference
    /// between a board that shows abandoned work as still open and one that does
    /// not.
    /// </remarks>
    [Fact]
    public void An_abandoned_azure_pull_request_is_read_as_closed()
    {
        var change = Assert.IsType<GitEvent.PullRequestChanged>(
            _azure.Read("git.pullrequest.updated", AzurePullRequest("abandoned"))).Change;

        Assert.Equal(PullRequestAction.Closed, change.Action);
    }

    [Fact]
    public void An_active_azure_pull_request_update_is_only_an_update() =>
        Assert.Equal(
            PullRequestAction.Updated,
            Assert.IsType<GitEvent.PullRequestChanged>(
                _azure.Read("git.pullrequest.updated", AzurePullRequest("active"))).Change.Action);

    [Fact]
    public void An_azure_merge_is_read_as_a_merge()
    {
        var change = Assert.IsType<GitEvent.PullRequestChanged>(
            _azure.Read("git.pullrequest.merged", AzurePullRequest("completed"))).Change;

        Assert.Equal(PullRequestAction.Merged, change.Action);
        Assert.Equal(412, change.Number);
        Assert.Equal("feature/412-payment-api", change.Branch);
        Assert.Equal("meshack@jiranisokotech.co.ke", change.Author);
    }

    /// <summary>
    /// Every adapter answers an unknown event rather than throwing on it.
    /// </summary>
    /// <remarks>
    /// The single most important shared behaviour. Providers send a great deal
    /// nothing here acts on, and an adapter that raised on the unfamiliar would
    /// fill the failure list with things nobody needs to do anything about until
    /// nobody read it at all.
    /// </remarks>
    [Fact]
    public void Every_adapter_ignores_what_it_does_not_act_on()
    {
        IGitProvider[] adapters = [new GitHubProvider(), _gitlab, _bitbucket, _azure];

        foreach (var adapter in adapters)
        {
            Assert.IsType<GitEvent.Uninteresting>(adapter.Read("something:nobody:sends", "{}"));
        }
    }

    /// <summary>
    /// Each provider is spoken for exactly once.
    /// </summary>
    /// <remarks>
    /// The inbox picks an adapter by asking each which provider it is for, so two
    /// answering the same one would mean deliveries handled by whichever the
    /// container happened to return first. A provider with no adapter is refused
    /// at the door, which is visible; the wrong adapter silently misreads.
    /// </remarks>
    [Fact]
    public void Every_provider_has_exactly_one_adapter()
    {
        IGitProvider[] adapters = [new GitHubProvider(), _gitlab, _bitbucket, _azure];

        Assert.Equal(
            Enum.GetValues<GitProvider>().Order(),
            adapters.Select(adapter => adapter.Provider).Order());
    }

    // --- the payloads -------------------------------------------------------

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] set) =>
        set.ToDictionary(one => one.Name, one => one.Value, StringComparer.OrdinalIgnoreCase);

    private static string Signed(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static string MergeRequest(string action) => $$"""
        {
          "object_kind": "merge_request",
          "project": { "path_with_namespace": "jiranisokotech/erp" },
          "user": { "username": "vincent", "name": "Vincent Bungei" },
          "object_attributes": {
            "id": 99001,
            "iid": 412,
            "title": "Payment API retries",
            "source_branch": "feature/412-payment-api",
            "action": "{{action}}",
            "created_at": "2026-09-22 14:00:00 UTC",
            "updated_at": "2026-09-22 16:45:00 UTC"
          }
        }
        """;

    private static string BitbucketPullRequest() => """
        {
          "repository": { "full_name": "jiranisokotech/erp" },
          "actor": { "nickname": "vincent" },
          "approval": { "user": { "nickname": "vincent" } },
          "pullrequest": {
            "id": 412,
            "title": "Payment API retries",
            "source": { "branch": { "name": "feature/412-payment-api" } },
            "author": { "nickname": "meshtirop1" },
            "created_on": "2026-09-22T14:00:00.000000+00:00",
            "updated_on": "2026-09-22T16:45:00.000000+00:00"
          }
        }
        """;

    private static string AzurePush() => """
        {
          "id": "c1a2b3d4-0000-0000-0000-000000000001",
          "eventType": "git.push",
          "resource": {
            "repository": { "name": "erp", "project": { "name": "Delivery" } },
            "refUpdates": [ { "name": "refs/heads/feature/412-payment-api" } ],
            "commits": [
              {
                "commitId": "9a1c0ff4e6b3d2a18f7c5e0b4d3a2916f8e7c0d5",
                "comment": "Add the retry",
                "author": { "name": "Meshack Tirop", "date": "2026-09-22T14:03:11Z" }
              }
            ]
          }
        }
        """;

    private static string AzurePullRequest(string status) => $$"""
        {
          "id": "c1a2b3d4-0000-0000-0000-000000000002",
          "eventType": "git.pullrequest.updated",
          "resource": {
            "pullRequestId": 412,
            "title": "Payment API retries",
            "sourceRefName": "refs/heads/feature/412-payment-api",
            "status": "{{status}}",
            "createdBy": {
              "uniqueName": "meshack@jiranisokotech.co.ke",
              "displayName": "Meshack Tirop"
            },
            "creationDate": "2026-09-22T14:00:00Z",
            "closedDate": "2026-09-22T16:45:00Z",
            "repository": { "name": "erp", "project": { "name": "Delivery" } }
          }
        }
        """;
}
