using JiranisokoTech.Domain.Settings;

namespace JiranisokoTech.Application.Settings;

public interface ISettingsRepository
{
    Task<FirmSettings?> FindAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyInvoicesAsync(CancellationToken cancellationToken = default);

    void Add(FirmSettings settings);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reading and changing the firm's own details.
/// </summary>
/// <remarks>
/// Every caller gets the same row. <see cref="CurrentAsync"/> creates it on
/// first use rather than expecting a seeder to have run, because the settings
/// are needed by the first invoice anybody raises — and a system that fails
/// until somebody remembers to seed it is a system that fails.
/// </remarks>
public sealed class SettingsService(ISettingsRepository settings)
{
    public async Task<FirmSettings> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (await settings.FindAsync(cancellationToken) is { } stored)
        {
            return stored;
        }

        var fresh = FirmSettings.Initial();

        settings.Add(fresh);
        await settings.SaveAsync(cancellationToken);

        return fresh;
    }

    /// <summary>
    /// Whether the firm has issued any invoice at all.
    /// </summary>
    /// <remarks>
    /// Which is the same question as "can the currency still be changed". The
    /// page asks it to say so before somebody types a new one, rather than
    /// letting them fill the field in and then be refused.
    /// </remarks>
    public Task<bool> HasInvoicedAsync(CancellationToken cancellationToken = default) =>
        settings.AnyInvoicesAsync(cancellationToken);

    public async Task IdentifyAsync(
        string tradingName,
        string legalName,
        string? taxPin,
        string? telephone,
        string? email,
        string? website,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken);

        current.Identify(tradingName, legalName, taxPin, telephone, email, website);
        await settings.SaveAsync(cancellationToken);
    }

    public async Task MoveToAsync(
        string? addressLine,
        string? town,
        string? postalCode,
        string? country,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken);

        current.MoveTo(addressLine, town, postalCode, country);
        await settings.SaveAsync(cancellationToken);
    }

    public async Task ExplainPaymentAsync(
        string? instructions, CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken);

        current.ExplainPayment(instructions);
        await settings.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The billing choices: numbering, currency and terms.
    /// </summary>
    /// <remarks>
    /// Whether any invoice exists is read here and handed to the aggregate
    /// rather than the aggregate reaching for it. The rule — that the currency
    /// cannot change once the firm has invoiced in one — belongs in the domain;
    /// knowing whether that is the case is a question for the database, and the
    /// domain has no business asking it.
    /// </remarks>
    public async Task BillAsAsync(
        string invoicePrefix,
        string currency,
        int paymentTermDays,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken);
        var invoiced = await settings.AnyInvoicesAsync(cancellationToken);

        current.NumberInvoicesFrom(invoicePrefix);
        current.InvoiceIn(currency, invoiced);
        current.SettleWithin(paymentTermDays);

        await settings.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Set what an hour of anybody's time costs the firm.
    /// </summary>
    /// <remarks>
    /// One blended rate rather than each person's actual pay, and the reason is a
    /// permission boundary. A delivery manager holds projects.manage and time.view_all
    /// and not employees.pay; a project cost derived from real salaries would let them
    /// recover any one person's rate by dividing — one project, one person, one month —
    /// with the permission intact and the information out.
    ///
    /// Nothing computes this. Salaries, statutory contributions, an allocation of rent
    /// and software are a management decision rather than a sum this system holds all
    /// the parts of.
    /// </remarks>
    public async Task CostAnHourAtAsync(
        long? minorUnits, CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken);

        current.CostAnHourAt(minorUnits);

        await settings.SaveAsync(cancellationToken);
    }
}
