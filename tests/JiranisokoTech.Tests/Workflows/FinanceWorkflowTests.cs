using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Persistence;
using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Roles = JiranisokoTech.Application.Authorization.Roles;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// The brief's finance workflow: invoice, send, payment, paid.
/// </summary>
/// <remarks>
/// Section 46. Every step through its page, by the role that now does it: sales takes the
/// client on, the accountant drafts the invoice and records what comes in, and the finance
/// manager sends it. Until the finance roles existed, only an administrator could do the
/// sending — so this chain is also the proof that they work, and that the separation between
/// them holds on the screen and not only in the role matrix: the accountant is not offered
/// the send button, and the finance manager is not offered the drafting form.
///
/// No test before this posted an invoice line, a send or a payment. The pieces had service
/// tests; the forms that carry them had none.
/// </remarks>
public static class FinanceWorkflow
{
    public static async Task WalkAsync(ApplicationFactory factory)
    {
        var tag = Guid.CreateVersion7().ToString("N")[^6..];

        var sales = await Browsing.SignedInAsync(factory, $"sales-{tag}@jiranisokotech.co.ke", Roles.Sales);
        var accountant = await Browsing.SignedInAsync(factory, $"accounts-{tag}@jiranisokotech.co.ke", Roles.Accountant);
        var finance = await Browsing.SignedInAsync(factory, $"finance-{tag}@jiranisokotech.co.ke", Roles.FinanceManager);

        // --- the client ------------------------------------------------------------------
        var clientName = $"Safari Logistics {tag}";

        Browsing.Accepted(await Browsing.PressAsync(sales, "/clients", "take-on",
            new Dictionary<string, string> { ["Input.Name"] = clientName }));

        var client = Guid.Empty;

        await factory.InScopeAsync(async services =>
            client = (await services.GetRequiredService<AppDbContext>().Clients.AsNoTracking()
                .SingleAsync(one => one.Name == clientName)).Id);

        Browsing.Accepted(await Browsing.PressAsync(sales, $"/clients/{client}", "status",
            new Dictionary<string, string> { ["State.Status"] = nameof(ClientStatus.Active) }));

        // --- the accountant drafts it; the finance manager cannot ------------------------
        Assert.DoesNotContain(
            "value=\"draft\"",
            await (await finance.GetAsync("/invoices")).Content.ReadAsStringAsync());

        var drafted = await Browsing.PressAsync(accountant, "/invoices", "draft", new Dictionary<string, string>
        {
            ["Input.ClientId"] = client.ToString(),
            ["Input.Currency"] = "KES",
        });

        Browsing.Accepted(drafted);

        var invoice = Guid.Parse(drafted.Headers.Location!.OriginalString.Split('/')[^1]);
        var path = $"/invoices/{invoice}";

        Browsing.Accepted(await Browsing.PressAsync(accountant, path, "line", new Dictionary<string, string>
        {
            ["Line.Description"] = "Route optimisation, September",
            ["Line.Quantity"] = "2",
            ["Line.UnitPrice"] = "1500",
        }));

        // --- the finance manager sends it; the accountant cannot -------------------------
        Assert.DoesNotContain(
            "value=\"send\"",
            await (await accountant.GetAsync(path)).Content.ReadAsStringAsync());

        Browsing.Accepted(await Browsing.PressAsync(finance, path, "send"));

        await factory.InScopeAsync(async services =>
        {
            var sent = await services.GetRequiredService<AppDbContext>().Invoices.AsNoTracking()
                .SingleAsync(one => one.Id == invoice);

            Assert.Equal(InvoiceStatus.Sent, sent.Status);
            Assert.Equal(3_000_00, sent.Total.MinorUnits);
        });

        // --- the money comes in, in two parts --------------------------------------------
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        Browsing.Accepted(await Browsing.PressAsync(accountant, path, "payment", new Dictionary<string, string>
        {
            ["Received.Amount"] = "1000",
            ["Received.On"] = today,
            ["Received.Reference"] = "MPESA QJK81H2",
        }));

        await factory.InScopeAsync(async services =>
        {
            var part = await services.GetRequiredService<AppDbContext>().Invoices.AsNoTracking()
                .SingleAsync(one => one.Id == invoice);

            Assert.Equal(InvoiceStatus.PartlyPaid, part.Status);
            Assert.Equal(2_000_00, part.Outstanding.MinorUnits);
        });

        Browsing.Accepted(await Browsing.PressAsync(accountant, path, "payment", new Dictionary<string, string>
        {
            ["Received.Amount"] = "2000",
            ["Received.On"] = today,
            ["Received.Reference"] = "MPESA QJK93L7",
        }));

        await factory.InScopeAsync(async services =>
        {
            var paid = await services.GetRequiredService<AppDbContext>().Invoices.AsNoTracking()
                .SingleAsync(one => one.Id == invoice);

            Assert.Equal(InvoiceStatus.Paid, paid.Status);
            Assert.Equal(0, paid.Outstanding.MinorUnits);
            Assert.Equal(2, paid.Payments.Count);
        });

        // Settled means nothing more can be recorded against it: the form is gone.
        Assert.DoesNotContain(
            "value=\"payment\"",
            await (await accountant.GetAsync(path)).Content.ReadAsStringAsync());
    }
}

public class FinanceWorkflowTests
{
    [Fact]
    public async Task From_an_invoice_to_paid()
    {
        using var factory = new WorkflowFactory();

        await FinanceWorkflow.WalkAsync(factory);
    }

    [PostgresFact]
    public async Task From_an_invoice_to_paid_on_postgres()
    {
        using var factory = new WorkflowPostgresFactory();

        await FinanceWorkflow.WalkAsync(factory);
    }
}
