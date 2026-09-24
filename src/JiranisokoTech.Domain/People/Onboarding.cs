using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// One thing that has to happen before somebody can work here.
/// </summary>
/// <remarks>
/// A row rather than a column, because the list differs by role and by year — a firm that adds
/// a security induction in March should not need a migration — and because a checklist somebody
/// can add to is one they keep using.
///
/// It records who marked it done as well as when. Not for blame: the useful question three
/// weeks later is "who set up the laptop, because it has the wrong keyboard layout", and the
/// answer is otherwise nobody's.
/// </remarks>
public sealed class OnboardingStep : Entity
{
    private OnboardingStep() => Name = string.Empty;

    internal OnboardingStep(string name, int order)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A step needs a name.", nameof(name))
            : name.Trim();
        Order = order;
    }

    public string Name { get; private init; }

    /// <summary>Where it sits in the list.</summary>
    /// <remarks>
    /// Kept explicitly rather than relying on insertion order, because the order matters — a
    /// sign-in before a contract is signed is the wrong way round — and a collection's order
    /// coming back from a database is whatever the database felt like.
    /// </remarks>
    public int Order { get; private init; }

    public DateTimeOffset? DoneAt { get; private set; }

    public Guid? DoneById { get; private set; }

    public bool IsDone => DoneAt is not null;

    internal void Did(Guid byId, DateTimeOffset at)
    {
        DoneAt = at;
        DoneById = byId;
    }

    /// <summary>
    /// Mark it undone, because people tick the wrong row.
    /// </summary>
    /// <remarks>
    /// Allowed, unlike a timeline line. A checklist is a working document about what still has
    /// to happen rather than a record of what was known when, and a tick that cannot be undone
    /// means the list stops being true the first time somebody misses.
    /// </remarks>
    internal void NotDone()
    {
        DoneAt = null;
        DoneById = null;
    }
}

/// <summary>
/// The list of things that have to happen before somebody starts.
/// </summary>
/// <remarks>
/// Section 8, and the other end of section 9's <see cref="Offboarding"/> — deliberately built to
/// the same shape, because they are the same problem pointed in opposite directions and the one
/// thing a firm needs from both is a list of what has not happened yet.
///
/// <b>A checklist, not an automation</b>, for exactly the reasons offboarding gives. The account
/// could be created automatically from the start date; it is not, because a start date entered
/// wrongly would then create a sign-in for somebody who does not work here, and because the
/// person who should decide what access somebody gets is a person.
///
/// <b>Equipment is not kept here.</b> It was, for about an hour: this aggregate had its own list
/// of what somebody had been handed and offboarding had another, and neither could answer where
/// a particular laptop was. Section 15 replaced both with one register the whole firm shares, so
/// the "Equipment issued" step is ticked by issuing from that register — which is also what the
/// leaver's list reads from.
/// </remarks>
public sealed class Onboarding : Entity, IAuditable
{
    /// <summary>
    /// What every new person needs, in the order it has to happen.
    /// </summary>
    /// <remarks>
    /// Written down here rather than configured on a screen, and it is worth saying why. A
    /// settings page for this would be a table somebody fills in once and nobody maintains, and
    /// the cost of being wrong is that a firm which needs a ninth step adds it to each
    /// onboarding by hand — which they can. The contract is signed before anything else because
    /// everything after it commits the firm to something.
    /// </remarks>
    public static IReadOnlyList<string> Usual { get; } =
    [
        "Signed contract returned",
        "Right to work checked",
        "Staff record complete — bank details, next of kin, tax number",
        "Sign-in created",
        "Equipment issued",
        "Added to the right teams and repositories",
        "Induction held",
        "First week planned with their manager",
    ];

    private readonly List<OnboardingStep> _steps = [];

    private Onboarding()
    {
    }

