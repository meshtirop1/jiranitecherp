using System.Net;
using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Web;

/// <summary>
/// An invoice can be raised in the currency the client pays in, and the lists survive it.
/// </summary>
/// <remarks>
/// <b>These exist because every invoice was in shillings.</b> Drafting took the firm's
/// currency with no way to choose another, so a client billed in dollars could not be invoiced
/// at all — the aggregate rightly refuses a dollar line on a shilling invoice.
///
/// Allowing a second currency exposed the next fault at once: the client list and the
/// invoice list each added every outstanding invoice together, starting from the first one's
/// currency, and <c>Money</c> refuses to add dollars to shillings. So the first dollar invoice
/// sent would have taken down both lists for everybody. The second test is that fault.
/// </remarks>
public class InvoiceCurrencyTests(ApplicationFactory factory) : IClassFixture<ApplicationFactory>
{
    private const string Password = "a-long-enough-password";

    [Fact]
    public async Task An_invoice_drafted_in_another_currency_is_in_that_currency()
    {
        var browser = await SignedInAsync("invoices-usd@jiranisokotech.co.ke");
        var client = await AClientAsync();

        var page = await browser.GetAsync("/invoices");

        var fields = HtmlForm.Fill(
            await page.Content.ReadAsStringAsync(),
            new Dictionary<string, string>
            {
                ["_handler"] = "draft",
                ["Input.ClientId"] = client.ToString(),
                ["Input.Currency"] = "usd",
            });

        var posted = await browser.PostAsync("/invoices", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Found, posted.StatusCode);

        await factory.InScopeAsync(async services =>
        {
            var invoice = await services.GetRequiredService<AppDbContext>().Invoices
                .AsNoTracking()
                .SingleAsync(one => one.ClientId == client);

            Assert.Equal("USD", invoice.Currency);
        });
    }

    [Fact]
    public async Task Leaving_the_currency_blank_drafts_in_the_firms_own()
    {
        var client = await AClientAsync();

        await factory.InScopeAsync(async services =>
        {
            var invoice = await services.GetRequiredService<InvoiceService>().DraftAsync(client);

            Assert.Equal("KES", invoice.Currency);
        });
    }

    /// <summary>
    /// A client owing in two currencies is shown owing both, and nothing falls over.
    /// </summary>
    [Fact]
    public async Task A_client_owing_in_two_currencies_is_shown_owing_both()
    {
        var browser = await SignedInAsync("invoices-mixed@jiranisokotech.co.ke");
        var client = await AClientAsync();

        await factory.InScopeAsync(async services =>
        {
            var invoices = services.GetRequiredService<InvoiceService>();

            var shillings = await invoices.DraftAsync(client);
            await invoices.AddLineAsync(shillings.Id, "Statement module", 1, Money.Of(150_000_00, "KES"));
            await invoices.SendAsync(shillings.Id);

            var dollars = await invoices.DraftAsync(client, "USD");
            await invoices.AddLineAsync(dollars.Id, "Hosting, one year", 1, Money.Of(1_200_00, "USD"));
            await invoices.SendAsync(dollars.Id);

            var row = (await services.GetRequiredService<BusinessQueries>().ClientsAsync())
                .Single(one => one.Id == client);

            Assert.Equal(
                [Money.Of(150_000_00, "KES"), Money.Of(1_200_00, "USD")],
                row.Owed);
        });

        var invoicesPage = await browser.GetAsync("/invoices");
        var clientsPage = await browser.GetAsync("/clients");

        Assert.Equal(HttpStatusCode.OK, invoicesPage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clientsPage.StatusCode);
        Assert.Contains("USD", await clientsPage.Content.ReadAsStringAsync());
    }

    private async Task<Guid> AClientAsync()
    {
        using var scope = factory.Services.CreateScope();

        var clients = scope.ServiceProvider.GetRequiredService<ClientService>();
        var client = await clients.TakeOnAsync("Acme " + Guid.CreateVersion7().ToString("N")[^8..]);

        // Taken on as a prospect; only a client with work agreed is offered for invoicing.
        await clients.MoveToAsync(client.Id, JiranisokoTech.Domain.Clients.ClientStatus.Active);

        return client.Id;
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
}
