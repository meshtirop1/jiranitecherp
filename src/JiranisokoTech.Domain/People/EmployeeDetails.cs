using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>On what terms somebody is engaged.</summary>
public enum ContractType
{
    Permanent = 1,

    /// <summary>A contract with an end date written into it.</summary>
    FixedTerm = 2,

    /// <summary>Not an employee. Invoices rather than draws a salary.</summary>
    /// <remarks>
    /// Kept distinct because the difference has consequences the system has to
    /// respect: a contractor has no leave entitlement, no probation and no payroll
    /// deductions, and treating one as an employee produces a statutory obligation
    /// the firm does not have and a deduction it should not make.
    /// </remarks>
    Contractor = 3,

    Intern = 4,
}

public enum PayFrequency
{
    Monthly = 1,
    Fortnightly = 2,
    Weekly = 3,

    /// <summary>Paid against an invoice rather than on a cycle.</summary>
    OnInvoice = 4,
}

/// <summary>Where somebody usually works.</summary>
public enum WorkLocation
{
    Office = 1,
    Remote = 2,
    Hybrid = 3,
}

/// <summary>
/// How to reach somebody, and where they are.
/// </summary>
/// <remarks>
/// An owned value object rather than a dozen more columns on Employee, because these
/// belong together and are read and written together — and because it gives one place
/// to say what is sensitive here.
///
/// The national identity and tax numbers are the reason this file is careful. They are
/// needed: a Kenyan firm cannot run payroll or file a return without them. They are
/// also the two fields in this system whose disclosure does a person lasting harm, so
/// they are excluded from the audit trail, shown masked, and behind a narrower
/// permission than the rest of a staff record.
/// </remarks>
public sealed record PersonalDetails
{
    public static PersonalDetails Empty { get; } = new();

    public string? Phone { get; init; }

    /// <summary>
    /// A private address, distinct from the work one.
    /// </summary>
    /// <remarks>
    /// Needed because the work address stops working on somebody's last day, and a
    /// final payslip or a reference request has to reach them after that.
    /// </remarks>
    public string? PersonalEmail { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    /// <summary>The national identity number. See the remarks on this type.</summary>
    public string? NationalId { get; init; }

    /// <summary>The tax number — KRA PIN in Kenya.</summary>
    public string? TaxNumber { get; init; }

    public string? Address { get; init; }

    public WorkLocation? Location { get; init; }

    /// <summary>
    /// An IANA time zone, such as Africa/Nairobi.
    /// </summary>
    /// <remarks>
    /// IANA rather than an offset, because an offset is wrong twice a year anywhere
    /// that observes daylight saving and this firm will eventually hire somebody who
    /// lives in such a place. Stored as the identifier rather than resolved, so a
    /// tzdata update corrects history rather than leaving it frozen.
    /// </remarks>
    public string? TimeZone { get; init; }

    /// <summary>
    /// The last four characters, which is all any screen shows.
    /// </summary>
    /// <remarks>
    /// Enough for somebody holding the paper copy to confirm it is the same number,
    /// and not enough to be worth stealing from a screenshot.
    /// </remarks>
    public string? MaskedNationalId => Mask(NationalId);

    public string? MaskedTaxNumber => Mask(TaxNumber);

    public bool IsEmpty =>
        Phone is null && PersonalEmail is null && DateOfBirth is null
        && NationalId is null && TaxNumber is null && Address is null
        && Location is null && TimeZone is null;

    private static string? Mask(string? value) => value switch
    {
        null or "" => null,
        { Length: <= 4 } => new string('•', value.Length),
        _ => new string('•', value.Length - 4) + value[^4..],
    };
}

/// <summary>
/// Who to call if something happens at work.
/// </summary>
/// <remarks>
/// The one piece of personal data whose absence is an actual risk rather than an
/// inconvenience, which is why it is its own object rather than three more optional
/// fields lost among twenty others.
/// </remarks>
public sealed record EmergencyContact
{
    public static EmergencyContact Empty { get; } = new();

    public string? Name { get; init; }

    /// <summary>Spouse, parent, friend. Free text, because families are not an enum.</summary>
    public string? Relationship { get; init; }

    public string? Phone { get; init; }

    public bool IsEmpty => Name is null && Phone is null;

