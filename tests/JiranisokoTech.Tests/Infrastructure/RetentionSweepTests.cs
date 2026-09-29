using JiranisokoTech.Application.Documents;
using JiranisokoTech.Application.People;
using JiranisokoTech.Application.Recruitment;
using JiranisokoTech.Application.Settings;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Settings;
using JiranisokoTech.Infrastructure.Assets;
using JiranisokoTech.Infrastructure.Identity;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Recruitment;
using JiranisokoTech.Infrastructure.Scheduling;
using JiranisokoTech.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.Infrastructure;

/// <summary>
/// The retention periods an administrator sets are what the nightly sweep removes — and only that.
/// </summary>
/// <remarks>
/// Section 53. Each test makes its records, moves the clock past the period, runs the real sweep
/// and reads the database back. The clock moves rather than the records being back-dated,
/// because the sweep reads the same clock and back-dating would test a situation the application
/// can never be in.
/// </remarks>
public class RetentionSweepTests
{
    /// <summary>With nothing set, nothing goes, however old it is.</summary>
    [Fact]
    public async Task With_no_period_set_nothing_is_removed()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var (candidate, _) = await ARejectedApplicantAsync(db);

        db.Clock.Now = db.Clock.Now.AddYears(20);

        var said = await Sweep(db).RunAsync();

        await using var read = db.NewContext();
        var stored = await read.Candidates.SingleAsync(one => one.Id == candidate);

