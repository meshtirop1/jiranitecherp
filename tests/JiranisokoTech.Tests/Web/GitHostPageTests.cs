using System.Net;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A repository can be connected on any of the four hosts the application has adapters for.
/// </summary>
/// <remarks>
/// <b>These exist because three of the four could not be.</b>
///
/// GitLab, Bitbucket and Azure DevOps have had adapters since section 39 — each with its own
/// signature scheme and its own payload parser, each registered in the container, each tested
/// against real payload shapes — and the connect form on <c>/repositories</c> posted
/// <c>GitProvider.GitHub</c> as a literal, with no control anywhere near it. So there was no way
/// from anywhere in the application to watch a repository on any host but one.
///
/// Section 51 asks that new providers can be added without changing core business logic. That was
/// true of the business logic and false of the firm: the work was finished, tested, marked done,
/// and nobody could reach it. It is the fault <c>ReachabilityTests</c> was written for a level up —
/// a whole provider rather than a service method — and nothing could see it, because every test of
/// those adapters called the adapter.
///
/// Its own class rather than a case in <c>RepositoryPageTests</c>, because connecting a repository
/// needs the host's webhook secret configured and that class deliberately runs against the plain
/// factory. Setting a secret for four hosts there would change what every other test in it is
/// running against.
/// </remarks>
public class GitHostPageTests(GitHostPageTests.WithEverySecret factory)
    : IClassFixture<GitHostPageTests.WithEverySecret>
{
    private const string Password = "a-long-enough-password";

    /// <summary>The secret every host is configured with, so one value is typed on the form.</summary>
    private const string Secret = "the-secret-every-host-is-configured-with";

    /// <summary>
    /// Each of the four hosts can be chosen, and the repository is stored against it.
    /// </summary>
    /// <remarks>
    /// All four rather than one, because the fault was a literal: a test for a single host that
    /// happened to be GitHub would have passed the entire time the form could reach nothing else,
    /// which is the same trap <c>PerRowFormPostTests</c> records about an opportunity's stage.
    ///
    /// The select's value is posted by name because <c>HtmlForm.Fill</c> reads inputs and not
    /// selects — the gap that hid thirteen broken selects until <c>BlankChoices</c> was written.
    /// </remarks>
    [Theory]
    [InlineData(GitProvider.GitHub)]
    [InlineData(GitProvider.GitLab)]
    [InlineData(GitProvider.Bitbucket)]
    [InlineData(GitProvider.AzureDevOps)]
    public async Task A_repository_can_be_connected_on_any_host_the_application_knows(
        GitProvider host)
    {
        var browser = await SignedInAsync($"host-{host}@jiranisokotech.co.ke");
        var name = $"{host}-{Guid.CreateVersion7().ToString("N")[^8..]}".ToLowerInvariant();

        var posted = await ConnectAsync(browser, host.ToString(), name);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        using var scope = factory.Services.CreateScope();

        /*
         * By owner and name rather than by FullName, which is computed and builder.Ignore'd — a
         * query on it is refused at run time with "translation of member 'FullName' failed".
         */
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories
            .AsNoTracking()
            .SingleAsync(one => one.Owner == "jiranisokotech" && one.Name == name);

        Assert.Equal(host, stored.Provider);
        Assert.True(stored.IsWatched);
    }

    /// <summary>
    /// The page offers all four by name.
    /// </summary>
    /// <remarks>
    /// Asserted on the words rather than on the enum names, because "AzureDevOps" is not what
    /// anybody calls it and the select spells it out.
    /// </remarks>
    [Fact]
    public async Task The_form_offers_every_host_by_the_name_people_use()
    {
        var browser = await SignedInAsync("hosts-list@jiranisokotech.co.ke");

        var html = await (await browser.GetAsync("/repositories")).Content.ReadAsStringAsync();

        Assert.Contains("GitHub", html);
        Assert.Contains("GitLab", html);
        Assert.Contains("Bitbucket", html);
        Assert.Contains("Azure DevOps", html);
    }

    /// <summary>
    /// A host nothing knows about is refused in words somebody can act on.
    /// </summary>
    /// <remarks>
    /// Zero is reachable and is no member of the enum: a non-nullable enum bound from a form comes
    /// back as the underlying zero when nothing is posted for it, which is the same hazard the
    /// empty option has for a nullable value type. Before the service refused it, the next thing
    /// it did was look up "Git:Providers:0:Secret" and put that in the refusal — which told
    /// whoever crafted the request the shape of this application's configuration keys and told
    /// somebody who had simply missed the control nothing at all.
    /// </remarks>
    [Fact]
    public async Task A_host_the_application_does_not_know_is_refused_plainly()
    {
        var browser = await SignedInAsync("hosts-unknown@jiranisokotech.co.ke");

        var posted = await ConnectAsync(browser, "0", "nowhere-in-particular");

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var said = await posted.Content.ReadAsStringAsync();

        Assert.Contains("not a host this application knows", said);
        Assert.DoesNotContain("Git:Providers:0", said);

        using var scope = factory.Services.CreateScope();

        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Repositories
            .AnyAsync(one => one.Name == "nowhere-in-particular"));
    }

    private static async Task<HttpResponseMessage> ConnectAsync(
        HttpClient browser, string host, string name)
    {
        var page = await browser.GetAsync("/repositories");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "connect",
                ["Input.Provider"] = host,
                ["Input.Owner"] = "jiranisokotech",
                ["Input.Name"] = name,
                ["Input.Secret"] = Secret,
                ["Input.ProjectId"] = string.Empty,
            });

        return await browser.PostAsync("/repositories", new FormUrlEncodedContent(fields));
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            if (await users.FindByEmailAsync(email) is null)
            {
                await factory.CreateAccountAsync(email, Password, email);
            }

            var stored = await users.FindByEmailAsync(email);

            if (!await users.IsInRoleAsync(stored!, Roles.Administrator))
            {
                await users.AddToRoleAsync(stored!, Roles.Administrator);
            }
        }

        var browser = factory.CreateBrowser();

        var form = await browser.GetAsync("/sign-in");

        await browser.PostAsync("/sign-in", new FormUrlEncodedContent(HtmlForm.Fill(
            await form.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = Password,
            })));

        return browser;
    }

    /// <summary>
    /// The application with a webhook secret configured for every host.
    /// </summary>
    /// <remarks>
    /// Connecting a repository refuses unless the secret typed matches the one this application is
    /// configured with for that host, which is the right rule — a repository connected with the
    /// wrong secret is one whose every delivery is refused, and the connect form is the last moment
    /// anybody would notice. So all four are set here, to the same value, and the form types it.
    /// </remarks>
    public sealed class WithEverySecret : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            foreach (var host in Enum.GetValues<GitProvider>())
            {
                builder.UseSetting($"Git:Providers:{host}:Secret", Secret);
            }

            // Nothing here needs the pollers, and a loop racing these tests for the same rows
            // is the one kind of test worse than none.
            builder.UseSetting("Git:PollInterval", "01:00:00");
            builder.UseSetting("Outbox:PollInterval", "01:00:00");
        }
    }
}
