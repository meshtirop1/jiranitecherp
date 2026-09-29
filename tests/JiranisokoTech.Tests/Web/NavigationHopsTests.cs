using System.Net;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Engineering;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// A record's page links to the records it names.
/// </summary>
/// <remarks>
/// <b>Section 98 names a navigation and says the product fails if it cannot be walked:</b> employee
/// → team → project → task → repository → pull request → deployment → invoice → client. Its row in
/// the checklist says the walk broke at five hops, and every one of them was the same shape — the
/// page had the identifier and rendered the name as plain text.
///
/// A work item named its project and its assignee and linked to neither. An invoice named the
/// client it bills and linked nowhere. A project's page linked its client and had no route to its
/// repositories or to what had been charged for the work, although <c>Repository.ProjectId</c> and
/// <c>Invoice.BillsFor</c> have both existed for sections. Each hop could be made by going back to
/// a list and searching, which is exactly the losing of context the section is about: it is not
/// that the information is missing, it is that following it costs a search every time.
///
/// Asserted on the href rather than on the words, because the words are what was already there.
/// </remarks>
public class NavigationHopsTests(NavigationHopsTests.WithASecret factory)
    : IClassFixture<NavigationHopsTests.WithASecret>
{
    private const string Password = "a-long-enough-password";

    private const string Secret = "the-secret-the-hops-repository-holds";

    /// <summary>
    /// A work item links to its project and to whoever is doing it.
    /// </summary>
    /// <remarks>
    /// Two of the section's hops from one page, and the busiest: a board is read by opening a card,
    /// and the next question is always either "what project is this" or "who has it".
    /// </remarks>
    [Fact]
    public async Task A_work_item_links_to_its_project_and_its_assignee()
    {
        var (browser, chain) = await AChainAsync("hops-item");

        var html = await Read(browser, $"/work/{chain.Item}");

        Assert.Contains($"href=\"/projects/{chain.Project}\"", html);
        Assert.Contains($"href=\"/people/{chain.Assignee}\"", html);
    }

    /// <summary>
    /// A project links to its repositories and to what has been billed against it.
    /// </summary>
    /// <remarks>
    /// The two hops that needed a query as well as a link. Nothing could ask which repositories
    /// belong to a project or which invoices bill it, though both keys were stored — so these
    /// assertions would have failed for a reason no amount of markup could fix.
    /// </remarks>
    [Fact]
    public async Task A_project_links_to_its_repositories_and_its_invoices()
    {
        var (browser, chain) = await AChainAsync("hops-project");

        var html = await Read(browser, $"/projects/{chain.Project}");

        Assert.Contains($"href=\"/clients/{chain.Client}\"", html);
        Assert.Contains(chain.Repository, html);
        Assert.Contains($"href=\"/invoices/{chain.Invoice}\"", html);
    }

    /// <summary>
    /// An invoice links to the client it bills.
    /// </summary>
    /// <remarks>
    /// The last hop of the section's chain, and the one somebody makes most often — an invoice is
    /// queried, and the next question is about the client.
    /// </remarks>
    [Fact]
    public async Task An_invoice_links_to_its_client()
    {
        var (browser, chain) = await AChainAsync("hops-invoice");

        var html = await Read(browser, $"/invoices/{chain.Invoice}");

        Assert.Contains($"href=\"/clients/{chain.Client}\"", html);
    }

    /// <summary>
    /// A project with nothing attached says so rather than showing an empty heading.
    /// </summary>
    /// <remarks>
    /// The state every one of these sections is in on the day a project starts, and the one a
    /// screen most often gets wrong — a heading with nothing under it reads as a page that failed
    /// to load. Both say what would put something there, because a reader who has just found an
    /// empty list wants to know how to fill it.
    /// </remarks>
    [Fact]
    public async Task A_project_with_no_repository_or_invoice_says_what_would_put_one_there()
    {
        var browser = await SignedInAsync("hops-empty@jiranisokotech.co.ke");

        var project = await factory.InRequestAsync(services =>
            services.GetRequiredService<WorkService>()
                .BeginProjectAsync("Nothing attached " + Suffix()));

        var html = await Read(browser, $"/projects/{project.Id}");

        Assert.Contains("No repository is filed under this project", html);
        Assert.Contains("Nothing has been invoiced against this project", html);
    }

    private static async Task<string> Read(HttpClient browser, string path)
    {
        var page = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    private sealed record Chain(
        Guid Client, Guid Project, Guid Item, Guid Assignee, Guid Invoice, string Repository);

    /// <summary>
    /// One of each record in the section's chain, joined the way the section expects.
    /// </summary>
    /// <remarks>
    /// Built through the services rather than the context, because the links being asserted are
    /// the ones the services set — <c>ForClientAsync</c>, <c>BillsFor</c>, <c>ConnectAsync</c>'s
    /// project — and a fixture that wrote the keys directly would pass while the services had
    /// stopped writing them.
    /// </remarks>
    private async Task<(HttpClient Browser, Chain Chain)> AChainAsync(string who)
    {
        var browser = await SignedInAsync($"{who}@jiranisokotech.co.ke");
        var tag = Suffix();

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var client = await services.GetRequiredService<ClientService>()
            .TakeOnAsync("Hops " + tag, "hops-" + tag);

        var project = await services.GetRequiredService<WorkService>()
            .BeginProjectAsync("Hops " + tag, "hops-" + tag);

        var work = services.GetRequiredService<WorkService>();

        await work.ForClientAsync(project.Id, client.Id);
        await work.ActivateProjectAsync(project.Id);

        var people = services.GetRequiredService<PeopleService>();

        var assignee = await people.HireAsync(
            "Hopper " + tag, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));

        await people.StartAsync(assignee.Id);

        var item = await work.RaiseAsync(
            "Something on the chain " + tag, assignee.Id, project.Id, assignee.Id);

        var name = "hops-" + tag;

        await services.GetRequiredService<EngineeringService>()
            .ConnectAsync(GitProvider.GitHub, "jiranisokotech", name, Secret, project.Id);

        var invoices = services.GetRequiredService<InvoiceService>();

        var invoice = await invoices.DraftAsync(client.Id, "KES");

        await invoices.BillForAsync(invoice.Id, project.Id);
        await invoices.AddLineAsync(invoice.Id, "The work", 1, Money.Of(100_00L, "KES"));

        return (browser, new Chain(
            client.Id, project.Id, item.Id, assignee.Id, invoice.Id, $"jiranisokotech/{name}"));
    }

    /// <remarks>
    /// The tail of the identifier, because a version 7 identifier starts with the time and two made
    /// in the same millisecond share their first characters.
    /// </remarks>
    private static string Suffix() => Guid.CreateVersion7().ToString("N")[^8..];

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
    /// The application with a webhook secret, because the chain has a repository on it.
    /// </summary>
    /// <remarks>
    /// Connecting one refuses a secret that does not match the configured value, which is the right
    /// rule — a repository connected with the wrong one is deaf to every delivery.
    /// </remarks>
    public sealed class WithASecret : ApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Git:Providers:GitHub:Secret", Secret);
            builder.UseSetting("Git:PollInterval", "01:00:00");
            builder.UseSetting("Outbox:PollInterval", "01:00:00");
        }
    }
}