        Assert.Null(stored.ForgottenAt);
        Assert.Equal("Grace Wanjiku", stored.FullName);
        Assert.Contains("everything is kept", said);
    }

    /// <summary>
    /// An applicant turned down longer ago than the period is erased, CV and all, and the trail
    /// records the erasure without what was erased.
    /// </summary>
    [Fact]
    public async Task An_applicant_past_the_period_is_forgotten_and_the_trail_keeps_nothing_of_them()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var (candidate, application) = await ARejectedApplicantAsync(db);
        await RetainAsync(db, candidateMonths: 6);

        db.Clock.Now = db.Clock.Now.AddMonths(7);

        var cvs = new RecordingCvStore();
        await Sweep(db, cvs).RunAsync();

        await using var read = db.NewContext();

        var stored = await read.Candidates.SingleAsync(one => one.Id == candidate);
        Assert.NotNull(stored.ForgottenAt);
        Assert.Equal("Former applicant", stored.FullName);
        Assert.DoesNotContain("grace", stored.Email, StringComparison.OrdinalIgnoreCase);

        var applied = await read.Applications.SingleAsync(one => one.Id == application);
        Assert.Null(applied.Note);
        Assert.Null(applied.CvStoredName);
        Assert.Equal(ApplicationStatus.Rejected, applied.Status);
        Assert.Contains("the-cv", cvs.Deleted);

        var trail = await read.AuditEntries.Where(entry => entry.SubjectId == candidate).ToListAsync();
        Assert.Contains(trail, entry => entry.Action == "candidate.forgotten");

        var everything = string.Join(" ", trail.SelectMany(entry =>
            (entry.Before ?? new Dictionary<string, string?>()).Values
                .Concat((entry.After ?? new Dictionary<string, string?>()).Values)));

        Assert.DoesNotContain("Grace", everything);
        Assert.DoesNotContain("grace@", everything);
    }

    /// <summary>Somebody with an application still in progress is left alone.</summary>
    [Fact]
    public async Task An_applicant_still_in_the_running_is_kept()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var (candidate, application) = await ARejectedApplicantAsync(db, reject: false);
        await RetainAsync(db, candidateMonths: 6);

        db.Clock.Now = db.Clock.Now.AddYears(2);
        await Sweep(db).RunAsync();

        await using var read = db.NewContext();
        Assert.Null((await read.Candidates.SingleAsync(one => one.Id == candidate)).ForgottenAt);
        _ = application;
    }

    /// <summary>
    /// A leaver's documents go after the period; somebody who left recently keeps theirs.
    /// </summary>
    [Fact]
    public async Task Leavers_documents_go_after_the_period_and_not_before()
    {
        await using var db = await DatabaseFixture.CreateAsync();
        var people = People(db);

        var long_gone = await people.HireAsync("Long Gone", DateOnly.FromDateTime(db.Clock.Now.UtcDateTime).AddYears(-3));
        var recent = await people.HireAsync("Recently Left", DateOnly.FromDateTime(db.Clock.Now.UtcDateTime).AddYears(-3));
        await people.StartAsync(long_gone.Id);
        await people.StartAsync(recent.Id);
        await people.RecordLeavingAsync(long_gone.Id, db.Clock.Today, "Moved on");

        await using (var write = db.NewContext())
        {
            write.Attachments.Add(Attachment.Of(
                AttachedTo.Employee, long_gone.Id, "contract.pdf", "old-file", 10, null, db.Clock.Now));
            write.Attachments.Add(Attachment.Of(
                AttachedTo.Photo, long_gone.Id, "face.jpg", "old-face", 10, null, db.Clock.Now));
            await write.SaveChangesAsync();
        }

        await RetainAsync(db, leaverDocumentYears: 5);

        // Six years on for the first; the second leaves now, five years from the first, so is
        // within the period when the sweep runs.
        db.Clock.Now = db.Clock.Now.AddYears(6);
        await People(db).RecordLeavingAsync(recent.Id, db.Clock.Today, "Moved on");

        await using (var write = db.NewContext())
        {
            write.Attachments.Add(Attachment.Of(
                AttachedTo.Employee, recent.Id, "letter.pdf", "recent-file", 10, null, db.Clock.Now));
            await write.SaveChangesAsync();
        }

        var files = new RecordingDocumentStore();
        await Sweep(db, documents: files).RunAsync();

        await using var read = db.NewContext();
        Assert.False(await read.Attachments.AnyAsync(one => one.OwnerId == long_gone.Id));
        Assert.True(await read.Attachments.AnyAsync(one => one.OwnerId == recent.Id));
        Assert.Equal(["old-face", "old-file"], files.Deleted.Order());
    }

    /// <summary>
    /// A withdrawn account's login details go after the period; the row stays for the trail.
    /// </summary>
    [Fact]
    public async Task A_withdrawn_account_is_forgotten_after_the_period()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        var account = new ApplicationUser("leaver@jiranisokotech.co.ke", "Leaving Person")
        {
            IsActive = false,
            WithdrawnAt = db.Clock.Now,
        };

        await using (var write = db.NewContext())
        {
            write.Users.Add(account);
            await write.SaveChangesAsync();
        }

        await RetainAsync(db, withdrawnAccountMonths: 3);

        db.Clock.Now = db.Clock.Now.AddMonths(4);
        await Sweep(db).RunAsync();

        await using var read = db.NewContext();
        var stored = await read.Users.SingleAsync(one => one.Id == account.Id);

        Assert.NotNull(stored.ForgottenAt);
        Assert.Equal("Former user", stored.DisplayName);
        Assert.DoesNotContain("leaver", stored.Email!, StringComparison.OrdinalIgnoreCase);

        var forgotten = await read.AuditEntries.SingleAsync(entry =>
            entry.SubjectId == account.Id && entry.Action == "application_user.forgotten");

        Assert.DoesNotContain("leaver@", string.Join(" ", forgotten.Before!.Values));
    }

    /// <summary>
    /// The trail loses what is older than the period, and says so on itself first.
    /// </summary>
    [Fact]
    public async Task Old_audit_entries_are_removed_and_the_removal_is_recorded()
    {
        await using var db = await DatabaseFixture.CreateAsync();

        await using (var write = db.NewContext())
        {
            write.AuditEntries.Add(AuditEntry.Record(
                "thing.happened", "Thing", Guid.NewGuid(), db.Clock.Now.AddYears(-8)));
            write.AuditEntries.Add(AuditEntry.Record(
                "thing.happened", "Thing", Guid.NewGuid(), db.Clock.Now.AddYears(-1)));
            await write.SaveChangesAsync();
        }

        await RetainAsync(db, auditYears: 7);
        var said = await Sweep(db).RunAsync();

        await using var read = db.NewContext();
        var left = await read.AuditEntries.ToListAsync();

        Assert.DoesNotContain(left, entry => entry.OccurredAt < db.Clock.Now.AddYears(-7));
        Assert.Contains(left, entry => entry.Action == "thing.happened");
        Assert.Contains(left, entry => entry.Action == "audit.pruned");
        Assert.Contains("1 audit entries removed", said);
    }

    [Theory]
    [InlineData(0, null, null, null)]
    [InlineData(null, 4, null, null)]
    [InlineData(null, null, 0, null)]
    [InlineData(null, null, null, 6)]
    public void A_period_under_its_floor_is_refused(
        int? candidateMonths, int? leaverDocumentYears, int? withdrawnAccountMonths, int? auditYears) =>
        Assert.Throws<InvalidOperationException>(() => FirmSettings.Initial().RetainFor(
            candidateMonths, leaverDocumentYears, withdrawnAccountMonths, auditYears));

    // --- set-up ------------------------------------------------------------

    private static ApplyRetention Sweep(
        DatabaseFixture db, ICvStore? cvs = null, IDocumentStore? documents = null) =>
        new(db.NewContext(), cvs ?? new RecordingCvStore(), documents ?? new RecordingDocumentStore(), db.Clock);

    private static PeopleService People(DatabaseFixture db)
    {
        var context = db.NewContext();

        return new PeopleService(
            new PeopleRepository(context), new AssetRepository(context), db.Clock, new NoAccounts());
    }

    private static async Task RetainAsync(
        DatabaseFixture db,
        int? candidateMonths = null,
        int? leaverDocumentYears = null,
        int? withdrawnAccountMonths = null,
        int? auditYears = null)
    {
        await using var context = db.NewContext();

        await new SettingsService(new SettingsRepository(context)).RetainForAsync(
            candidateMonths, leaverDocumentYears, withdrawnAccountMonths, auditYears);
    }

    /// <summary>An applicant with a CV and a note, turned down now unless asked not to be.</summary>
    private static async Task<(Guid Candidate, Guid Application)> ARejectedApplicantAsync(
        DatabaseFixture db, bool reject = true)
    {
        await using var context = db.NewContext();

        var people = new PeopleService(
            new PeopleRepository(context), new AssetRepository(context), db.Clock, new NoAccounts());
        var raiser = await people.HireAsync("Raiser", db.Clock.Today.AddYears(-1));
        await people.StartAsync(raiser.Id);

        var recruitment = new RecruitmentService(
            new RecruitmentRepository(context), new PeopleRepository(context), new RecordingCvStore(), db.Clock);

        var requisition = await recruitment.RaiseRequisitionAsync(
            "Delivery Engineer", null, 1, "We need somebody.", raiser.Id);
        await recruitment.SubmitAsync(requisition.Id);
        await recruitment.RecordDecisionAsync(requisition.Id, true, null);

        var posting = await recruitment.DraftPostingAsync(
            requisition.Id, "Delivery Engineer", "Build things", "A longer description.",
            slug: $"advert-{Guid.NewGuid():N}"[..20]);
        await recruitment.PublishAsync(posting.Id);

        using var cv = new MemoryStream("a CV"u8.ToArray());

        var application = await recruitment.ApplyAsync(
            posting.Id, "Grace Wanjiku", "grace@example.com",
            note: "I have built three of these.", cv: cv, cvFileName: "grace-cv.pdf");

        if (reject)
        {
            await recruitment.MoveApplicationAsync(
                application.Id, ApplicationStatus.Rejected, "Not enough delivery experience.");
        }

        return (application.CandidateId, application.Id);
    }

    private sealed class RecordingCvStore : ICvStore
    {
        public List<string> Deleted { get; } = [];

        public Task<string> SaveAsync(
            Stream contents, string originalName, CancellationToken cancellationToken = default) =>
            Task.FromResult("the-cv");

        public Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
        {
            Deleted.Add(storedName);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDocumentStore : IDocumentStore
    {
        public List<string> Deleted { get; } = [];

        public Task<string> SaveAsync(
            Stream contents, string originalName, CancellationToken cancellationToken = default) =>
            Task.FromResult("stored");

        public Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);

        public Task DeleteAsync(string storedName, CancellationToken cancellationToken = default)
        {
            Deleted.Add(storedName);

            return Task.CompletedTask;
        }
    }
}
