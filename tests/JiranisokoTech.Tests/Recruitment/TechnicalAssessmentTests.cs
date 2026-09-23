using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Infrastructure.Recruitment;
using JiranisokoTech.Tests.Infrastructure;

namespace JiranisokoTech.Tests.Recruitment;

/// <summary>
/// The exercise between the interview and the offer.
/// </summary>
/// <remarks>
/// Section 7's chain ran requisition, approval, opening, published, application, screening,
/// interview, offer letter — and the step between the last two was missing. Almost every firm
/// that hires engineers sets something, and without a record of it the decision to make an
/// offer rests on a conversation nobody wrote down.
///
/// The refusals are what is worth testing. An exercise somebody can mark without reading it,
/// or an offer that can go out while the work is still with the candidate, is a stage that
/// exists on a screen and changes nothing.
/// </remarks>
public class TechnicalAssessmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An offer is refused while an exercise is still out.
    /// </summary>
    /// <remarks>
    /// The rule the whole stage exists for. An offer sent while the work is still with the
    /// candidate is an offer made on evidence nobody has read — and it cannot be checked on
    /// either aggregate, because the application knows its own state machine and nothing about
    /// exercises, while the exercise knows nothing about offers.
    /// </remarks>
    [Fact]
    public async Task An_offer_is_refused_while_an_exercise_is_still_out()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing",
            "Route four depots and explain the trade.", db.Clock.Today.AddDays(7));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Recruitment.MoveApplicationAsync(
                application, ApplicationStatus.Offered));

        Assert.Contains("nobody has marked", refused.Message);
    }

    /// <summary>
    /// Once it is marked, the offer goes through.
    /// </summary>
    /// <remarks>
    /// The other half. A gate that could not be opened would be a stage people worked around
    /// by not setting exercises.
    /// </remarks>
    [Fact]
    public async Task Once_it_is_marked_the_offer_goes_through()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing",
            "Route four depots.", db.Clock.Today.AddDays(7));

        await module.Assessments.HandedInAsync(set.Id, "https://example.test/work");
        await module.Assessments.MarkAsync(
            set.Id, Recommendation.Yes, "Sound routing, thin on the write-up.");

        await module.Recruitment.MoveApplicationAsync(application, ApplicationStatus.Offered);
    }

    /// <summary>
    /// Calling an exercise off also opens the gate.
    /// </summary>
    /// <remarks>
    /// The route out for an exercise nobody handed in. Without it, a candidate who never
    /// submitted could never be offered anything — so the gate would have to be bypassed, and
    /// a gate people bypass is worse than none.
    /// </remarks>
    [Fact]
    public async Task Calling_it_off_also_opens_the_gate()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing",
            "Route four depots.", db.Clock.Today.AddDays(7));

        await module.Assessments.CancelAsync(set.Id, "They withdrew from the exercise.");

        await module.Recruitment.MoveApplicationAsync(application, ApplicationStatus.Offered);
    }

    /// <summary>
    /// An offer with no exercise ever set is not refused.
    /// </summary>
    /// <remarks>
    /// Not every post needs one. A gate that demanded an exercise would turn a decision into a
    /// box to tick, and the ticking would be done without reading anything.
    /// </remarks>
    [Fact]
    public async Task An_offer_with_no_exercise_at_all_is_not_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        await module.Recruitment.MoveApplicationAsync(application, ApplicationStatus.Offered);
    }

    /// <summary>
    /// Two live exercises on one application are refused.
    /// </summary>
    /// <remarks>
    /// Either a mistake or two people working on the same application without knowing. Both
    /// are better answered by a refusal naming the first than by a second row that makes the
    /// offer gate ambiguous about which exercise it is waiting for.
    /// </remarks>
    [Fact]
    public async Task A_second_live_exercise_on_one_application_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "First", "Do this.",
            db.Clock.Today.AddDays(7));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Assessments.SetAsync(
                application, AssessmentKind.WrittenTest, "Second", "Do this too.",
                db.Clock.Today.AddDays(7)));

        Assert.Contains("already an exercise out", refused.Message);
    }

    /// <summary>
    /// Marking something nobody handed in is refused.
    /// </summary>
    /// <remarks>
    /// There is nothing to have marked. An exercise that was not submitted is called off with
    /// a reason, which is a different and more honest record than a mark on absent work.
    /// </remarks>
    [Fact]
    public async Task Marking_something_nobody_handed_in_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing", "Route four depots.",
            db.Clock.Today.AddDays(7));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => module.Assessments.MarkAsync(set.Id, Recommendation.No, "Nothing arrived."));

        Assert.Contains("nothing to mark", refused.Message);
    }

    /// <summary>
    /// A mark with no reasoning behind it is refused.
    /// </summary>
    /// <remarks>
    /// A recommendation on its own is a verdict nobody can check, and this record is what
    /// somebody reads when a candidate asks why. The sentence is the useful half.
    /// </remarks>
    [Fact]
    public async Task A_mark_with_no_reasoning_is_refused()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing", "Route four depots.",
            db.Clock.Today.AddDays(7));

        await module.Assessments.HandedInAsync(set.Id, null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => module.Assessments.MarkAsync(set.Id, Recommendation.Yes, "   "));
    }

    /// <summary>
    /// Work handed in after the deadline is still accepted.
    /// </summary>
    /// <remarks>
    /// Somebody who worked late and handed in on Monday morning has done the exercise, and a
    /// system that refused the submission would throw away the only evidence there is —
    /// leaving whoever decides with a blank row and a harder conversation. That it was late is
    /// visible from the two dates, which is where that judgement belongs.
    /// </remarks>
    [Fact]
    public async Task Work_handed_in_late_is_still_accepted()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing", "Route four depots.",
            db.Clock.Today.AddDays(1));

        Assert.False(set.IsLateOn(db.Clock.Today));

        db.Clock.Advance(TimeSpan.FromDays(4));

        Assert.True(set.IsLateOn(db.Clock.Today));

        await module.Assessments.HandedInAsync(set.Id, "https://example.test/late");

        Assert.Equal(AssessmentStatus.Submitted, set.Status);
    }

    /// <summary>A deadline may be pushed out, never pulled in.</summary>
    /// <remarks>
    /// Moving one closer after the fact would make somebody late retrospectively, which is a
    /// judgement about a person made by an accident of data entry.
    /// </remarks>
    [Fact]
    public async Task A_deadline_moves_out_and_never_in()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        db.Clock.Now = Now;
        await using var module = new Module(db);

        var application = await module.AnApplicationAtInterview();

        var set = await module.Assessments.SetAsync(
            application, AssessmentKind.TakeHome, "Depot routing", "Route four depots.",
            db.Clock.Today.AddDays(7));

        var original = set.DueBy;

        await module.Assessments.DueLaterAsync(set.Id, original.AddDays(-3));
        Assert.Equal(original, set.DueBy);

        await module.Assessments.DueLaterAsync(set.Id, original.AddDays(3));
        Assert.Equal(original.AddDays(3), set.DueBy);
    }

    /// <summary>The exercise stage is not a one-way door.</summary>
    /// <remarks>
    /// An exercise that raises a question worth a second conversation is the ordinary reason to
    /// set one. A stage nobody could leave except forwards would be worked around by not using
    /// it at all.
    /// </remarks>
    [Fact]
    public void The_exercise_stage_can_go_back_to_interviewing()
    {
        var allowed = JobApplication.NextFrom(ApplicationStatus.Assessing);

        Assert.Contains(ApplicationStatus.Interviewing, allowed);
        Assert.Contains(ApplicationStatus.Offered, allowed);
    }

    /// <summary>An application at the exercise stage is still live.</summary>
    /// <remarks>
    /// Forgetting this would have taken everybody sitting an exercise off every list of people
    /// still in the running — which is the one list the hiring screens are read for.
    /// </remarks>
    [Fact]
    public void An_application_can_be_moved_to_the_exercise_stage_from_interviewing()
    {
        Assert.Contains(
            ApplicationStatus.Assessing,
            JobApplication.NextFrom(ApplicationStatus.Interviewing));

        // And interviewing keeps its direct route to an offer, since not every post needs one.
        Assert.Contains(
            ApplicationStatus.Offered,
            JobApplication.NextFrom(ApplicationStatus.Interviewing));
    }

    /// <summary>
    /// Just enough of the hiring chain to have somebody at interview stage.
    /// </summary>
    /// <remarks>
    /// The aggregates are created directly rather than driven through RecruitmentService,
    /// which needs a raiser, a department and an approval chain to get a requisition as far as
    /// advertised. None of that is what these tests are about, and a fixture that built all of
    /// it would fail for reasons in another module.
    /// </remarks>
    private sealed class Module(DatabaseFixture db) : IAsyncDisposable
    {
        private readonly TestDbContext _context = db.NewContext();

        private RecruitmentRepository Repository => new(_context);

        public AssessmentService Assessments => new(Repository, db.Clock);

        public RecruitmentService Recruitment => new(
            Repository,
            new JiranisokoTech.Infrastructure.People.PeopleRepository(_context),
            new NoCvStore(),
            db.Clock);

        /// <summary>Somebody at interview stage, which is where an exercise is set.</summary>
        public async Task<Guid> AnApplicationAtInterview()
        {
            /*
             * A real requisition, because postings carry a foreign key to one. Passing a
             * fresh Guid instead failed on SQLite's FOREIGN KEY constraint, which is the
             * test database behaving exactly like PostgreSQL will.
             */
            var raiser = JiranisokoTech.Domain.People.Employee.Hire(
                "Otieno Barasa", new DateOnly(2024, 3, 4));
            _context.Employees.Add(raiser);
            await _context.SaveChangesAsync();

            var requisition = JobRequisition.Raise(
                "Delivery engineer", null, 1, "Depot work needs another pair of hands.",
                raiser.Id);
            _context.Requisitions.Add(requisition);

            var candidate = Candidate.Of("Grace Wanjiru", "grace@example.test", null, db.Clock.Now);
            _context.Candidates.Add(candidate);
            await _context.SaveChangesAsync();

            var posting = JobPosting.Draft(
                requisition.Id, "Delivery engineer", "Depot work",
                "A longer description.", "Nairobi", "depot-work");
            _context.Postings.Add(posting);
            await _context.SaveChangesAsync();

            var application = JobApplication.Receive(posting.Id, candidate.Id, db.Clock.Now);
            application.MoveTo(ApplicationStatus.Screening, db.Clock.Now);
            application.MoveTo(ApplicationStatus.Interviewing, db.Clock.Now);

            _context.Applications.Add(application);
            await _context.SaveChangesAsync();

            return application.Id;
        }

        public async ValueTask DisposeAsync() => await _context.DisposeAsync();

        /// <summary>A CV store that keeps nothing, because no test here uploads one.</summary>
        private sealed class NoCvStore : JiranisokoTech.Application.Recruitment.ICvStore
        {
            public Task<string> SaveAsync(
                Stream contents, string fileName, CancellationToken cancellationToken = default) =>
                Task.FromResult("stored");

            public Task<Stream?> OpenAsync(
                string storedName, CancellationToken cancellationToken = default) =>
                Task.FromResult<Stream?>(null);

            public Task DeleteAsync(
                string storedName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

        }
    }
}
