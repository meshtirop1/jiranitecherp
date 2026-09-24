using System.Globalization;
using System.Text.Json;
using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Api;
using JiranisokoTech.Domain.Approvals;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Contracts;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Settings;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Engineering;
using JiranisokoTech.Domain.Renewals;
using JiranisokoTech.Domain.Integrations;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JiranisokoTech.Infrastructure.Persistence;

/// <summary>
/// The database, and the two things that must happen inside every transaction.
/// </summary>
/// <remarks>
/// Saving here does three things atomically: it writes the change, it records
/// what changed in the audit trail, and it writes any domain events the entities
/// raised into the outbox. All three commit together or none of them do, which
/// is the whole point — a change with no audit row is unexplainable, and an
/// event with no change is a lie.
/// </remarks>
public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    IClock clock,
    ICurrentUser currentUser)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Employee> Employees => Set<Employee>();

    /// <summary>
    /// The teams, which are not departments.
    /// </summary>
    /// <remarks>
    /// A department is where somebody sits and a team is what they are working on. Keeping them
    /// as separate tables is what lets a team cross departments without taking anybody out of
    /// the one that answers for them.
    /// </remarks>
    public DbSet<Team> Teams => Set<Team>();

    /// <summary>
    /// What people are trying to achieve, and the rounds of reviews they are discussed in.
    /// </summary>
    /// <remarks>
    /// Two sets and nothing joining them, deliberately: a rating is never computed from goal
    /// outcomes. See the note on the service.
    /// </remarks>
    public DbSet<Domain.Performance.Goal> Goals => Set<Domain.Performance.Goal>();

    public DbSet<Domain.Performance.ReviewCycle> ReviewCycles =>
        Set<Domain.Performance.ReviewCycle>();

    /// <summary>
    /// What still has to happen when somebody leaves.
    /// </summary>
    /// <remarks>
    /// A checklist rather than an automation. See Offboarding for why closing an account
    /// on a recorded date is the wrong default.
    /// </remarks>
    public DbSet<Offboarding> Offboardings => Set<Offboarding>();

    /// <summary>
    /// What has to happen before somebody starts, and what they were given.
    /// </summary>
    /// <remarks>
    /// Section 8, and deliberately the same shape as Offboardings at the other end. The
    /// equipment recorded here is what the leaver's checklist asks back, which is the loop that
    /// was open: a laptop handed over on day one and written down nowhere is a laptop nobody
    /// misses until the audit.
    /// </remarks>
    public DbSet<Onboarding> Onboardings => Set<Onboarding>();

    /// <summary>What the firm offered somebody, and what they said.</summary>
    public DbSet<Offer> Offers => Set<Offer>();

    /// <summary>
    /// Everything the firm owns, and where each thing is.
    /// </summary>
    /// <remarks>
    /// Section 15, and it replaced two lists that could not agree: what a joiner was handed and
    /// what a leaver had to give back, neither of which could answer where a particular laptop
    /// was. Both screens are now views onto this.
    /// </remarks>
    public DbSet<Domain.Assets.Asset> Assets => Set<Domain.Assets.Asset>();

    /// <summary>
    /// What the firm runs, and what it runs on.
    /// </summary>
    /// <remarks>
    /// Section 14, and a register rather than a control plane: every row is something a person
    /// typed. Nothing reads a cloud account, a DNS zone or a certificate — a catalogue that
    /// claimed to would be believed and would go quietly wrong the first time a credential
    /// expired.
    /// </remarks>
    public DbSet<Domain.Platform.Service> Services => Set<Domain.Platform.Service>();

    public DbSet<Domain.Platform.Resource> Resources => Set<Domain.Platform.Resource>();

    /// <summary>
    /// What the firm can turn on and off without deploying.
    /// </summary>
    /// <remarks>
    /// Section 68, and the one table here an application outside this system reads: the flags
    /// are served over the public API. The change history beside them is what an incident's
    /// "what changed just before" was missing — a flag is quick precisely because it leaves no
    /// trace anywhere else.
    /// </remarks>
    public DbSet<Domain.Platform.Flag> Flags => Set<Domain.Platform.Flag>();

    /// <summary>
    /// Things that happened, addressed to one person each.
    /// </summary>
    /// <remarks>
    /// Section 32, and deliberately not a list of things to do — the home page has answered
    /// "what is waiting on you" since section 35, from what the reader may do rather than from
    /// anything stored, and a second answer to one question is two answers that disagree.
    /// </remarks>
    public DbSet<Domain.Notices.Notice> Notices => Set<Domain.Notices.Notice>();

    /// <summary>What each person wants emailed as well as recorded. Section 59.</summary>
    public DbSet<Domain.Notices.NoticeRule> NoticeRules => Set<Domain.Notices.NoticeRule>();

    /// <summary>
    /// The notice board: one row read by many.
    /// </summary>
    /// <remarks>
    /// The opposite shape from a notice, which is one row per person. A firm-wide message written
    /// as notices would cost a row a head, would need every one of them edited to fix a typo, and
    /// would never reach whoever is hired next week.
    /// </remarks>
    public DbSet<Domain.Notices.Announcement> Announcements =>
        Set<Domain.Notices.Announcement>();

    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    public DbSet<Client> Clients => Set<Client>();

    /// <summary>
    /// The firm's suppliers.
    /// </summary>
    /// <remarks>
    /// A separate table from clients rather than one party table with flags, because a company
    /// can be both — Safaricom sells this firm airtime and could buy software from it — and one
    /// row has one status column that would have to say Former and Active at once.
    /// </remarks>
    public DbSet<Domain.Vendors.Vendor> Vendors => Set<Domain.Vendors.Vendor>();

    /// <summary>
    /// Asking to buy something, and committing to a supplier.
    /// </summary>
    /// <remarks>
    /// Two aggregates rather than one with more states, because they are not one-to-one: a
    /// request splits across suppliers, an order combines lines from two requests, and an order
    /// is filled by several deliveries. Receiving is an owned collection on the order, not a
    /// third table of its own.
    /// </remarks>
    public DbSet<Domain.Procurement.PurchaseRequest> PurchaseRequests =>
        Set<Domain.Procurement.PurchaseRequest>();

    public DbSet<Domain.Procurement.PurchaseOrder> PurchaseOrders =>
        Set<Domain.Procurement.PurchaseOrder>();

    public DbSet<Domain.Vendors.VendorContact> VendorContacts =>
        Set<Domain.Vendors.VendorContact>();

    /// <summary>
    /// The exercises candidates are set, and what the marker made of them.
    /// </summary>
    /// <remarks>
    /// Its own table rather than a fourth InterviewKind. An interview is a conversation at a
    /// time with people in the room; an exercise is work done unattended against a deadline,
    /// and one table for both would put a panel and a scheduled time on rows that have
    /// neither.
    /// </remarks>
    public DbSet<TechnicalAssessment> Assessments => Set<TechnicalAssessment>();

    /// <summary>
    /// The chart of accounts: what money is classified as, and nothing about how much.
    /// </summary>
    /// <remarks>
    /// Accounts are retired rather than deleted, and none of them holds a balance. There is no
    /// journal either, and that is the decision of section 18 rather than an omission: a
    /// stored balance is a number that can disagree with the documents it was added up from,
    /// and on the day it does nobody can tell which is wrong. Every figure in the financial
    /// report is summed from the invoices, claims and charges themselves.
    /// </remarks>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>Costs that fall due on a timetable, and the charges they have raised.</summary>
    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();

    /// <summary>
    /// Notices already given about approaching deadlines.
    /// </summary>
    /// <remarks>
    /// Section 42. The scheduler and its run history existed and two reminder jobs were
    /// registered, but nothing recorded that a notice had gone out — so both jobs re-sent the
    /// same warning every morning until the deadline passed, in flat contradiction of the
    /// contract IRecurringJob states in as many words. This is the row that makes the second
    /// run of the day find something.
    /// </remarks>
    public DbSet<Reminder> Reminders => Set<Reminder>();

    /// <summary>
    /// Builds of commits, as the hosts reported them.
    /// </summary>
    /// <remarks>
    /// Section 13. Nothing here runs a build; this is a mirror, like commits and pull
    /// requests, and it exists because section 12 could not be finished without it — a task
    /// knew its branch, its commits and its pull request and could not say whether any of it
    /// had built.
    /// </remarks>
    public DbSet<Build> Builds => Set<Build>();

    /// <summary>What reached which environment, and whether it landed.</summary>
    public DbSet<Deployment> Deployments => Set<Deployment>();

    /// <summary>The people at a client, who are not the client.</summary>
    public DbSet<Contact> Contacts => Set<Contact>();

    /// <summary>
    /// Work the firm might be paid for, at whatever stage it has reached.
    /// </summary>
    /// <remarks>
    /// A lead and an opportunity are one thing here. Modelling them apart needs a conversion
    /// step, and every conversion step loses the history — see Opportunity.
    /// </remarks>
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();

    public DbSet<Contract> Contracts => Set<Contract>();

    /// <summary>
    /// The paper the firm has signed with people who are not clients.
    /// </summary>
    /// <remarks>
    /// Section 17's second half: employment contracts, vendor agreements and NDAs. Its own table
    /// rather than more rows in contracts, because a client contract carries a value and is what
    /// invoices are raised against, and this paper carries obligations and no money.
    /// </remarks>
    public DbSet<Agreement> Agreements => Set<Agreement>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<ExpenseClaim> Expenses => Set<ExpenseClaim>();

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    public DbSet<LeaveRequest> Leave => Set<LeaveRequest>();

    /// <summary>
    /// The public holidays, which are how much leave a week off actually costs.
    /// </summary>
    public DbSet<Holiday> Holidays => Set<Holiday>();

    /// <summary>
    /// What the firm has planned that belongs to nothing else.
    /// </summary>
    /// <remarks>
    /// The only storage section 33 adds. Everything else the calendar shows is already recorded
    /// somewhere, and is read where it lives rather than copied into a projection.
    /// </remarks>
    public DbSet<Domain.Time.Occasion> Occasions => Set<Domain.Time.Occasion>();

    public DbSet<Interview> Interviews => Set<Interview>();

    public DbSet<JobRequisition> Requisitions => Set<JobRequisition>();

    public DbSet<JobPosting> Postings => Set<JobPosting>();

    public DbSet<Candidate> Candidates => Set<Candidate>();

    public DbSet<JobApplication> Applications => Set<JobApplication>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    /// <summary>
    /// The sprints. The backlog is not a set: it is the work with no sprint.
    /// </summary>
    /// <remarks>
    /// A second table for the backlog would need every item to be in exactly one of the two with
    /// nothing enforcing it, and work would end up in both or in neither.
    /// </remarks>
    public DbSet<Sprint> Sprints => Set<Sprint>();

    /// <summary>Which work has to finish before which other work can.</summary>
    public DbSet<WorkItemLink> WorkItemLinks => Set<WorkItemLink>();

    public DbSet<Domain.Engineering.Repository> Repositories =>
        Set<Domain.Engineering.Repository>();

    /// <summary>
    /// Everything a Git host has ever sent, verbatim.
    /// </summary>
    /// <remarks>
    /// Kept rather than pruned, and the reason is not sentiment. The unique
    /// index on the provider's delivery identifier is the only thing standing
    /// between this endpoint and a captured request being posted back later, and
    /// it protects exactly the deliveries still in this table.
    /// </remarks>
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    public DbSet<PullRequest> PullRequests => Set<PullRequest>();

    public DbSet<Commit> Commits => Set<Commit>();

    /// <summary>Which provider login belongs to which member of staff.</summary>
    public DbSet<Contributor> Contributors => Set<Contributor>();

    /// <summary>
    /// Everything that has gone wrong, and what each one taught the firm.
    /// </summary>
    /// <remarks>
    /// Sections 27 and 69. The timeline lives in a table of its own beneath this one and is
    /// append-only by construction — the aggregate exposes no way to change or remove a line —
    /// because a record of what was known when is worthless if it can be tidied up afterwards.
    /// </remarks>
    public DbSet<Domain.Incidents.Incident> Incidents => Set<Domain.Incidents.Incident>();

    public DbSet<Domain.Incidents.Postmortem> Postmortems => Set<Domain.Incidents.Postmortem>();

    /// <summary>
    /// The versions the firm has named, and what happened to each.
    /// </summary>
    /// <remarks>
    /// The one table in the engineering half of this system that nothing writes to on its own.
    /// Repositories, commits, pull requests, builds and deployments all arrive from a host; a
    /// release is a claim a person makes, which is section 67's whole point.
    /// </remarks>
    public DbSet<Release> Releases => Set<Release>();

    /// <summary>
    /// What one currency was worth in another, on a day.
    /// </summary>
    /// <remarks>
    /// Rates are recorded, never fetched, and conversions are computed in reports rather
    /// than stored. See ExchangeRate for why both of those are deliberate.
    /// </remarks>
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    /// <summary>
    /// Notifications queued for systems outside this one.
    /// </summary>
    /// <remarks>
    /// A queue of its own rather than a use of the outbox. The outbox runs this
    /// system's own handlers after a commit; this one waits on third-party servers over
    /// the internet, and one unreachable customer endpoint must not delay the firm's
    /// own email.
    /// </remarks>
    public DbSet<OutboundDelivery> OutboundDeliveries => Set<OutboundDelivery>();

    /// <summary>
    /// The firm's own details. One row, and the key is a constant.
    /// </summary>
    public DbSet<FirmSettings> Settings => Set<FirmSettings>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    /// <summary>
    /// What each scheduled job did, and when.
    /// </summary>
    /// <remarks>
    /// The history is the whole feature. A scheduler with no record of its runs is one
    /// nobody can tell has stopped, and a job that silently stopped three weeks ago is
    /// worse than one that never existed — because the firm has been relying on it.
    /// </remarks>
    public DbSet<Scheduling.JobRun> JobRuns => Set<Scheduling.JobRun>();

    /*
     * Payroll. The runs and the rates are auditable; the payslips deliberately are not — every
     * figure on one is somebody's pay, and the trail is append-only and never pruned. See the
     * remarks on Payslip.
     */
    public DbSet<Domain.Payroll.PayRun> PayRuns => Set<Domain.Payroll.PayRun>();

    public DbSet<Domain.Payroll.Payslip> Payslips => Set<Domain.Payroll.Payslip>();

    public DbSet<Domain.Payroll.StatutoryRates> StatutoryRates =>
        Set<Domain.Payroll.StatutoryRates>();

    public override int SaveChanges() =>
        SaveChangesAsync().GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RefuseToRewriteHistory();

        // Captured before saving, while the change tracker still knows the
        // original values. After SaveChanges they are gone.
        var entries = CaptureAudit();

        CollectDomainEvents();

        AuditEntries.AddRange(entries);

        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Append-only, enforced rather than assumed.
    /// </summary>
    /// <remarks>
    /// Not merely "we do not offer an edit screen". A trail that some future
    /// code path can rewrite is not a trail, and the assurance is worth more
    /// than the convenience it costs.
    /// </remarks>
    private void RefuseToRewriteHistory()
    {
        var tampered = ChangeTracker.Entries<AuditEntry>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (tampered)
        {
            throw new InvalidOperationException(
                "Audit entries are append-only. They cannot be edited or deleted, by anything.");
        }
    }

    /// <summary>
    /// Turn tracked changes into audit entries.
    /// </summary>
    /// <remarks>
    /// Only entities marked <see cref="IAuditable"/>, and only the properties
    /// that actually moved. An entry listing twenty unchanged columns buries the
    /// one that changed, and auditing every table indiscriminately produces a
    /// trail nobody reads.
    /// </remarks>
    private List<AuditEntry> CaptureAudit()
    {
        var now = clock.Now;
        var recorded = new List<AuditEntry>();

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var excluded = ExcludedProperties(entry);

            var (before, after) = entry.State switch
            {
                EntityState.Added => (null, Values(entry, excluded, original: false)),
                EntityState.Deleted => (Values(entry, excluded, original: true), null),
                _ => ChangedValues(entry, excluded),
            };

            // A modification that moved nothing — touching a row and saving the
            // same values — is not an event. Recording it teaches people that
            // most entries mean nothing.
            if (entry.State is EntityState.Modified && (after is null || after.Count == 0))
            {
                continue;
            }

            var subject = entry.Entity.GetType().Name;
            var action = $"{ToSnakeCase(subject)}.{entry.State.ToString().ToLowerInvariant()}";

            recorded.Add(AuditEntry.Record(
                action,
                subject,
                KeyOf(entry),
                now,
                currentUser.Id,
                currentUser.Name,
                before,
                after));
        }

        return recorded;
    }

    private static IReadOnlySet<string> ExcludedProperties(EntityEntry<IAuditable> entry)
    {
        // Read through the interface's static abstract member, so an entity
        // declares its own secrets rather than this class keeping a list of
        // every sensitive column in the system.
        var property = entry.Entity.GetType()
            .GetProperty(nameof(IAuditable.AuditExcludes));

        return property?.GetValue(null) as IReadOnlySet<string> ?? new HashSet<string>();
    }

    private static Dictionary<string, string?> Values(
        EntityEntry<IAuditable> entry,
        IReadOnlySet<string> excluded,
        bool original)
    {
        var values = new Dictionary<string, string?>();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (excluded.Contains(name))
            {
                continue;
            }

            var value = original ? property.OriginalValue : property.CurrentValue;
            values[name] = Stringify(value);
        }

        foreach (var complex in entry.ComplexProperties)
        {
            var name = complex.Metadata.Name;

            if (excluded.Contains(name))
            {
                /*
                 * A snapshot rather than a change, so it says so. "Changed" would be wrong on
                 * every employee ever created — nothing changed, a value was set — and a trail
                 * whose wording is wrong in the commonest case is one people stop reading
                 * carefully.
                 */
                values[name] = Withheld;
                continue;
            }

            foreach (var member in complex.Properties)
            {
                var value = original ? member.OriginalValue : member.CurrentValue;
                values[$"{name}.{member.Metadata.Name}"] = Stringify(value);
            }
        }

        return values;
    }

    /// <summary>
    /// What the trail says about a value it is not allowed to write down.
    /// </summary>
    /// <remarks>
    /// The distinction this whole complex-property change exists for. Excluding a value from
    /// the trail and recording nothing at all are different things, and only the first was
    /// ever intended: the first says this changed and the new value is not kept here, and
    /// the second says nothing happened.
    ///
    /// Before this, an employee's salary terms could be edited and the trail held no entry
    /// whatsoever — not the figures, which is right, but not the act either. Somebody
    /// changed the most disputed field in an ERP and there was no record that anybody had.
    /// </remarks>
    private const string Recorded = "(changed; value not kept in the trail)";

    /// <summary>The same, for a snapshot of a row rather than a change to one.</summary>
    private const string Withheld = "(set; value not kept in the trail)";

    private static (Dictionary<string, string?>? Before, Dictionary<string, string?>? After) ChangedValues(
        EntityEntry<IAuditable> entry,
        IReadOnlySet<string> excluded)
    {
        var before = new Dictionary<string, string?>();
        var after = new Dictionary<string, string?>();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            if (excluded.Contains(name) || !property.IsModified)
            {
                continue;
            }

            var from = Stringify(property.OriginalValue);
            var to = Stringify(property.CurrentValue);

            // EF marks a property modified when it was assigned, even if the
            // value is identical. The trail should record movement, not assignment.
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                continue;
            }

            before[name] = from;
            after[name] = to;
        }

        /*
         * Complex properties, which entry.Properties does not enumerate.
         *
         * This was the fault. PersonalDetails, EmergencyContact and Terms are mapped with
         * ComplexProperty, so every loop in this class walked straight past them — and
         * because a modification with an empty "after" is deliberately skipped as "nothing
         * moved", editing somebody's salary, their phone number or their next of kin left no
         * audit entry at all. Not a redacted one. None. The section 29 row claimed an audit
         * log of every change for weeks while three of the most sensitive columns in the
         * database were outside it, and Employee.AuditExcludes named two of them as
         * deliberately excluded, which was true of the values by accident and of the act by
         * mistake.
         */
        foreach (var complex in entry.ComplexProperties)
        {
            var name = complex.Metadata.Name;

            if (excluded.Contains(name))
            {
                /*
                 * Whether anything inside it moved is decided by comparing the members, the
                 * same way the scalar loop above decides it — so both halves of this method
                 * answer one question one way, and neither depends on what EF's modified flag
                 * happens to mean for a complex member.
                 *
                 * EF 10 does mark them correctly: reassigning an equal value leaves
                 * IsModified false, which was checked rather than assumed. Comparing anyway
                 * costs a string per member on a save that touched the row, and buys not
                 * having to check it again after an upgrade — on a field where a false entry
                 * is worse than a missing one, because the trail is append-only and cannot be
                 * corrected.
                 */
                if (complex.Properties.Any(member => !string.Equals(
                    Stringify(member.OriginalValue),
                    Stringify(member.CurrentValue),
                    StringComparison.Ordinal)))
                {
                    before[name] = Recorded;
                    after[name] = Recorded;
                }

                continue;
            }

            foreach (var member in complex.Properties)
            {
                var from = Stringify(member.OriginalValue);
                var to = Stringify(member.CurrentValue);

                if (string.Equals(from, to, StringComparison.Ordinal))
                {
                    continue;
                }

                before[$"{name}.{member.Metadata.Name}"] = from;
                after[$"{name}.{member.Metadata.Name}"] = to;
            }
        }

        return (before, after);
    }

    private static Guid KeyOf(EntityEntry<IAuditable> entry)
    {
        var key = entry.Metadata.FindPrimaryKey()?.Properties.SingleOrDefault();

        if (key is null)
        {
            return Guid.Empty;
        }

        return entry.Property(key.Name).CurrentValue as Guid? ?? Guid.Empty;
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        DateTimeOffset at => at.ToString("O"),
        DateTime at => at.ToString("O"),
        DateOnly on => on.ToString("O"),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// Put an event on the outbox that no aggregate raised.
    /// </summary>
    /// <remarks>
    /// One caller, and it is meant to stay that way: a successful sign-in from somewhere
    /// an account has not been used before. Signing in is not a change to a business
    /// record, so there is no aggregate to raise it and nothing for CollectDomainEvents to
    /// find — and the alternative, inventing an entity to hold the fact, would put a row
    /// in a table for the sake of the plumbing.
    ///
    /// Serialised the same way as every other event, so the dispatcher cannot tell the
    /// difference and no second code path exists on the way out.
    /// </remarks>
    public void Announce(IDomainEvent domainEvent) =>
        Outbox.Add(new OutboxMessage(
            domainEvent.GetType().Name,
            JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
            domainEvent.OccurredAt));

    /// <summary>
    /// Move every raised domain event onto the outbox, in this transaction.
    /// </summary>
    private void CollectDomainEvents()
    {
        var raised = ChangeTracker.Entries<Entity>()
            .Select(entry => entry.Entity)
            .Where(entity => entity.Events.Count > 0)
            .ToList();

        foreach (var entity in raised)
        {
            foreach (var domainEvent in entity.Events)
            {
                Outbox.Add(new OutboxMessage(
                    domainEvent.GetType().Name,
                    JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
                    domainEvent.OccurredAt));
            }

            // Cleared once written. An entity that stays tracked across two
            // saves would otherwise publish everything it has ever done, twice.
            entity.ClearEvents();
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        OwnedKeysComeFromTheDomain(modelBuilder);

        if (Database.IsSqlite())
        {
            StoreTimestampsAsSortableText(modelBuilder);
        }
    }

    /// <summary>
    /// Tell EF that an owned row's key is never the store's to generate.
    /// </summary>
    /// <remarks>
    /// Worth the words, because getting this wrong is silent. EF's convention
    /// for a Guid primary key is "the store generates it", and it decides
    /// whether a newly discovered dependent is an insert or an update by asking
    /// whether that key still holds its default value. Every id here is a
    /// GUIDv7 the entity gave itself in its constructor, so the key is always
    /// set — and an invoice line added to an invoice that was loaded from the
    /// database was therefore taken for a row that already existed. EF issued
    /// an UPDATE, it matched nothing, and the line was simply never saved.
    ///
    /// Applied to owned types rather than to the whole model on purpose. An
    /// aggregate is added to its DbSet explicitly, which settles the question;
    /// and ASP.NET Identity's own tables do leave their Guid keys at the default
    /// and expect the store to fill them, so a blanket rule here would give
    /// every user the empty GUID.
    /// </remarks>
    private static void OwnedKeysComeFromTheDomain(ModelBuilder modelBuilder)
    {
        var ownedKeys = modelBuilder.Model.GetEntityTypes()
            .Where(entityType => entityType.IsOwned())
            .SelectMany(entityType => entityType.GetKeys())
            .SelectMany(key => key.Properties)
            .Where(property => property.ClrType == typeof(Guid));

        foreach (var property in ownedKeys)
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }

    /// <summary>
    /// Store every timestamp as ISO-8601 text when running on SQLite.
    /// </summary>
    /// <remarks>
    /// SQLite has no date type and refuses to sort a DateTimeOffset at all —
    /// "does not support expressions of type 'DateTimeOffset' in ORDER BY".
    /// Which matters: the outbox dispatcher reads the oldest pending messages
    /// first, and an audit trail is read newest first. Both are ORDER BY on a
    /// timestamp, so without this every such query fails on the provider the
    /// test suite runs on — and the failure would surface as a broken feature
    /// rather than as a mapping problem.
    ///
    /// Round-trip "O" format in UTC sorts lexicographically in exactly the
    /// order it sorts chronologically, because the fields run from most to
    /// least significant and the width is fixed. PostgreSQL keeps its native
    /// timestamptz and is untouched by this.
    /// </remarks>
    private static void StoreTimestampsAsSortableText(ModelBuilder modelBuilder)
    {
        var toText = new ValueConverter<DateTimeOffset, string>(
            value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        var toNullableText = new ValueConverter<DateTimeOffset?, string?>(
            value => value == null
                ? null
                : value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            text => text == null
                ? null
                : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(toText);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(toNullableText);
                }
            }
        }
    }
}
