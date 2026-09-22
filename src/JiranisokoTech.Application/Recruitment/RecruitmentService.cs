using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.People;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Application.Recruitment;

/// <summary>
/// Hiring, from the request to the person.
/// </summary>
/// <remarks>
/// Deliberately knows nothing about approval chains. A requisition is submitted
/// and later told what was decided; who decides, and in what order, is settled
/// by a handler listening to the event. That is what lets the firm change who
/// signs off a hire without a line of this file moving.
/// </remarks>
public sealed class RecruitmentService(
    IRecruitmentRepository recruitment,
    IPeopleRepository people,
    ICvStore cvs,
    IClock clock)
{
    public async Task<JobRequisition> RaiseRequisitionAsync(
        string jobTitle,
        Guid? departmentId,
        int headcount,
        string justification,
        Guid raisedById,
        CancellationToken cancellationToken = default)
    {
        if (departmentId is { } department
            && !await people.DepartmentExistsAsync(department, cancellationToken))
        {
            throw new InvalidOperationException("That department does not exist.");
        }

        if (await people.FindAsync(raisedById, cancellationToken) is null)
        {
            throw new InvalidOperationException(
                "A requisition has to be raised by somebody on the staff list.");
        }

        var requisition = JobRequisition.Raise(
            jobTitle, departmentId, headcount, justification, raisedById);

        recruitment.Add(requisition);
        await recruitment.SaveAsync(cancellationToken);

        return requisition;
    }

    /// <summary>
    /// Send it for approval.
    /// </summary>
    /// <remarks>
    /// Saving raises the event that opens the chain. Nothing here waits for
    /// that: the chain is opened in its own transaction by the dispatcher, and
    /// the requisition sits at awaiting-approval in the meantime, which is
    /// exactly what it is.
    /// </remarks>
    public async Task SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        var requisition = await RequiredRequisition(requisitionId, cancellationToken);

        requisition.Submit(clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    public async Task RecordDecisionAsync(
        Guid requisitionId,
        bool approved,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var requisition = await RequiredRequisition(requisitionId, cancellationToken);

        if (approved)
        {
            requisition.Approved(clock.Now);
        }
        else
        {
            requisition.Refused(reason ?? "No reason was recorded.", clock.Now);
        }

        await recruitment.SaveAsync(cancellationToken);
    }

    public async Task ChangeHeadcountAsync(
        Guid requisitionId, int headcount, CancellationToken cancellationToken = default)
    {
        var requisition = await RequiredRequisition(requisitionId, cancellationToken);

        requisition.ChangeHeadcount(headcount, clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Give up on a requisition, taking its adverts down with it.
    /// </summary>
    /// <remarks>
    /// An advert left up for a post the firm has stopped filling collects
    /// applications from people who will hear nothing back, which is the single
    /// most common complaint about hiring anywhere.
    /// </remarks>
    public async Task<int> CloseRequisitionAsync(
        Guid requisitionId, string reason, CancellationToken cancellationToken = default)
    {
        var requisition = await RequiredRequisition(requisitionId, cancellationToken);

        requisition.Close(reason, clock.Now);

        var postings = await recruitment.OpenPostingsForAsync(requisitionId, cancellationToken);

        foreach (var posting in postings)
        {
            posting.Close(clock.Now);
        }

        await recruitment.SaveAsync(cancellationToken);

        return postings.Count;
    }

    public async Task<JobPosting> DraftPostingAsync(
        Guid requisitionId,
        string title,
        string summary,
        string description,
        string? location = null,
        string? slug = null,
        CancellationToken cancellationToken = default)
    {
        await RequiredRequisition(requisitionId, cancellationToken);

        var handle = Slug.From(slug ?? title);

        if (await recruitment.PostingSlugTakenAsync(handle.Value, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another advert already answers at '{handle.Value}'. Give this one a different "
                + "short name.");
        }

        var posting = JobPosting.Draft(
            requisitionId, title, summary, description, location, handle.Value);

        recruitment.Add(posting);
        await recruitment.SaveAsync(cancellationToken);

        return posting;
    }

    /// <summary>
    /// Put an advert up.
    /// </summary>
    /// <remarks>
    /// The rule this method exists for: nothing is advertised until the
    /// requisition behind it has been approved. An advert for a post nobody
    /// agreed to pay for is a promise the firm has not made, and somebody
    /// reading it cannot tell the difference.
    /// </remarks>
    public async Task PublishAsync(Guid postingId, CancellationToken cancellationToken = default)
    {
        var posting = await RequiredPosting(postingId, cancellationToken);
        var requisition = await RequiredRequisition(posting.RequisitionId, cancellationToken);

        if (requisition.Status != RequisitionStatus.Approved)
        {
            throw new InvalidOperationException(
                requisition.Status == RequisitionStatus.AwaitingApproval
                    ? "This is still with the approvers. Nothing is advertised until they agree."
                    : $"The requisition behind this advert is "
                      + $"{requisition.Status.ToString().ToLowerInvariant()}, so it cannot be "
                      + "advertised.");
        }

        posting.Publish(clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    public async Task ClosePostingAsync(
        Guid postingId, CancellationToken cancellationToken = default)
    {
        var posting = await RequiredPosting(postingId, cancellationToken);

        posting.Close(clock.Now);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record an application.
    /// </summary>
    /// <remarks>
    /// The candidate is matched on their email address, so somebody applying
    /// for a second post is recognisably the same person rather than two halves
    /// of a history nobody joins up.
    /// </remarks>
    public async Task<JobApplication> ApplyAsync(
        Guid postingId,
        string fullName,
        string email,
        string? phone = null,
        string? note = null,
        Stream? cv = null,
        string? cvFileName = null,
        CancellationToken cancellationToken = default)
    {
        var posting = await RequiredPosting(postingId, cancellationToken);

        if (!posting.IsOpen)
        {
            throw new InvalidOperationException(
                "That advert is not open, so an application cannot be recorded against it.");
        }

        var candidate = await recruitment.FindCandidateByEmailAsync(
            email.Trim().ToLowerInvariant(), cancellationToken);

        if (candidate is null)
        {
            candidate = Candidate.Of(fullName, email, phone, clock.Now);
            recruitment.Add(candidate);
        }
        else
        {
            // They may have changed their name or number since last time. The
            // address is what identifies them and is never rewritten.
            candidate.Update(fullName, phone ?? candidate.Phone);
        }

        var application = JobApplication.Receive(postingId, candidate.Id, clock.Now, note);

        /*
         * The file is written before the row that points at it.
         *
         * Either order leaves a window. A file with no row is an orphan on disk
         * that a sweep can find and remove; a row with no file is an
         * application whose CV button is broken, in front of somebody deciding
         * whether to interview a person. The first is the better failure.
         */
        if (cv is not null && cvFileName is not null)
        {
            var stored = await cvs.SaveAsync(cv, cvFileName, cancellationToken);

            application.AttachCv(cvFileName, stored);
        }

        recruitment.Add(application);
        await recruitment.SaveAsync(cancellationToken);

        return application;
    }

    public async Task MoveApplicationAsync(
        Guid applicationId,
        ApplicationStatus status,
        string? because = null,
        CancellationToken cancellationToken = default)
    {
        var application = await RequiredApplication(applicationId, cancellationToken);

        if (status == ApplicationStatus.Hired)
        {
            throw new InvalidOperationException(
                "Use the hire step rather than moving it. Hiring takes a post off the "
                + "requisition, and that has to happen in the same breath.");
        }

        application.MoveTo(status, clock.Now, because);
        await recruitment.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Hire somebody, and take a post off the requisition.
    /// </summary>
    /// <remarks>
    /// Both in one transaction, and the requisition is checked first. Hiring
    /// three people against a requisition for two is the failure this prevents,
    /// and it is only catchable here: the application knows nothing about the
    /// count, and the requisition knows nothing about who applied.
    /// </remarks>
    public async Task HireAsync(Guid applicationId, CancellationToken cancellationToken = default)
    {
        var application = await RequiredApplication(applicationId, cancellationToken);
        var posting = await RequiredPosting(application.PostingId, cancellationToken);
        var requisition = await RequiredRequisition(posting.RequisitionId, cancellationToken);

        if (!requisition.CanHire)
        {
            throw new InvalidOperationException(
                requisition.Status == RequisitionStatus.Approved
                    ? $"All {requisition.Headcount} post(s) on this requisition are filled. "
                      + "Raise the headcount if there is room for another."
                    : $"The requisition is "
                      + $"{requisition.Status.ToString().ToLowerInvariant()} and cannot be hired "
                      + "against.");
        }

        application.MoveTo(ApplicationStatus.Hired, clock.Now);
        requisition.RecordHire(clock.Now);

        await recruitment.SaveAsync(cancellationToken);
    }

    private async Task<JobRequisition> RequiredRequisition(
        Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindRequisitionAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no requisition with that identifier.");

    private async Task<JobPosting> RequiredPosting(Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindPostingAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no advert with that identifier.");

    private async Task<JobApplication> RequiredApplication(
        Guid id, CancellationToken cancellationToken) =>
        await recruitment.FindApplicationAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no application with that identifier.");
}
