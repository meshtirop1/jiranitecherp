using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Application.People;

/// <summary>
/// The list of things that have to happen before somebody starts.
/// </summary>
/// <remarks>
/// Section 8's second half, and deliberately the mirror of what section 9 does at the other end.
/// The two are the same problem pointed in opposite directions, and what a firm needs from both
/// is the same thing: a list of what has not happened yet, in front of somebody, before the day
/// it matters.
///
/// <b>Nothing here is automatic.</b> The account is not created from the start date, the
/// equipment is not ordered, nobody is added to anything. Each is a person marking something
/// done, for the reasons offboarding gives at length: a start date entered wrongly would
/// otherwise create a sign-in for somebody who does not work here, and what access a new person
/// gets is a decision rather than a consequence.
///
/// <b>Equipment is not recorded here.</b> The "Equipment issued" step is ticked by handing
/// something over on the asset register, which is the one place that knows where a particular
/// laptop is — see section 15. This aggregate briefly kept its own list, and a list that only
/// the joiner's screen can read is how a laptop goes missing without anybody noticing.
/// </remarks>
public sealed class OnboardingService(IPeopleRepository people, IClock clock)
{
    /// <summary>
    /// Start a checklist for somebody already on the staff list.
    /// </summary>
    /// <remarks>
    /// For people hired outside the offer flow, which is most of the first ones in any system:
    /// somebody types in the existing staff and then wants a checklist for the joiner next
    /// month. Returns the existing one rather than refusing, because two checklists for one
    /// person means the laptop is on neither.
    /// </remarks>
    public async Task<Onboarding> BeginAsync(
        Guid employeeId, DateOnly startsOn, CancellationToken cancellationToken = default)
    {
        if (await people.OnboardingForAsync(employeeId, cancellationToken) is { } already)
        {
            return already;
        }

        if (await people.FindAsync(employeeId, cancellationToken) is null)
        {
            throw new InvalidOperationException("That person is not on the staff list.");
        }

        var onboarding = Onboarding.Begin(employeeId, startsOn, clock.Now);

        people.Add(onboarding);
        await people.SaveAsync(cancellationToken);

        return onboarding;
    }

    public Task<Onboarding?> ForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        people.OnboardingForAsync(employeeId, cancellationToken);

    /// <summary>Every checklist still open, soonest start first.</summary>
    public Task<List<Onboarding>> OutstandingAsync(
        CancellationToken cancellationToken = default) =>
        people.OnboardingsAsync(cancellationToken);

    public async Task DidAsync(
        Guid employeeId,
        Guid stepId,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.Did(stepId, byId, clock.Now);

        await people.SaveAsync(cancellationToken);
    }

    public async Task NotDoneAsync(
        Guid employeeId, Guid stepId, CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.NotDone(stepId);

        await people.SaveAsync(cancellationToken);
    }

    /// <summary>Add something this role needs that the usual list does not have.</summary>
    public async Task AlsoAsync(
        Guid employeeId, string name, CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.Also(name);

        await people.SaveAsync(cancellationToken);
    }

    public async Task NotNeededAsync(
        Guid employeeId, Guid stepId, CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.NotNeeded(stepId);

        await people.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Move the start date.
    /// </summary>
    /// <remarks>
    /// It moves, often, between an offer being accepted and somebody arriving — notice periods
    /// are negotiated and visas take what they take. The checklist's date rather than the staff
    /// record's, because this is the date the list is late against.
    /// </remarks>
    public async Task StartsOnAsync(
        Guid employeeId, DateOnly on, CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.StartsOnActually(on);

        await people.SaveAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var onboarding = await Required(employeeId, cancellationToken);

        onboarding.Complete(clock.Now);

        await people.SaveAsync(cancellationToken);
    }

    private async Task<Onboarding> Required(
        Guid employeeId, CancellationToken cancellationToken) =>
        await people.OnboardingForAsync(employeeId, cancellationToken)
        ?? throw new InvalidOperationException("There is no checklist for that person.");
}
