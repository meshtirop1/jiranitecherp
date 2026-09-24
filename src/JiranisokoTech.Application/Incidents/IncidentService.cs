using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Application.Work;
using JiranisokoTech.Domain.Incidents;
using JiranisokoTech.Domain.Work;

namespace JiranisokoTech.Application.Incidents;

/// <summary>What incidents and their reviews need read and written.</summary>
public interface IIncidentRepository
{
    Task<Incident?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Incident?> ByNumberAsync(int number, CancellationToken cancellationToken = default);

    /// <summary>The highest number given out so far.</summary>
    Task<int> LastNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>The review of one incident, if anybody has started it.</summary>
    Task<Postmortem?> ReviewForAsync(
        Guid incidentId, CancellationToken cancellationToken = default);

    void Add(Incident incident);

    void Add(Postmortem review);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Running an incident, and writing down what it taught the firm.
/// </summary>
/// <remarks>
/// Sections 27 and 69 together, because they are one workflow and splitting them into two
/// services would mean the moment an incident is resolved — the only moment anybody is ever
/// going to start a review — is handled somewhere that cannot start one.
///
/// <b>Nothing here pages, alerts or telephones anybody.</b> It records. The firm's on-call
/// arrangements are whatever they are, and a button in an ERP that looked like it woke somebody
/// up and did not would be the most dangerous control in this application.
///
/// <b>Every write that changes the shape of an incident writes its own timeline line.</b> Not
/// as a convenience: a timeline assembled by asking people to remember is the one artefact of
/// an incident that is always missing, and the only version that ever gets written is the one
/// the system wrote while the incident was happening.
/// </remarks>
public sealed class IncidentService(
    IIncidentRepository incidents, IWorkRepository work, IClock clock)
{
    /// <summary>
    /// Say that something is wrong.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Everything except the title and how bad it is can be filled in
    /// afterwards, because the alternative is a form somebody abandons at two in the morning —
    /// and an incident nobody raised is an incident with no timeline, which is the only thing
    /// this section exists to produce.
    /// </remarks>
    public async Task<Incident> ReportAsync(
        string title,
        IncidentSeverity severity,
        DateTimeOffset startedAt,
        Guid byId,
        string? affects = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;

        if (startedAt > now)
        {
            throw new ArgumentException(
                "It cannot have started in the future.", nameof(startedAt));
        }

        var number = await incidents.LastNumberAsync(cancellationToken) + 1;

        var incident = Incident.Report(
            number, title, severity, startedAt, now, byId, affects);

        /*
         * The first line of every timeline, written by the system rather than asked for. An
         * incident whose timeline begins forty minutes in, with somebody's first note, has lost
         * the part a review most wants: what was known at the start.
         */
        incident.Note(
            $"Reported as {severity}. {Describe(startedAt, now)}",
            NoteKind.Observation,
            byId,
            now,
            now);

        incidents.Add(incident);
        await incidents.SaveAsync(cancellationToken);

        return incident;
    }

    public async Task NoteAsync(
        Guid incidentId,
        string text,
        NoteKind kind,
        DateTimeOffset at,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Note(text, kind, byId, at, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>Put somebody in charge.</summary>
    public async Task LeadAsync(
        Guid incidentId, Guid leadId, Guid byId, CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.LedBy(leadId, byId, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task ReclassifyAsync(
        Guid incidentId,
        IncidentSeverity severity,
        string why,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Reclassify(severity, why, byId, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>Sharpen the title and what it affects, which both change as people learn.</summary>
    public async Task DescribeAsync(
        Guid incidentId,
        string title,
        string? affects,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Retitle(title);
        incident.Affecting(affects);

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>Correct when it actually started.</summary>
    public async Task StartedAtAsync(
        Guid incidentId,
        DateTimeOffset at,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.StartedAtActually(at, byId, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task MitigateAsync(
        Guid incidentId,
        string how,
        DateTimeOffset at,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Mitigate(how, byId, at, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task ResolveAsync(
        Guid incidentId,
        string cause,
        DateTimeOffset at,
        Guid byId,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Resolve(cause, byId, at, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task ReopenAsync(
        Guid incidentId, string why, Guid byId, CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        incident.Reopen(why, byId, clock.Now);

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Start the review, or return the one already going.
    /// </summary>
    /// <remarks>
    /// Idempotent, because two people opening the review page at once is the ordinary case and
    /// two reviews of one incident is the thing the unique index refuses. Refused before the
    /// incident is resolved: a review written while the cause is still unknown is a guess, and a
    /// guess in a document headed "why it was possible" is believed for years.
    /// </remarks>
    public async Task<Postmortem> ReviewAsync(
        Guid incidentId, Guid byId, CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);

        if (await incidents.ReviewForAsync(incidentId, cancellationToken) is { } already)
        {
            return already;
        }

        if (!incident.IsOver)
        {
            throw new InvalidOperationException(
                "This incident is not resolved yet. A review written while the cause is still "
                + "unknown records a guess as a finding.");
        }

        var review = Postmortem.Begin(incidentId, byId, clock.Now);

        incidents.Add(review);
        await incidents.SaveAsync(cancellationToken);

        return review;
    }

    public async Task WriteReviewAsync(
        Guid incidentId,
        string whatHappened,
        string whyItWasPossible,
        string howItWasNoticed,
        string whatWouldHaveCaughtItSooner,
        CancellationToken cancellationToken = default)
    {
        var review = await RequiredReview(incidentId, cancellationToken);

        review.Write(
            whatHappened, whyItWasPossible, howItWasNoticed, whatWouldHaveCaughtItSooner);

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Agree to do something, which puts it on the board.
    /// </summary>
    /// <remarks>
    /// The work item is real and ordinary: it appears on the board, it can be assigned and
    /// estimated and argued about like anything else, and it competes for the same time as
    /// everything else. That competition is the decision the firm is actually making, and a
    /// checklist inside a document is a way of not making it.
    ///
    /// Its detail says which incident it came from, in words, because six weeks later somebody
    /// picking it up needs to know why it is worth doing — and "agreed in the review of incident
    /// 14" is the sentence that answers that.
    /// </remarks>
    public async Task<CorrectiveAction> ActAsync(
        Guid incidentId,
        string title,
        Guid raisedById,
        CancellationToken cancellationToken = default)
    {
        var incident = await Required(incidentId, cancellationToken);
        var review = await RequiredReview(incidentId, cancellationToken);

        var number = await work.LastNumberAsync(cancellationToken) + 1;
        var item = WorkItem.Raise(number, title, raisedById, priority: Priority.High);

        item.Describe(
            $"Agreed in the review of incident {incident.Number}: {incident.Title}.");

        work.Add(item);

        var action = review.Act(title, item.Id, number, clock.Now);

        /*
         * One save, through the incident repository, and it covers the work item too — both
         * repositories are the same DbContext. Saving them separately would leave a review
         * pointing at a work item that failed to save, which is a link to nothing on the one
         * screen that exists to prove the firm did something.
         */
        await incidents.SaveAsync(cancellationToken);

        return action;
    }

    public async Task DropActionAsync(
        Guid incidentId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var review = await RequiredReview(incidentId, cancellationToken);

        review.Drop(actionId);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task AgreeReviewAsync(
        Guid incidentId,
        Guid byId,
        string? nothingToDoBecause = null,
        CancellationToken cancellationToken = default)
    {
        var review = await RequiredReview(incidentId, cancellationToken);

        review.Agree(byId, clock.Now, nothingToDoBecause);

        await incidents.SaveAsync(cancellationToken);
    }

    public async Task ReopenReviewAsync(
        Guid incidentId, CancellationToken cancellationToken = default)
    {
        var review = await RequiredReview(incidentId, cancellationToken);

        review.Reopen();

        await incidents.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The sentence that goes on the first timeline line.
    /// </summary>
    /// <remarks>
    /// It names the gap between when it started and when somebody noticed, in words, at the
    /// moment somebody is best placed to correct it. A number in a column marked "time to
    /// detect" is looked at once a quarter; a line saying "already going for about 40 minutes"
    /// is read by whoever is running the incident, who is the only person who can tell whether
    /// it is right.
    /// </remarks>
    private static string Describe(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var gap = now - startedAt;

        if (gap < TimeSpan.FromMinutes(2))
        {
            return "Noticed as it began.";
        }

        /*
         * Pluralised properly, which is not fussiness. The first line of every timeline is read
         * by whoever is running the incident and quoted into the review afterwards, and "about 1
         * hours" is the sort of thing that makes a reader trust nothing else on the page. It
         * said exactly that until somebody opened the screen and looked at it.
         */
        var said = gap < TimeSpan.FromHours(1)
            ? Count((int)gap.TotalMinutes, "minute")
            : gap < TimeSpan.FromDays(1)
                ? Count(Rounded(gap.TotalHours), "hour")
                : Count(Rounded(gap.TotalDays), "day");

        return $"Already going for about {said} before anybody noticed.";
    }

    private static string Count(int how, string thing) =>
        how == 1 ? $"one {thing}" : $"{how} {thing}s";

    /// <summary>
    /// Rounded the way a person rounds, which is not the way .NET does by default.
    /// </summary>
    /// <remarks>
    /// <c>Math.Round(2.5)</c> is 2, because the default is to round a midpoint to the even
    /// number. Two and a half hours reported as "about 2 hours" understates how long something
    /// was broken, which is the wrong direction to be wrong in on this particular line.
    /// </remarks>
    private static int Rounded(double how) =>
        (int)Math.Round(how, MidpointRounding.AwayFromZero);

    private async Task<Incident> Required(Guid id, CancellationToken cancellationToken) =>
        await incidents.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That incident no longer exists.");

    private async Task<Postmortem> RequiredReview(
        Guid incidentId, CancellationToken cancellationToken) =>
        await incidents.ReviewForAsync(incidentId, cancellationToken)
        ?? throw new InvalidOperationException("That incident has no review yet.");
}
