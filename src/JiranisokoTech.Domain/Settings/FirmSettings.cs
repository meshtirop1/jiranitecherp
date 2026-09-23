using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Settings;

/// <summary>
/// The firm's own details, and the few choices that apply to everything.
/// </summary>
/// <remarks>
/// One row, and that is the point rather than a limitation. This system is for
/// Jiranisoko Tech Solutions and no one else, so "which firm's settings" is not
/// a question that can be asked — and a settings table that could hold two rows
/// invites code that asks it, then code that picks the first row, then a bug
/// that picks the wrong one.
///
/// Most of what is here goes on an invoice. A tax invoice in Kenya carries the
/// issuer's name, address and KRA PIN, and an invoice missing them is one the
/// client's accounts department sends back. They live here rather than in
/// configuration because they change by somebody deciding, not by somebody
/// deploying, and because a change to them should leave an audit entry like any
/// other.
/// </remarks>
public sealed class FirmSettings : Entity, IAuditable
{
    /// <summary>
    /// The one row's identifier, fixed rather than generated.
    /// </summary>
    /// <remarks>
    /// A known constant means the row can be read without a query that sorts or
    /// takes the first of something, and means a second row cannot be inserted
    /// by accident — the primary key refuses it.
    /// </remarks>
    public static readonly Guid TheOnlyOne = new("00000000-0000-0000-0000-00000000f19a");

    private FirmSettings()
    {
        TradingName = string.Empty;
        LegalName = string.Empty;
        InvoicePrefix = string.Empty;
        Currency = string.Empty;
    }

    private FirmSettings(string tradingName, string legalName)
        : base(TheOnlyOne)
    {
        TradingName = Required(tradingName, nameof(tradingName));
        LegalName = Required(legalName, nameof(legalName));
        InvoicePrefix = "JTS";
        Currency = "KES";
        PaymentTermDays = 30;
    }

    /// <summary>
    /// The settings as they start out, before anybody has opened the page.
    /// </summary>
    /// <remarks>
    /// Created with working defaults rather than blanks so that an invoice can
    /// be raised on day one. Everything that would be wrong to guess — the PIN,
    /// the address, the bank details — is left empty and the page says what is
    /// missing, because a plausible-looking wrong PIN on an invoice is worse
    /// than an obviously absent one.
    /// </remarks>
    public static FirmSettings Initial() =>
        new("Jiranisoko Tech Solutions", "Jiranisoko Tech Solutions Limited");

    public string TradingName { get; private set; }

    /// <summary>The name on the certificate of incorporation.</summary>
    public string LegalName { get; private set; }

    /// <summary>The KRA PIN, which belongs on every tax invoice.</summary>
    public string? TaxPin { get; private set; }

    public string? AddressLine { get; private set; }

    public string? Town { get; private set; }

    public string? PostalCode { get; private set; }

    public string Country { get; private set; } = "Kenya";

    public string? Telephone { get; private set; }

    public string? Email { get; private set; }

    public string? Website { get; private set; }

    /// <summary>How a client is told to pay.</summary>
    /// <remarks>
    /// Free text, because it is bank details for some clients, a paybill number
    /// for others, and both for most. Structuring it would mean deciding now
    /// which payment methods this firm will ever accept.
    /// </remarks>
    public string? PaymentInstructions { get; private set; }

    /// <summary>What every invoice number starts with.</summary>
    public string InvoicePrefix { get; private set; }

    /// <summary>The currency the firm invoices in.</summary>
    public string Currency { get; private set; }

    /// <summary>The terms a new client starts on.</summary>
    public int PaymentTermDays { get; private set; } = 30;

    /// <summary>
    /// What an hour of anybody's time costs the firm, in minor units of
    /// <see cref="Currency"/>.
    /// </summary>
    /// <remarks>
    /// One blended rate for everybody, and that is a deliberate refusal to do the more
    /// obvious thing. The system now knows what each person is paid, so a project's cost
    /// could be computed from the actual salary of whoever logged the hours — and that
    /// would be more accurate and would also publish everybody's pay.
    ///
    /// A delivery manager holds projects.manage and time.view_all and not employees.pay.
    /// Show them a per-project cost derived from individual salaries and they can
    /// recover any one person's rate by dividing: one project, one person, one month.
    /// The permission would be intact and the information would be out.
    ///
    /// So project costing uses a standard rate, which is what firms use anyway for
    /// exactly this reason. It is less precise about one project and does not leak
    /// anything about one person.
    /// </remarks>
    public long? StandardCostPerHourMinorUnits { get; private set; }

    /// <summary>What an hour costs, when a rate has been set.</summary>
    public Common.Money? StandardCostPerHour => StandardCostPerHourMinorUnits is { } minor
        ? Common.Money.Of(minor, Currency)
        : null;

