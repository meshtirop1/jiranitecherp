using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;

namespace JiranisokoTech.Web;

/// <summary>
/// What the business states are called on screen.
/// </summary>
/// <remarks>
/// One place, because the same six enums appear across the time, leave, expense,
/// client and invoice screens, and a switch copied onto each of them drifts: one
/// page ends up saying "Awaiting approval" while the next says "Pending", and
/// people reasonably conclude they are different things.
///
/// The words are what somebody in the office would say, not the identifier.
/// <c>PartlyPaid</c> is a good enum name and a bad label.
/// </remarks>
public static class Words
{
    /// <summary>A number of minutes as somebody would say it out loud.</summary>
    /// <remarks>
    /// Here rather than in the reporting page because the page and its CSV export
    /// have to agree character for character: the file is the screen, sent to
    /// somebody else. Two copies of this expression would differ the first time
    /// one of them was adjusted, and the report would be accused of being wrong
    /// by whoever was holding the other one.
    /// </remarks>
    public static string Hours(int minutes) => minutes % 60 == 0
        ? $"{minutes / 60}h"
        : $"{minutes / 60}h {minutes % 60}m";

    public static string For(LeaveKind kind) => kind switch
    {
        LeaveKind.Annual => "Annual leave",
        LeaveKind.Sick => "Sick leave",
        LeaveKind.Compassionate => "Compassionate leave",
        LeaveKind.Maternity => "Maternity leave",
        LeaveKind.Paternity => "Paternity leave",
        LeaveKind.Study => "Study leave",
        LeaveKind.Unpaid => "Unpaid leave",
        _ => kind.ToString(),
    };

    public static string For(LeaveStatus status) => status switch
    {
        LeaveStatus.Draft => "Not sent",
        LeaveStatus.AwaitingApproval => "Waiting",
        LeaveStatus.Approved => "Approved",
        LeaveStatus.Refused => "Refused",
        LeaveStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    public static string For(ClaimStatus status) => status switch
    {
        ClaimStatus.Draft => "Not sent",
        ClaimStatus.AwaitingApproval => "Waiting",
        ClaimStatus.Approved => "Approved, not yet paid",
        ClaimStatus.Refused => "Refused",
        ClaimStatus.Paid => "Paid",
        ClaimStatus.Withdrawn => "Withdrawn",
        _ => status.ToString(),
    };

    public static string For(ExpenseCategory category) => category switch
    {
        ExpenseCategory.Travel => "Travel",
        ExpenseCategory.Accommodation => "Accommodation",
        ExpenseCategory.Meals => "Meals",
        ExpenseCategory.Equipment => "Equipment",
        ExpenseCategory.Software => "Software",
        ExpenseCategory.Training => "Training",
        ExpenseCategory.Other => "Something else",
        _ => category.ToString(),
    };

    public static string For(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Draft => "Draft",
        InvoiceStatus.Sent => "Sent",
        InvoiceStatus.PartlyPaid => "Part paid",
        InvoiceStatus.Paid => "Paid",
        InvoiceStatus.Void => "Voided",
        _ => status.ToString(),
    };

    public static string For(ClientStatus status) => status switch
    {
        ClientStatus.Prospect => "Prospect",
        ClientStatus.Active => "Current",
        ClientStatus.Dormant => "Dormant",
        ClientStatus.Former => "Former",
        _ => status.ToString(),
    };
}
