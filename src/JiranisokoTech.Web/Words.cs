using JiranisokoTech.Domain.Automation;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;

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
    /// <summary>
    /// What to call the firm when its own name cannot be read yet.
    /// </summary>
    /// <remarks>
    /// The only place in the web project this name is typed, and it is here rather than on a page
    /// because <c>TheFirmsOwnNameTests</c> walks every <c>.razor</c> file for it. Seven of them had
    /// it, so changing the trading name on the settings screen changed it on invoices and on
    /// nothing a visitor ever saw.
    ///
    /// It is a fallback and nothing else. Every place that shows the name reads
    /// <c>FirmSettings.TradingName</c>; this is what renders for the frame before that read
    /// completes, and a blank where a firm's name should be reads as a broken page.
    /// </remarks>
    public const string TheFirm = "Jiranisoko Tech Solutions";

    /// <summary>
    /// A count and the thing counted, in the right number.
    /// </summary>
    /// <remarks>
    /// Because eight screens said "3 service(s)", "@Ever.Count thing(s)" and "invoice(s)" while
    /// every other headline in the application says "One incident is open" and "2 clients"
    /// properly. The bracket is the form somebody writes when they are not thinking about the
    /// reader, and on a page beside one that does it properly it reads as the unfinished half.
    ///
    /// <paramref name="many"/> is for the words English does not pluralise with an s — "people",
    /// "things it runs on" — and defaults to the regular form, which covers most of them.
    /// </remarks>
    public static string Count(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    /// <summary>
    /// A length of time, said the way a person would say it.
    /// </summary>
    /// <remarks>
    /// One copy, because there were six: the two help desk pages, the two incident pages, the
    /// incident review and the service page each carried their own, and every one of them said
    /// <b>"1 days"</b>. It is on screen wherever a span happens to land between one and two days —
    /// how long an incident ran, how long ago a promise was missed, how long a firm has to answer
    /// — which is often enough that somebody would have noticed and rare enough that nobody had.
    /// Found by opening the help desk and reading the sentence under the priority select.
    ///
    /// Never in seconds and never to two decimal places. The only thing anybody does with these
    /// numbers is compare them to what was promised, and "4.25 hours" is harder to do that with
    /// than "4 hr".
    ///
    /// <paramref name="nothing"/> is what to say for a span that has not begun or has run
    /// backwards, because the pages differ: a duration reads "no time" and a countdown to a
    /// deadline reads "moments".
    /// </remarks>
    public static string HowLong(TimeSpan span, string nothing = "no time")
    {
        if (span < TimeSpan.Zero)
        {
            return nothing;
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            var minutes = (int)span.TotalMinutes;

            return minutes == 1 ? "1 min" : $"{minutes} min";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;

            return minutes == 0 ? $"{hours} hr" : $"{hours} hr {minutes} min";
        }

        var days = (int)span.TotalDays;

        return days == 1 ? "1 day" : $"{days} days";
    }

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

    /// <summary>What became of a build, or that nothing has yet.</summary>
    /// <remarks>
    /// "Building" rather than "Running", because this word lands in a column beside
    /// "Passed" and "Failed" and a reader scanning that column is asking what the
    /// answer was. A noun there looks like an answer whether or not it is one; a verb
    /// in the present tense cannot be mistaken for a result, and the fifteen minutes
    /// a build takes are exactly the window in which somebody is looking.
    ///
    /// Cancelled keeps its own word and is not softened towards Failed. Somebody
    /// pushed again or the queue was drained, and the code was never broken — so a
    /// screen that reports a cancelled build as a failure puts a mark against work
    /// that was fine, and the reliable lesson people draw from that is to stop
    /// believing the failures.
    /// </remarks>
    public static string For(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Running => "Building",
        BuildOutcome.Passed => "Passed",
        BuildOutcome.Failed => "Failed",
        BuildOutcome.Cancelled => "Cancelled",

        /*
         * Not "Blocked", which reads as a statement about the code. The pipeline has stopped
         * at a gate and is waiting for a person, and saying so is also saying what to do
         * about it.
         */
        BuildOutcome.Blocked => "Waiting on somebody",
        _ => outcome.ToString(),
    };

    /// <summary>Which of the firm's environments something reached.</summary>
    /// <remarks>
    /// The three named environments are already what everybody here calls them, so
    /// there is nothing to translate and translating anyway would only invent a
    /// second vocabulary for the same three places.
    ///
    /// <c>Other</c> is the one that needs care. It is the result of the classifier
    /// failing to recognise a name, and the host's own name for the environment is
    /// printed beside this word — so "Somewhere else" says exactly as much as is
    /// known and leaves the specific answer to the text next to it. A confident
    /// label like "Custom" or "Internal" would be this system asserting something
    /// about a deployment it could not place.
    /// </remarks>
    public static string For(DeploymentEnvironment environment) => environment switch
    {
        DeploymentEnvironment.Development => "Development",
        DeploymentEnvironment.Staging => "Staging",
        DeploymentEnvironment.Production => "Production",
        DeploymentEnvironment.Other => "Somewhere else",
        _ => environment.ToString(),
    };

    /// <summary>Where a deployment got to.</summary>
    /// <remarks>
    /// "Going out now" rather than "Running", and deliberately not the same word as a
    /// build's, because the two middles ask different things of the reader: a build
    /// running is a wait, and a deployment running is the few minutes in which an
    /// environment may be neither the old version nor the new one. Somebody who sees
    /// the same word for both will treat them the same way.
    ///
    /// "Live" for succeeded because that is what is said in the room, and because the
    /// environment is printed beside it: "Staging — Live" is a sentence a person would
    /// say, and "Staging — Succeeded" is one only a pipeline would.
    /// </remarks>
    /// <remarks>
    /// Past tense, because both screens that show these are historical. The timesheet lists
    /// a day that has already happened, so "going out now" beside 14:32 on last Tuesday is
    /// simply untrue; and a work item's panel is read long after the release. "Deployed"
    /// and "Deploy failed" read correctly in both.
    ///
    /// Written here rather than on either page because two screens showed this enum with
    /// two different vocabularies for a while — one saying "Live" and the other "Deployed"
    /// for the same stored value — which is the exact drift this file exists to prevent, and
    /// neither page was wrong on its own.
    /// </remarks>
    public static string For(DeploymentState state) => state switch
    {
        DeploymentState.Running => "Deploying",
        DeploymentState.Succeeded => "Deployed",
        DeploymentState.Failed => "Deploy failed",
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

    /// <summary>A four-point recommendation, said the way an interviewer says it.</summary>
    /// <remarks>
    /// Moved here from Interviews.razor when the exercises screen became the second place
    /// showing it. Two copies of an enum's wording differ the first time one is adjusted, and
    /// a reader holding both concludes one of the screens is wrong — which is the whole
    /// argument for this file.
    /// </remarks>
    public static string For(Recommendation recommendation) => recommendation switch
    {
        Recommendation.StrongNo => "Strong no",
        Recommendation.No => "No",
        Recommendation.Yes => "Yes",
        Recommendation.StrongYes => "Strong yes",
        _ => recommendation.ToString(),
    };

    public static string For(AssessmentKind kind) => kind switch
    {
        AssessmentKind.TakeHome => "Take-home",
        AssessmentKind.LiveExercise => "Live exercise",
        AssessmentKind.WrittenTest => "Written test",
        _ => kind.ToString(),
    };

    /// <remarks>
    /// "Out with them" rather than "Assigned", because the question somebody opens the
    /// exercises screen to answer is who is waiting on whom.
    /// </remarks>
    public static string For(AssessmentStatus status) => status switch
    {
        AssessmentStatus.Assigned => "Out with them",
        AssessmentStatus.Submitted => "Waiting to be marked",
        AssessmentStatus.Marked => "Marked",
        AssessmentStatus.Cancelled => "Called off",
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

    /// <summary>The colour a build outcome gets, not the word for it.</summary>
    /// <remarks>
    /// The words are a few methods up, where every screen gets them; this is only the pill. It is
    /// a method rather than the inline conditional the pull request column uses because four
    /// outcomes do not fit one, and because a page's summary line and the table under it have to
    /// agree: a failed build shown red in the sentence and grey in the table is a reader deciding
    /// the page is unreliable and going to the host to check, which is the habit the panel was
    /// built to remove.
    ///
    /// Cancelled is quiet rather than red on purpose. Somebody pushed again or the queue was
    /// drained; nothing was broken, and marking it as a failure puts a red row against work that
    /// was fine.
    ///
    /// Here rather than on the work item page, which is where it was, because the stream of what
    /// happened to a work item draws the same pills from the same rows immediately above that
    /// table. Two copies would disagree the first time one of them was adjusted, and a reader
    /// holding a red sentence and an amber pill about one build cannot tell which is wrong.
    /// </remarks>
    public static string PillFor(BuildOutcome outcome) => outcome switch
    {
        BuildOutcome.Passed => "pill--done",
        BuildOutcome.Failed => "pill--danger",

        // Its own colour, not the waiting one. A build that is running is not queued.
        BuildOutcome.Running => "pill--running",

        /*
         * Amber rather than the quiet grey a cancelled build gets. A cancelled build asks nothing
         * of anybody; a blocked one is waiting for a person to open a gate, and drawn the same
         * colour the second one is invisible on a page somebody is scanning.
         */
        BuildOutcome.Blocked => "pill--wait",
        _ => "pill--quiet",
    };

    /// <summary>The colour a pull request's state gets.</summary>
    /// <remarks>
    /// The work item page had this inline — <c>State == Merged ? "pill--done" : "pill--quiet"</c>
    /// — which painted an OPEN pull request the same grey as one closed without merging. Those
    /// are opposite pieces of news: one is waiting for somebody and the other is over, and on a
    /// panel somebody scans to find what needs them, the waiting one is the row that matters.
    /// Open is amber here for the same reason a blocked build is.
    /// </remarks>
    public static string PillFor(PullRequestState state) => state switch
    {
        PullRequestState.Merged => "pill--done",
        PullRequestState.Open => "pill--wait",
        _ => "pill--quiet",
    };

    /// <summary>The colour a deployment's state gets.</summary>
    /// <remarks>
    /// Its own overload rather than a shared one over both enums, because a build and a deployment
    /// do not have the same middle: a build running is a wait, and a deployment running is the few
    /// minutes in which an environment is neither the old version nor the new one. They share the
    /// amber for now, and a change to either should not silently be a change to both.
    ///
    /// Two pages had a copy of exactly this switch under two different names -- <c>Tone</c> on the
    /// work item and <c>PillFor</c> on the timesheet -- each with its own paragraph explaining the
    /// same middle arm in different words. Neither had drifted in behaviour, which is the only
    /// reason it was worth moving rather than fixing.
    /// </remarks>
    public static string PillFor(DeploymentState state) => state switch
    {
        DeploymentState.Succeeded => "pill--done",
        DeploymentState.Failed => "pill--danger",

        /*
         * Its own colour rather than the waiting one. A deployment that is still going is not
         * queued behind anything — it is happening — and on a panel read to reconstruct an
         * afternoon those are different pieces of news.
         */
        _ => "pill--running",
    };

    /// <remarks>
    /// "Held back" rather than "Suppressed", because the question somebody opens a rule's
    /// history to answer is why it did not do anything, and "suppressed" sounds like a fault.
    /// </remarks>
    public static string For(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Waiting => "Waiting out its delay",
        AutomationRunStatus.Queued => "About to run",
        AutomationRunStatus.Done => "Done",
        AutomationRunStatus.DoneWithRefusals => "Done, with refusals",
        AutomationRunStatus.Retrying => "Failed, will retry",
        AutomationRunStatus.GaveUp => "Gave up",
        AutomationRunStatus.Suppressed => "Held back",
        AutomationRunStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    public static string PillFor(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Done => "pill--done",
        AutomationRunStatus.DoneWithRefusals or AutomationRunStatus.Waiting => "pill--wait",
        AutomationRunStatus.Queued => "pill--running",
        AutomationRunStatus.Retrying or AutomationRunStatus.GaveUp => "pill--danger",
        _ => "pill--quiet",
    };

    /// <remarks>
    /// Five pages carried a copy of this switch and no two of them were quite the same shape:
    /// three worded it in title case, two in lower, and one of the lower pair spelt Blocked out
    /// by hand while its twin let the fall-through do it. None of them disagreed about a word
    /// yet, which is exactly the state the <c>Length(TimeSpan)</c> copies were in before one of
    /// them started writing "1 days" on three screens. The rule in CLAUDE.md is that a small
    /// formatting helper belongs here, and a status is the most-rendered value in the work half
    /// of this application.
    ///
    /// Lower case is taken at the call site with <c>ToLowerInvariant</c>, which is what the two
    /// pages wanting it in a sentence already did, rather than a second method whose only
    /// difference is capitalisation.
    ///
    /// This one falls through where <see cref="WorkItem.Name(WorkItemKind)"/> deliberately
    /// throws, and the difference is worth stating: an unnamed KIND is the zero an unbound
    /// select posts, so it has to be refused. The four statuses not named here --
    /// Blocked, Done, Deployed, Cancelled -- are each spelt correctly by <c>ToString</c>, so the
    /// fall-through is the right answer for them rather than a guess nobody checked.
    ///
    /// <see cref="WorkItem"/> keeps its own private copy. It words exception text, the domain
    /// cannot reference the web project, and a sentence thrown at a caller is not a sentence on
    /// a screen. That duplication is a decision.
    /// </remarks>
    public static string For(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Todo => "To do",
        WorkItemStatus.InProgress => "In progress",
        WorkItemStatus.InReview => "In review",
        _ => status.ToString(),
    };
}