    /// <summary>
    /// Set what an hour of time costs the firm.
    /// </summary>
    /// <remarks>
    /// Salaries, national insurance, an allocation of rent and software — whatever the
    /// firm decides. Nothing here computes it, because the inputs are a management
    /// decision rather than a sum this system holds all the parts of.
    /// </remarks>
    public void CostAnHourAt(long? minorUnits)
    {
        if (minorUnits is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minorUnits), "An hour cannot cost less than nothing.");
        }

        StandardCostPerHourMinorUnits = minorUnits;
    }

    /// <summary>Whether enough is filled in for an invoice to be sent out.</summary>
    /// <remarks>
    /// Not enforced as a refusal, because a firm may genuinely want to draft
    /// invoices before its paperwork is in order, and blocking that would be a
    /// system telling its owner how to run their business. Said on the page
    /// instead, where it can be acted on.
    /// </remarks>
    public bool IsReadyToInvoice =>
        !string.IsNullOrWhiteSpace(TaxPin)
        && !string.IsNullOrWhiteSpace(AddressLine)
        && !string.IsNullOrWhiteSpace(PaymentInstructions);

    public void Identify(
        string tradingName,
        string legalName,
        string? taxPin,
        string? telephone,
        string? email,
        string? website)
    {
        TradingName = Required(tradingName, nameof(tradingName));
        LegalName = Required(legalName, nameof(legalName));
        TaxPin = Trimmed(taxPin);
        Telephone = Trimmed(telephone);
        Email = Trimmed(email);
        Website = Trimmed(website);
    }

    public void MoveTo(string? addressLine, string? town, string? postalCode, string? country)
    {
        AddressLine = Trimmed(addressLine);
        Town = Trimmed(town);
        PostalCode = Trimmed(postalCode);
        Country = Trimmed(country) ?? "Kenya";
    }

    public void ExplainPayment(string? instructions) => PaymentInstructions = Trimmed(instructions);

    /// <summary>
    /// Change what future invoice numbers start with.
    /// </summary>
    /// <remarks>
    /// Future ones only. Numbers already issued are on documents other people
    /// are holding, and a sequence that changes shape halfway through is one an
    /// accountant will ask about — which is fine, because they can be told the
    /// date it changed. Silently renumbering the old ones would not be.
    /// </remarks>
    public void NumberInvoicesFrom(string prefix)
    {
        var cleaned = Required(prefix, nameof(prefix)).ToUpperInvariant();

        if (cleaned.Length > 8 || !cleaned.All(char.IsLetterOrDigit))
        {
            // It is read down a telephone and typed into somebody else's
            // system. Punctuation in it produces two spellings of the same
            // invoice number, and the firm then has two invoices as far as the
            // client is concerned.
            throw new ArgumentException(
                "An invoice prefix is up to eight letters or digits, and nothing else. "
                + "It gets read aloud and typed into other people's systems.",
                nameof(prefix));
        }

        InvoicePrefix = cleaned;
    }

    /// <summary>
    /// Change the currency the firm invoices in.
    /// </summary>
    /// <remarks>
    /// Refused once any invoice exists, and this is the one hard rule on this
    /// page. Every invoice stores its own currency and its totals are summed
    /// from lines in that currency, so changing this does not touch them — it
    /// changes what the next one is raised in. That is exactly the problem: the
    /// firm would then hold invoices in two currencies with nothing saying why,
    /// every total across them would refuse to add, and the reporting page
    /// would stop showing a figure. If that is genuinely wanted it is a
    /// decision worth making deliberately, not by editing a field.
    /// </remarks>
    public void InvoiceIn(string currency, bool anyInvoicesExist)
    {
        var cleaned = Required(currency, nameof(currency)).ToUpperInvariant();

        if (cleaned.Length != 3 || !cleaned.All(char.IsAsciiLetter))
        {
            throw new ArgumentException(
                "A currency is its three-letter ISO code, such as KES.", nameof(currency));
        }

        if (cleaned == Currency)
        {
            return;
        }

        if (anyInvoicesExist)
        {
            throw new InvalidOperationException(
                $"This firm has already invoiced in {Currency}. Changing the currency now would "
                + "leave invoices in two currencies with nothing to say why, and no total across "
                + "them would add up.");
        }

        Currency = cleaned;
    }

    public void SettleWithin(int days)
    {
        if (days is < 0 or > 365)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days), days, "Payment terms run from zero days to a year.");
        }

        PaymentTermDays = days;
    }

    /// <summary>
    /// Nothing here is hidden from the trail.
    /// </summary>
    /// <remarks>
    /// Including the PIN. It is printed on every invoice the firm sends, so it
    /// is not a secret — and a change to it is precisely the kind of thing
    /// somebody needs to be able to look up afterwards.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
