using JiranisokoTech.Domain.Recruitment;

namespace JiranisokoTech.Tests.Domain;

/// <summary>
/// Requisitions, adverts and applications.
/// </summary>
public class RecruitmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static JobRequisition Raised(int headcount = 1) =>
        JobRequisition.Raise(
            "Delivery Engineer", Guid.CreateVersion7(), headcount,
            "Two projects starting in November and nobody free.", Guid.CreateVersion7());

    private static JobRequisition Approved(int headcount = 1)
    {
        var requisition = Raised(headcount);

        requisition.Submit(Now);
        requisition.Approved(Now);
        requisition.ClearEvents();

        return requisition;
    }

    [Fact]
    public void A_requisition_starts_as_a_draft_and_nobody_is_asked_anything()
    {
        var requisition = Raised(2);

        Assert.Equal(RequisitionStatus.Draft, requisition.Status);
        Assert.Equal(2, requisition.Remaining);
        Assert.False(requisition.CanHire);
        Assert.Single(requisition.Events.OfType<RequisitionRaised>());
        Assert.Empty(requisition.Events.OfType<RequisitionSubmitted>());
    }

    [Fact]
    public void A_requisition_has_to_be_for_at_least_one_person()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Raised(0));
    }

    [Fact]
    public void Submitting_announces_it_and_nothing_else()
    {
        var requisition = Raised();

        requisition.Submit(Now);

        Assert.Equal(RequisitionStatus.AwaitingApproval, requisition.Status);
        Assert.False(requisition.CanHire);
        Assert.Single(requisition.Events.OfType<RequisitionSubmitted>());
    }

    [Fact]
    public void Nothing_is_submitted_twice()
    {
        var requisition = Raised();
        requisition.Submit(Now);

        Assert.Throws<InvalidOperationException>(() => requisition.Submit(Now));
    }

    [Fact]
    public void A_decision_only_lands_on_something_awaiting_one()
    {
        var requisition = Raised();

        Assert.Throws<InvalidOperationException>(() => requisition.Approved(Now));
        Assert.Throws<InvalidOperationException>(() => requisition.Refused("no", Now));
    }

    [Fact]
    public void Approval_opens_it_for_hiring()
    {
        var requisition = Raised(2);

        requisition.Submit(Now);
        requisition.Approved(Now);

        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
        Assert.True(requisition.CanHire);
        Assert.Single(requisition.Events.OfType<RequisitionApproved>());
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_closes_the_door()
    {
        var requisition = Raised();

        requisition.Submit(Now);
        requisition.Refused("no budget this quarter", Now);

        Assert.Equal(RequisitionStatus.Refused, requisition.Status);
        Assert.Equal("no budget this quarter", requisition.Outcome);
        Assert.False(requisition.CanHire);
    }

    [Fact]
    public void Filling_the_last_post_closes_the_requisition()
    {
        var requisition = Approved(2);

        requisition.RecordHire(Now);

        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
        Assert.Equal(1, requisition.Remaining);

        requisition.RecordHire(Now);

        Assert.Equal(RequisitionStatus.Filled, requisition.Status);
        Assert.Equal(0, requisition.Remaining);
        Assert.False(requisition.CanHire);
        Assert.Single(requisition.Events.OfType<RequisitionFilled>());
    }

    [Fact]
    public void Nobody_is_hired_against_a_full_requisition()
    {
        var requisition = Approved();
        requisition.RecordHire(Now);

        var refused = Assert.Throws<InvalidOperationException>(() => requisition.RecordHire(Now));

        Assert.Contains("filled", refused.Message);
    }

    /// <summary>
    /// The count is a fact about people who now work here, and no figure typed
    /// into a form can make it untrue.
    /// </summary>
    [Fact]
    public void The_headcount_cannot_be_set_below_the_number_already_hired()
    {
        var requisition = Approved(3);

        requisition.RecordHire(Now);
        requisition.RecordHire(Now);

        var refused = Assert.Throws<InvalidOperationException>(
            () => requisition.ChangeHeadcount(1, Now));

        Assert.Contains("2 people have already been hired", refused.Message);
        Assert.Equal(3, requisition.Headcount);
    }

    /// <summary>
    /// The system this replaces could not do this. A filled requisition stayed
    /// filled, so hiring a second person for the same role meant raising a
    /// fresh one and losing the approval already given.
    /// </summary>
    [Fact]
    public void Raising_the_headcount_reopens_a_filled_requisition()
    {
        var requisition = Approved();
        requisition.RecordHire(Now);

        Assert.Equal(RequisitionStatus.Filled, requisition.Status);

        requisition.ChangeHeadcount(2, Now);

        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
        Assert.True(requisition.CanHire);
        Assert.Equal(1, requisition.Remaining);
    }

    [Fact]
    public void Closing_a_requisition_keeps_it_with_its_reason()
    {
        var requisition = Approved();

        requisition.Close("the project was cancelled", Now);

        Assert.Equal(RequisitionStatus.Closed, requisition.Status);
        Assert.Equal("the project was cancelled", requisition.Outcome);
        Assert.False(requisition.CanHire);
    }

    // --- adverts -----------------------------------------------------------

    [Fact]
    public void An_advert_starts_as_a_draft_with_an_address_of_its_own()
    {
        var posting = JobPosting.Draft(
            Guid.CreateVersion7(), "Delivery Engineer", "Build things", "A longer description.");

        Assert.Equal(PostingStatus.Draft, posting.Status);
        Assert.Equal("delivery-engineer", posting.Slug);
        Assert.False(posting.IsOpen);
    }

    [Fact]
    public void Publishing_and_closing_an_advert_are_announced()
    {
        var posting = JobPosting.Draft(
            Guid.CreateVersion7(), "Delivery Engineer", "Build things", "A longer description.");

        posting.Publish(Now);

        Assert.True(posting.IsOpen);
        Assert.Equal(Now, posting.PublishedAt);
        Assert.Single(posting.Events.OfType<PostingPublished>());

        posting.Close(Now);

        Assert.False(posting.IsOpen);
        Assert.Single(posting.Events.OfType<PostingClosed>());
    }

    /// <summary>
    /// A job advert gets linked to from elsewhere, and a link that stops
    /// working takes the applications with it.
    /// </summary>
    [Fact]
    public void Rewriting_an_advert_leaves_its_address_alone()
    {
        var posting = JobPosting.Draft(
            Guid.CreateVersion7(), "Delivery Engineer", "Build things", "A longer description.");

        posting.Rewrite("Senior Delivery Engineer", "Build bigger things", "Longer still.", "Nairobi");

        Assert.Equal("delivery-engineer", posting.Slug);
        Assert.Equal("Nairobi", posting.Location);
    }

    // --- applications ------------------------------------------------------

    private static JobApplication Applied() =>
        JobApplication.Receive(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void An_application_arrives_as_received()
    {
        var application = Applied();

        Assert.Equal(ApplicationStatus.Received, application.Status);
        Assert.True(application.IsLive);
        Assert.Single(application.Events.OfType<ApplicationReceived>());
    }

    [Theory]
    [InlineData(ApplicationStatus.Received, ApplicationStatus.Screening)]
    [InlineData(ApplicationStatus.Received, ApplicationStatus.Withdrawn)]
    public void An_allowed_move_is_made(ApplicationStatus from, ApplicationStatus to)
    {
        var application = Applied();

        if (from != ApplicationStatus.Received)
        {
            application.MoveTo(from, Now);
        }

        application.MoveTo(to, Now);

        Assert.Equal(to, application.Status);
    }

    /// <summary>
    /// Nobody is hired without an offer, and nobody is offered without being
    /// interviewed. A free column is how those get skipped.
    /// </summary>
    [Theory]
    [InlineData(ApplicationStatus.Hired)]
    [InlineData(ApplicationStatus.Offered)]
    [InlineData(ApplicationStatus.Interviewing)]
    public void An_application_cannot_jump_the_process(ApplicationStatus to)
    {
        var application = Applied();

        Assert.Throws<InvalidOperationException>(() => application.MoveTo(to, Now));
    }

    /// <summary>
    /// "Why did we not take them?" is asked by the candidate, by a colleague,
    /// and occasionally by a tribunal.
    /// </summary>
    [Fact]
    public void A_rejection_has_to_say_why()
    {
        var application = Applied();

        Assert.Throws<ArgumentException>(
            () => application.MoveTo(ApplicationStatus.Rejected, Now));

        Assert.Equal(ApplicationStatus.Received, application.Status);
    }

    /// <summary>
    /// People are reconsidered. Refusing to allow it means a second application
    /// that loses the history of the first.
    /// </summary>
    [Fact]
    public void Somebody_rejected_can_be_looked_at_again()
    {
        var application = Applied();

        application.MoveTo(ApplicationStatus.Rejected, Now, "we went with somebody more senior");

        Assert.Equal("we went with somebody more senior", application.RejectionReason);

        application.MoveTo(ApplicationStatus.Screening, Now);

        Assert.Equal(ApplicationStatus.Screening, application.Status);

        // And the reason goes, because it is no longer true.
        Assert.Null(application.RejectionReason);
    }

    [Fact]
    public void Nothing_comes_back_from_hired_or_withdrawn()
    {
        var hired = Applied();
        hired.MoveTo(ApplicationStatus.Screening, Now);
        hired.MoveTo(ApplicationStatus.Interviewing, Now);
        hired.MoveTo(ApplicationStatus.Offered, Now);
        hired.MoveTo(ApplicationStatus.Hired, Now);

        Assert.Throws<InvalidOperationException>(
            () => hired.MoveTo(ApplicationStatus.Screening, Now));

        var gone = Applied();
        gone.MoveTo(ApplicationStatus.Withdrawn, Now);

        Assert.Throws<InvalidOperationException>(
            () => gone.MoveTo(ApplicationStatus.Screening, Now));
    }

    [Fact]
    public void Being_hired_is_announced_on_its_own()
    {
        var application = Applied();

        application.MoveTo(ApplicationStatus.Screening, Now);
        application.MoveTo(ApplicationStatus.Interviewing, Now);
        application.MoveTo(ApplicationStatus.Offered, Now);
        application.ClearEvents();

        application.MoveTo(ApplicationStatus.Hired, Now);

        Assert.Single(application.Events.OfType<CandidateHired>());
        Assert.False(application.IsLive);
    }

    /// <summary>
    /// An event is the thing most likely to end up somewhere it was not meant
    /// to go — a notification, a webhook, a log. The internal reason stays out
    /// of it.
    /// </summary>
    [Fact]
    public void The_rejection_reason_never_travels_on_an_event()
    {
        var application = Applied();

        application.MoveTo(ApplicationStatus.Rejected, Now, "weak on the database side");

        var moved = Assert.Single(application.Events.OfType<ApplicationMoved>());

        Assert.DoesNotContain("database", moved.ToString());
    }

    [Fact]
    public void A_candidate_is_identified_by_their_address_in_lower_case()
    {
        var candidate = Candidate.Of("Precious", "  Precious@Example.COM ", " 0700 000 000 ", Now);

        Assert.Equal("precious@example.com", candidate.Email);
        Assert.Equal("Precious", candidate.FullName);
        Assert.Equal("0700 000 000", candidate.Phone);
    }

    /// <summary>
    /// Nobody administering this system needs a stranger's mobile number in an
    /// audit row, and the trail is read by more people than these screens are.
    /// </summary>
    [Fact]
    public void A_candidate_phone_number_stays_out_of_the_audit_trail()
    {
        Assert.Contains(nameof(Candidate.Phone), Candidate.AuditExcludes);
    }
}