    private Onboarding(Guid employeeId, DateOnly startsOn, DateTimeOffset at)
    {
        EmployeeId = employeeId;
        StartsOn = startsOn;
        BegunAt = at;

        for (var i = 0; i < Usual.Count; i++)
        {
            _steps.Add(new OnboardingStep(Usual[i], i));
        }

        Raise(new OnboardingBegun(Id, employeeId, startsOn, at));
    }

    public static Onboarding Begin(Guid employeeId, DateOnly startsOn, DateTimeOffset at) =>
        new(employeeId, startsOn, at);

    public Guid EmployeeId { get; private init; }

    public DateOnly StartsOn { get; private set; }

    public DateTimeOffset BegunAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<OnboardingStep> Steps =>
        [.. _steps.OrderBy(one => one.Order).ThenBy(one => one.Name, StringComparer.Ordinal)];

    public bool IsComplete => CompletedAt is not null;

    public IReadOnlyList<OnboardingStep> Outstanding =>
        [.. Steps.Where(one => !one.IsDone)];

    /// <summary>
    /// Is anything still outstanding on somebody's first day?
    /// </summary>
    /// <remarks>
    /// Takes the date rather than reading a clock, so that the answer is the same wherever it
    /// is asked. This is the number the list exists to put in front of somebody: a person
    /// arriving on Monday with no sign-in is a Monday nobody gets back.
    /// </remarks>
    public bool IsLate(DateOnly today) => !IsComplete && today >= StartsOn && Outstanding.Count > 0;

    public void StartsOnActually(DateOnly on) => StartsOn = on;

    public void Did(Guid stepId, Guid byId, DateTimeOffset at)
    {
        Step(stepId).Did(byId, at);

        /*
         * Never completed automatically, even when the last step is ticked. Completing is
         * somebody saying "this person is set up", and a list that closed itself the moment the
         * last box was ticked would mean nobody ever looked at the whole of it.
         */
    }

    public void NotDone(Guid stepId) => Step(stepId).NotDone();

    /// <summary>Add something this role needs that the usual list does not have.</summary>
    public OnboardingStep Also(string name)
    {
        var step = new OnboardingStep(name, _steps.Count);

        _steps.Add(step);

        return step;
    }

    /// <summary>Drop a step that does not apply.</summary>
    /// <remarks>
    /// Rather than ticking it to get it out of the way, which is what people do otherwise — and
    /// a list of ticks where some mean "done" and some mean "not applicable" is a list nobody
    /// can read.
    /// </remarks>
    public void NotNeeded(Guid stepId) => _steps.RemoveAll(one => one.Id == stepId);


    /// <summary>
    /// Everything is done.
    /// </summary>
    /// <remarks>
    /// Refused while anything is outstanding, which is the opposite of <c>Offboarding.Complete</c>
    /// — that one allows it, because a leaver's laptop sometimes never comes back and a list
    /// that cannot be closed is one that stays open forever accusing somebody. Here the
    /// outstanding items are things the firm has not done for a person who works here, and
    /// there is no equivalent of "they kept it": either it happened or it has not yet.
    /// </remarks>
    public void Complete(DateTimeOffset at)
    {
        if (IsComplete)
        {
            return;
        }

        if (Outstanding.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Outstanding.Count} thing(s) are still outstanding. Tick them, or take off "
                + "the ones that do not apply to this role.");
        }

        CompletedAt = at;

        Raise(new OnboardingCompleted(Id, EmployeeId, at));
    }

    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private OnboardingStep Step(Guid id) =>
        _steps.FirstOrDefault(one => one.Id == id)
        ?? throw new InvalidOperationException("That step is not on this list.");
}

public sealed record OnboardingBegun(
    Guid OnboardingId,
    Guid EmployeeId,
    DateOnly StartsOn,
    DateTimeOffset At) : DomainEvent;

public sealed record OnboardingCompleted(
    Guid OnboardingId, Guid EmployeeId, DateTimeOffset At) : DomainEvent;
