using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// The exercise a candidate is set between the interview and the offer.
/// </summary>
/// <remarks>
/// Almost nothing is here, and what is here is the two rules the aggregate cannot see because
/// each needs a second row loaded: that the application is at a stage where setting an
/// exercise makes sense, and that there is not already one out on it. Everything else — what
/// may be marked, what a cancellation requires, whether a deadline may move — is on
/// <see cref="TechnicalAssessment"/>, which can answer all of it from inside itself.
///
/// It does not move the application to Assessing, deliberately. Setting an exercise and
/// deciding that somebody is at the exercise stage are two acts, the second belongs to
/// whoever runs the hiring, and a service that did both would take a decision out of their
/// hands on the strength of a form submission.
/// </remarks>
public sealed class AssessmentService(IRecruitmentRepository recruitment, IClock clock)
{
    /// <summary>
    /// Set an exercise.
    /// </summary>
    /// <remarks>
    /// Refused while another is outstanding on the same application. Two live exercises for
    /// one candidate is either a mistake or two people working on the same application
    /// without knowing — and both are better answered by a refusal naming the first one than
    /// by a second row that makes the offer gate ambiguous.
    ///
    /// Refused for an application that is no longer live, because setting work for somebody
    /// who has withdrawn or been turned down is a letter nobody should send.
    /// </remarks>
    public async Task<TechnicalAssessment> SetAsync(
        Guid applicationId,
        AssessmentKind kind,
        string title,
        string instructions,
        DateOnly dueBy,
        Guid? setBy = null,
        CancellationToken cancellationToken = default)
    {
        var application = await recruitment.FindApplicationAsync(applicationId, cancellationToken)
            ?? throw new InvalidOperationException("There is no application with that identifier.");

        if (!application.IsLive)
        {
            throw new InvalidOperationException(
                "That application is no longer live, so there is nobody to set an exercise for.");
        }

        if (dueBy < clock.Today)
        {
            throw new ArgumentException(
                "That deadline has already gone by. An exercise cannot be due before it is set.",
                nameof(dueBy));
        }

        if (await recruitment.AssessmentOutstandingAsync(applicationId, cancellationToken))
        {
            throw new InvalidOperationException(
                "There is already an exercise out on this application. Mark it or call it off "
                + "before setting another, or the offer gate cannot tell which one it is "
                + "waiting for.");
        }

        var assessment = TechnicalAssessment.Set(
            applicationId, kind, title, instructions, dueBy, setBy, clock.Now);

        recruitment.Add(assessment);
        await recruitment.SaveAsync(cancellationToken);

        return assessment;
    }

    /// <summary>The candidate has handed something in.</summary>
    public async Task HandedInAsync(
        Guid assessmentId,
        string? submissionUrl = null,
        CancellationToken cancellationToken = default)
    {
        var assessment = await Required(assessmentId, cancellationToken);

        assessment.HandedIn(submissionUrl, clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>Somebody has marked it.</summary>
    public async Task MarkAsync(
        Guid assessmentId,
        Recommendation result,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var assessment = await Required(assessmentId, cancellationToken);

        assessment.Marked(result, notes, clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>Call it off, with a reason.</summary>
    public async Task CancelAsync(
        Guid assessmentId, string because, CancellationToken cancellationToken = default)
    {
        var assessment = await Required(assessmentId, cancellationToken);

        assessment.Cancel(because, clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>Give them longer.</summary>
    public async Task DueLaterAsync(
        Guid assessmentId, DateOnly dueBy, CancellationToken cancellationToken = default)
    {
        var assessment = await Required(assessmentId, cancellationToken);

        assessment.DueLater(dueBy);
        await recruitment.SaveAsync(cancellationToken);
    }

    private async Task<TechnicalAssessment> Required(
        Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindAssessmentAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no exercise with that identifier.");
}
