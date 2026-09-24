using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Time;

/// <summary>
/// Something the firm has planned that belongs to nothing else.
/// </summary>
/// <remarks>
/// Section 33, and the <i>only</i> new storage the calendar adds. Everything else it shows is
/// already recorded somewhere: leave, holidays, sprints, review cycles, contract and agreement
/// expiries, goal dates, work due dates, interviews. The calendar reads those where they live.
///
/// <b>This exists because one question has nowhere to go.</b> "When is the all-hands" is what
/// every calendar is asked first, and the two obvious homes both break:
///
/// <see cref="Holiday"/> would be wrong in a way nothing would warn about. A holiday is a
/// non-working day, and leave counts working days between two dates — so filing the all-hands as
/// a holiday would silently give everybody a day back on every leave request spanning it.
///
/// An opportunity's activity is append-only history of contact that already happened, so a future
/// meeting cannot be one without changing what that record means.
///
/// <b>Four columns, and that is the honest minimum.</b> No attendees, no invitations, no
/// recurrence. An invitation is a message with a state machine and a notification per transition;
/// recurrence is a rule engine with exceptions to the rule. Both are modules, and neither is what
/// somebody wants when they type "all-hands, Friday".
/// </remarks>
public sealed class Occasion : Entity, IAuditable
{
    private Occasion() => Name = string.Empty;

    private Occasion(DateOnly on, DateOnly? until, string name, string? note)
    {
        if (until is { } ends && ends < on)
        {
            throw new ArgumentException(
                "An occasion cannot end before it starts.", nameof(until));
        }

        On = on;
        Until = until;
        Name = Require(name, nameof(name));
        Note = Trimmed(note);
    }

    public static Occasion Planned(
        DateOnly on, string name, DateOnly? until = null, string? note = null) =>
        new(on, until, name, note);

    /// <summary>The day it happens, or the first day when it runs over several.</summary>
    public DateOnly On { get; private set; }

    /// <summary>
    /// The last day, when it runs over several. Null for a single day.
    /// </summary>
    /// <remarks>
    /// Nullable rather than defaulted to the start date, because "one day" and "a range that
    /// happens to be one day" read differently on a screen — and an office closure over a week is
    /// the case that makes the field worth having at all.
    /// </remarks>
    public DateOnly? Until { get; private set; }

    public string Name { get; private set; }

    public string? Note { get; private set; }

    /// <summary>The last day it occupies, whether or not a range was given.</summary>
    public DateOnly Ends => Until ?? On;

    public bool RunsOver(DateOnly from, DateOnly to) => On <= to && Ends >= from;

    public void Rename(string name) => Name = Require(name, nameof(name));

    public void Runs(DateOnly on, DateOnly? until)
    {
        if (until is { } ends && ends < on)
        {
            throw new ArgumentException(
                "An occasion cannot end before it starts.", nameof(until));
        }

        On = on;
        Until = until;
    }

    public void Describe(string? note) => Note = Trimmed(note);

    /// <summary>
    /// Nothing here is a secret.
    /// </summary>
    /// <remarks>
    /// An occasion is the firm telling everybody something is happening, so the trail records it
    /// in full. Unlike most things in this system it is deleted rather than ended when it is
    /// withdrawn — a meeting that was cancelled before it happened is not history, it is a plan
    /// that changed, and the audit trail is what keeps the record of the change.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Require(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
