using System.Net;
using System.Net.Http.Json;
using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Web.Api;
using Microsoft.Extensions.DependencyInjection;
using JiranisokoTech.Application.Api;

namespace JiranisokoTech.Tests.Api;

/// <summary>
/// Writing through the API, and paging what it reads.
/// </summary>
/// <remarks>
/// The tests that matter are the ones proving a rule is not enforced twice. An invoice
/// refused for a former client on a screen must be refused for the same reason through the
/// API, by the same code — and the way to check that is to make the API break a rule and
/// read the sentence that comes back.
/// </remarks>
public class PublicApiWriteTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    /// <summary>
    /// A limit is applied whether or not one was asked for.
    /// </summary>
    /// <remarks>
    /// The default of "everything" is how the problem got here: fine with forty clients, and
    /// twenty seconds and several megabytes with forty thousand — at which point the caller's
    /// own timeout breaks the integration at a size nobody chose.
    /// </remarks>
    [Fact]
    public void Paging_always_limits_and_never_refuses()
    {
        Assert.Equal(Paging.Default, Paging.From(null, null).Take);
        Assert.Equal(0, Paging.From(null, null).Skip);

        // Clamped rather than rejected: a caller asking for three hundred wants as many as
        // it can have, and a 400 there buys an argument in somebody else's logs.
        Assert.Equal(Paging.Most, Paging.From(0, 100_000).Take);
        Assert.Equal(1, Paging.From(0, 0).Take);

        // A negative skip is read as none, for the same reason.
        Assert.Equal(0, Paging.From(-50, null).Skip);
    }

    /// <summary>
    /// A page says whether there is more, rather than leaving it to arithmetic.
    /// </summary>
    /// <remarks>
    /// A caller that works it out from three numbers will eventually work it out wrongly and
    /// stop one page early, which for an import means silently importing half of one.
    /// </remarks>
    [Fact]
    public void A_page_says_whether_there_is_more()
    {
        var all = Enumerable.Range(1, 120).ToList();

        var first = Paging.Wrap(all, Paging.From(0, 50));

        Assert.Equal(50, first.Items.Count);
        Assert.Equal(120, first.Total);
        Assert.True(first.More);
        Assert.Equal(50, first.NextSkip);

        var last = Paging.Wrap(all, Paging.From(100, 50));

        Assert.Equal(20, last.Items.Count);
        Assert.False(last.More);
        Assert.Null(last.NextSkip);
    }

    /// <summary>Mapping a page keeps the envelope.</summary>
    [Fact]
    public void Mapping_a_page_keeps_where_the_caller_is()
    {
        var page = Paging.Wrap(Enumerable.Range(1, 10).ToList(), Paging.From(2, 3))
            .Map(number => number.ToString());

        Assert.Equal(["3", "4", "5"], page.Items);
        Assert.Equal(10, page.Total);
        Assert.Equal(2, page.Skip);
        Assert.True(page.More);
    }

    // --- through the real pipeline -------------------------------------------

    [Fact]
    public async Task A_client_can_be_taken_on_through_the_api()
    {
        var browser = await KeyedAsync(Permissions.ClientsManage);

        var response = await browser.PostAsJsonAsync(
            "/api/v1/clients",
            new { name = "Acme Haulage", code = "acme-haulage" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The address of the thing made, so a caller need not guess a path.
        Assert.Contains("/api/v1/clients/", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// A rule refused through the API is refused by the same code as on a screen.
    /// </summary>
    /// <remarks>
    /// The test this file exists for. The service's own sentence comes back, because those
    /// sentences say what to do differently and a generic message throws that away.
    /// </remarks>
    [Fact]
    public async Task A_duplicate_client_is_refused_with_the_reason()
    {
        var browser = await KeyedAsync(Permissions.ClientsManage);

        await browser.PostAsJsonAsync(
            "/api/v1/clients", new { name = "Twice Limited", code = "twice" });

        var again = await browser.PostAsJsonAsync(
            "/api/v1/clients", new { name = "Twice Limited", code = "twice" });

        // 422, not 400. The request was understood and a rule said no, and telling the
        // caller 400 would send them looking at their JSON.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);

        var body = await again.Content.ReadAsStringAsync();

        Assert.Contains("refused", body);
        Assert.Contains("code", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A key without the permission cannot write, even though it can read.
    /// </summary>
    /// <remarks>
    /// Writes take the manage permissions and reads take the view ones, so a key issued for
    /// a reporting integration cannot create anything. Asserted because the API's scopes are
    /// chosen by whoever issues a key, and the failure would be silent until it was used.
    /// </remarks>
    [Fact]
    public async Task A_read_only_key_cannot_write()
    {
        var browser = await KeyedAsync(Permissions.ClientsView);

        var response = await browser.PostAsJsonAsync(
            "/api/v1/clients", new { name = "Not Allowed Limited", code = "nope" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A read comes back as a page, with a total beside it.
    /// </summary>
    [Fact]
    public async Task A_list_comes_back_as_a_page()
    {
        var browser = await KeyedAsync(Permissions.ClientsView, Permissions.ClientsManage);

        await browser.PostAsJsonAsync(
            "/api/v1/clients", new { name = "Paged Limited", code = "paged" });

        var body = await (await browser.GetAsync("/api/v1/clients?take=1")).Content
            .ReadAsStringAsync();

        Assert.Contains("\"items\"", body);
        Assert.Contains("\"total\"", body);
        Assert.Contains("\"take\":1", body);
    }

    /// <summary>
    /// Work raised through the API comes back with its number.
    /// </summary>
    /// <remarks>
    /// The number rather than only the identifier, because the number is what goes in a
    /// branch name and is the only thing that ties a commit back to the work. A caller
    /// raising work from its own tooling needs it in the same response.
    /// </remarks>
    [Fact]
    public async Task Work_raised_through_the_api_comes_back_with_its_number()
    {
        var browser = await KeyedAsync(Permissions.TasksCreate);

        var response = await browser.PostAsJsonAsync(
            "/api/v1/work", new { title = "Fit the second printer" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"number\"", body);
        Assert.Contains("\"reference\"", body);
        Assert.Contains("#", body);
    }

    /// <summary>
    /// A key with a scope it was issued for, talking to the real pipeline.
    /// </summary>
    /// <remarks>
    /// Issued through the same service the screen uses, so the scopes arrive as the same
    /// claims a person's role does — which is the arrangement that stops the API having its
    /// own idea of who may do what.
    /// </remarks>
    private async Task<HttpClient> KeyedAsync(params string[] scopes)
    {
        var secret = await factory.InRequestAsync(async services =>
        {
            var keys = services.GetRequiredService<ApiKeyService>();

            var issued = await keys.IssueAsync(
                $"Test key {Guid.CreateVersion7():N}", [.. scopes], createdById: null);

            return issued.Secret;
        });

        var browser = factory.CreateBrowser();

        browser.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        return browser;
    }
}
