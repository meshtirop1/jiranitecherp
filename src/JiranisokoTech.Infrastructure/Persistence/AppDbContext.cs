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

    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<ExpenseClaim> Expenses => Set<ExpenseClaim>();

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    public DbSet<LeaveRequest> Leave => Set<LeaveRequest>();

    /// <summary>
    /// The public holidays, which are how much leave a week off actually costs.
    /// </summary>
    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<Interview> Interviews => Set<Interview>();

    public DbSet<JobRequisition> Requisitions => Set<JobRequisition>();

    public DbSet<JobPosting> Postings => Set<JobPosting>();

    public DbSet<Candidate> Candidates => Set<Candidate>();

    public DbSet<JobApplication> Applications => Set<JobApplication>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

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

        return values;
    }

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
