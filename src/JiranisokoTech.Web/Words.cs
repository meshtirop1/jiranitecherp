using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Integrations;
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

    /// <summary>
    /// What became of a delivery, said plainly.
    /// </summary>
    /// <remarks>
    /// "Ignored" and "Dead lettered" are the two that need the plain words most.
    /// The first is a success that looks like a failure — a star or a fork,
    /// correctly read and correctly disregarded — and the second is a failure
    /// that has stopped announcing itself, which is the one somebody has to act
    /// on.
    /// </remarks>
    public static string For(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Received => "Waiting",
        DeliveryStatus.Handled => "Recorded",
        DeliveryStatus.Ignored => "Nothing for us",
        DeliveryStatus.Failed => "Failed, will retry",
        DeliveryStatus.DeadLettered => "Gave up",
        _ => status.ToString(),
    };

    /// <summary>What became of a notification this system sent.</summary>
    /// <remarks>
    /// "Gave up" rather than "dead lettered", because the person reading it has to
    /// decide whether to chase somebody at the far end, and the jargon does not help
    /// them do that.
    /// </remarks>
    public static string For(OutboundStatus status) => status switch
    {
        OutboundStatus.Waiting => "Queued",
        OutboundStatus.Sent => "Sent",
        OutboundStatus.Failed => "Failed, will retry",
        OutboundStatus.DeadLettered => "Gave up",
        _ => status.ToString(),
    };

    /// <summary>Where a pull request got to.</summary>
    /// <remarks>
    /// "Closed" is spelt out as closed without merging, because the difference
    /// between that and merged is the difference between work that shipped and
    /// work that was abandoned — and a one-word label lets a reader assume the
    /// generous reading.
    /// </remarks>
    public static string For(PullRequestState state) => state switch
    {
        PullRequestState.Open => "Open",
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed without merging",
        _ => state.ToString(),
    };

    /// <remarks>
    /// "Enquiry" and the rest read as they are, but Won and Lost are said as "Won" and
    /// "Lost" rather than "Closed won" — which is the language of a sales tool and not of
    /// anybody in this firm describing what happened.
    /// </remarks>
    public static string For(Stage stage) => stage switch
    {
        Stage.Enquiry => "Enquiry",
        Stage.Qualified => "Qualified",
        Stage.Proposed => "Proposed",
        Stage.Negotiating => "Negotiating",
        Stage.Won => "Won",
        Stage.Lost => "Lost",
        _ => stage.ToString(),
    };

    public static string For(ActivityKind kind) => kind switch
    {
        ActivityKind.Note => "A note",
        ActivityKind.Call => "A call",
        ActivityKind.Meeting => "A meeting",
        ActivityKind.Email => "An email",
        ActivityKind.Sent => "Sent them something",
        _ => kind.ToString(),
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