    /// <summary>
    /// A contact with a name and no number is not a contact.
    /// </summary>
    /// <remarks>
    /// Worth distinguishing from empty on a screen: "nobody recorded" is a gap
    /// somebody should fill, and "a name with no way to reach them" is a gap that
    /// looks filled and is not.
    /// </remarks>
    public bool IsReachable => !string.IsNullOrWhiteSpace(Phone);
}

/// <summary>
/// What somebody is paid, and on what terms.
/// </summary>
/// <remarks>
/// Behind its own permission everywhere it appears. `employees.view` is the permission
/// to see that somebody works here and which department they are in; it is not the
/// permission to see what they earn, and a system where those are the same one is a
/// system where anybody who can open the staff list knows everybody's salary.
/// </remarks>
public sealed record EmploymentTerms
{
    public static EmploymentTerms Empty { get; } = new();

    public ContractType? Contract { get; init; }

    /// <summary>
    /// The gross salary for one pay period, as a count of minor units.
    /// </summary>
    /// <remarks>
    /// Stored as a number beside its currency rather than as a Money, matching how a
    /// contract's value and a claim's amount are stored: a report can sum the column,
    /// and Money is what the code works with.
    ///
    /// Per period rather than annualised, because the period is what a payslip is for
    /// and an annual figure has to be divided by something before it is useful —
    /// whoever divides it has to know whether the year holds twelve months or
    /// twenty-six fortnights.
    /// </remarks>
    public long? SalaryMinorUnits { get; init; }

    public string? SalaryCurrency { get; init; }

    /// <summary>The salary as money, when both halves are present.</summary>
    public Common.Money? Salary =>
        SalaryMinorUnits is { } minor && SalaryCurrency is { Length: 3 } currency
            ? Common.Money.Of(minor, currency)
            : null;

    public PayFrequency? Frequency { get; init; }

    /// <summary>How much notice either side must give, in days.</summary>
    public int? NoticeDays { get; init; }

    public DateOnly? ProbationEndsOn { get; init; }

    /// <summary>The end date on a fixed-term contract.</summary>
    public DateOnly? EndsOn { get; init; }

    public bool IsEmpty =>
        Contract is null && SalaryMinorUnits is null && Frequency is null;

    /// <summary>
    /// Is the probation period over, on a given day?
    /// </summary>
    /// <remarks>
    /// Nothing acts on this automatically. A probation that ends is a conversation
    /// somebody has to have, and a system that quietly confirmed an appointment
    /// because a date passed would be making that decision on the firm's behalf.
    /// </remarks>
    public bool IsOnProbation(DateOnly on) =>
        ProbationEndsOn is { } ends && on <= ends;

    /// <summary>Has a fixed term run out?</summary>
    public bool HasExpired(DateOnly on) => EndsOn is { } ends && on > ends;
}

/// <summary>Something somebody is good at.</summary>
public sealed class Skill
{
    private Skill() => Name = string.Empty;

    internal static Skill Of(string name, SkillLevel level) =>
        new() { Id = Guid.CreateVersion7(), Name = name.Trim(), Level = level };

    public Guid Id { get; private init; }

    public string Name { get; private init; }

    public SkillLevel Level { get; private init; }
}

/// <summary>
/// How well.
/// </summary>
/// <remarks>
/// Four levels and no numbers. A one-to-ten scale invites an average, and an average
/// of somebody's skills is not a fact about anything.
/// </remarks>
public enum SkillLevel
{
    Learning = 1,
    Working = 2,
    Strong = 3,

    /// <summary>The person others ask.</summary>
    Leading = 4,
}

/// <summary>
/// A qualification somebody holds, and when it stops being true.
/// </summary>
/// <remarks>
/// The expiry is the reason this is stored at all. A certification nobody tracks
/// lapses quietly, and the moment it matters is the moment a client asks for evidence
/// of one during a tender.
/// </remarks>
public sealed class Certification
{
    private Certification()
    {
        Name = string.Empty;
        Issuer = string.Empty;
    }

    internal static Certification Of(
        string name, string issuer, DateOnly? issuedOn, DateOnly? expiresOn) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Issuer = issuer.Trim(),
            IssuedOn = issuedOn,
            ExpiresOn = expiresOn,
        };

    public Guid Id { get; private init; }

    public string Name { get; private init; }

    public string Issuer { get; private init; }

    public DateOnly? IssuedOn { get; private init; }

    public DateOnly? ExpiresOn { get; private init; }

    public bool HasLapsed(DateOnly on) => ExpiresOn is { } expires && on > expires;

    /// <summary>Within two months of lapsing, which is enough time to renew one.</summary>
    public bool LapsesSoon(DateOnly on) =>
        ExpiresOn is { } expires && !HasLapsed(on) && on >= expires.AddMonths(-2);
}
