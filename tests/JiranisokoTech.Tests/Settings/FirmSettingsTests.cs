using JiranisokoTech.Application.Business;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Domain.Settings;
using JiranisokoTech.Infrastructure.Business;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Settings;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Tests.Settings;

/// <summary>
/// The firm's own details, and the two of them that change what an invoice
/// looks like.
/// </summary>
public class FirmSettingsTests
{
    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        public SettingsService Settings => new(new SettingsRepository(_context));

        public ClientService Clients => new(new BusinessRepository(_context));

        public InvoiceService Invoices =>
            new(new BusinessRepository(_context), Settings, db.Clock);

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    /// <summary>
    /// There is one settings row, and asking twice does not make two.
    /// </summary>
    /// <remarks>
    /// The row is created on first use rather than by a seeder, so the obvious
    /// way to get this wrong is to create one per call. The key is a constant,
    /// which means the second insert would not merely duplicate — it would
    /// throw, in whatever unrelated request happened to be second.
    /// </remarks>
    [Fact]
    public async Task The_settings_are_created_once_and_only_once()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var first = await module.Settings.CurrentAsync();
        var second = await module.Settings.CurrentAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(FirmSettings.TheOnlyOne, first.Id);

        await using var read = db.NewContext();
        Assert.Equal(1, await read.Settings.CountAsync());
    }

    /// <summary>
    /// The invoice prefix is what invoices are actually numbered with.
    /// </summary>
    /// <remarks>
    /// It was a constant in two places before this — the service that builds the
    /// number and the query that finds the last one — so the test raises a real
    /// invoice rather than asserting the setting was stored.
    /// </remarks>
    [Fact]
    public async Task An_invoice_is_numbered_with_the_prefix_the_firm_set()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Settings.BillAsAsync("JTSL", "KES", 30);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        Assert.StartsWith($"JTSL-{db.Clock.Today.Year}-", invoice.Number);
    }

    /// <summary>
    /// Numbering continues within a prefix rather than across all of them.
    /// </summary>
    /// <remarks>
    /// A firm that changes its prefix has not issued fewer invoices, but it has
    /// started a new run. Continuing the old count under the new prefix would
    /// produce NEW-2026-0004 as the first invoice anybody ever saw with that
    /// prefix, which reads like three are missing.
    /// </remarks>
    [Fact]
    public async Task Changing_the_prefix_starts_a_fresh_sequence()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");

        var first = await module.Invoices.DraftAsync(client.Id);
        var second = await module.Invoices.DraftAsync(client.Id);

        Assert.EndsWith("-0001", first.Number);
        Assert.EndsWith("-0002", second.Number);

        await module.Settings.BillAsAsync("NEW", "KES", 30);

        var third = await module.Invoices.DraftAsync(client.Id);

        Assert.EndsWith("-0001", third.Number);
        Assert.StartsWith("NEW-", third.Number);
    }

    /// <summary>
    /// The currency cannot change once the firm has invoiced in one.
    /// </summary>
    [Fact]
    public async Task The_currency_is_settled_by_the_first_invoice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        // Before anything is invoiced it is simply a choice.
        await module.Settings.BillAsAsync("JTS", "USD", 30);
        Assert.Equal("USD", (await module.Settings.CurrentAsync()).Currency);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        Assert.Equal("USD", invoice.Currency);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Settings.BillAsAsync("JTS", "KES", 30));

        Assert.Contains("already invoiced in USD", refused.Message);
    }

    /// <summary>
    /// Saving the same currency back is not a change and is not refused.
    /// </summary>
    /// <remarks>
    /// The billing form saves all three fields together, so somebody changing
    /// only the payment terms posts the currency untouched. Treating that as an
    /// attempt to change it would make the terms uneditable for ever after the
    /// first invoice — a rule doing the opposite of its job.
    /// </remarks>
    [Fact]
    public async Task The_terms_can_still_be_changed_after_the_first_invoice()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        await module.Invoices.DraftAsync(client.Id);

        await module.Settings.BillAsAsync("JTS", "KES", 14);

        Assert.Equal(14, (await module.Settings.CurrentAsync()).PaymentTermDays);
    }

    [Theory]
    [InlineData("JTS LTD")]
    [InlineData("JTS-2")]
    [InlineData("TOOMANYCHARS")]
    public async Task A_prefix_that_cannot_be_read_aloud_is_refused(string prefix)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Settings.BillAsAsync(prefix, "KES", 30));
    }

    [Theory]
    [InlineData("SHILLING")]
    [InlineData("K")]
    [InlineData("K3S")]
    public async Task A_currency_that_is_not_a_three_letter_code_is_refused(string currency)
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Settings.BillAsAsync("JTS", currency, 30));
    }

    /// <summary>
    /// The firm is not ready to invoice until the paperwork is filled in.
    /// </summary>
    /// <remarks>
    /// Not a refusal — a firm may want to draft before its paperwork is in
    /// order — but the page says it, so the flag has to be right.
    /// </remarks>
    [Fact]
    public async Task Readiness_needs_the_pin_the_address_and_somewhere_to_send_the_money()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        Assert.False((await module.Settings.CurrentAsync()).IsReadyToInvoice);

        await module.Settings.IdentifyAsync(
            "Jiranisoko Tech Solutions",
            "Jiranisoko Tech Solutions Limited",
            "P051234567X",
            null,
            null,
            null);

        Assert.False((await module.Settings.CurrentAsync()).IsReadyToInvoice);

        await module.Settings.MoveToAsync("Kabarnet Road", "Nairobi", "00100", "Kenya");

        Assert.False((await module.Settings.CurrentAsync()).IsReadyToInvoice);

        await module.Settings.ExplainPaymentAsync("Paybill 000000, account JTS.");

        Assert.True((await module.Settings.CurrentAsync()).IsReadyToInvoice);
    }

    /// <summary>
    /// A settings change is in the audit trail like any other.
    /// </summary>
    [Fact]
    public async Task Changing_the_firm_details_is_recorded()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Settings.IdentifyAsync(
            "Jiranisoko Tech", "Jiranisoko Tech Solutions Limited", "P051234567X", null, null, null);

        await using var read = db.NewContext();

        var entries = await read.AuditEntries
            .Where(entry => entry.SubjectType == nameof(FirmSettings))
            .ToListAsync();

        Assert.NotEmpty(entries);
    }

    /// <summary>
    /// An invoice is raised in the currency the firm bills in, not a constant.
    /// </summary>
    [Fact]
    public async Task An_invoice_line_must_be_in_the_firms_currency()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        await using var module = new Module(db);

        await module.Settings.BillAsAsync("JTS", "USD", 30);

        var client = await module.Clients.TakeOnAsync("Acme Logistics");
        var invoice = await module.Invoices.DraftAsync(client.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Invoices.AddLineAsync(invoice.Id, "Work", 1, Money.Of(1000, "KES")));

        Assert.Contains("USD", refused.Message);
    }
}
