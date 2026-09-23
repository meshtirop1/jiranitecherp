using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;

namespace JiranisokoTech.Web;

/// <summary>
/// What the business states are called on screen.
/// </summary>
/// <remarks>
/// One place, because the same handful of enums appear across the time, leave,
/// expense, client, contract and invoice screens, and a switch copied onto each
/// of them drifts: one
/// page ends up saying "Awaiting approval" while the next says "Pending", and
/// people reasonably conclude they are different things.
///
/// The words are what somebody in the office would say, not the identifier.
/// <c>PartlyPaid</c> is a good enum name and a bad label.
/// </remarks>
public static class Words
{
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

    /// <summary>
    /// What a contract's state is called on screen.
    /// </summary>
    /// <remarks>
    /// There is no word here for expired, because there is no state for it. A
    /// contract past its end date is still recorded as active and is shown as
    /// expired beside that, the same way an overdue invoice is shown as sent and
    /// overdue — the state is what somebody decided, and expiry is what the
    /// calendar has since done to it.
    /// </remarks>
    public static string For(ContractState state) => state switch
    {
        ContractState.Draft => "Being agreed",
        ContractState.Active => "In force",
        ContractState.Terminated => "Terminated",
        _ => state.ToString(),
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
