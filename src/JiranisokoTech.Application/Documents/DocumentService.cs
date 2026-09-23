using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Documents;

namespace JiranisokoTech.Application.Documents;

/// <summary>Where the bytes go. Never the database.</summary>
/// <remarks>
/// Files in a database make every backup the size of every file anybody ever
/// attached, and make a restore an all-or-nothing operation on both. They live
/// on a volume; the row records where.
/// </remarks>
public interface IDocumentStore
{
    Task<string> SaveAsync(
        Stream contents, string originalName, CancellationToken cancellationToken = default);

    Task<Stream?> OpenAsync(string storedName, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storedName, CancellationToken cancellationToken = default);
}

public interface IAttachmentRepository
{
    Task<Attachment?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<Attachment>> ForAsync(
        AttachedTo kind, Guid ownerId, CancellationToken cancellationToken = default);

    Task<bool> OwnerExistsAsync(
        AttachedTo kind, Guid ownerId, CancellationToken cancellationToken = default);

    void Add(Attachment attachment);

    void Remove(Attachment attachment);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What this system will accept as a document.
/// </summary>
/// <remarks>
/// Wider than the CV list, because these are contracts, purchase orders,
/// delivery notes and photographs of receipts, and a receipt arrives as a
/// photograph taken on a phone far more often than as a PDF.
///
/// Still an allow-list. The point is not that these formats are safe to run —
/// nothing here runs anything, and every download is served as an attachment —
/// but that a file which is none of these is not a document somebody meant to
/// attach, and accepting it means storing whatever anybody felt like uploading.
/// </remarks>
public static class Documents
{
    /// <summary>
    /// Ten megabytes.
    /// </summary>
    /// <remarks>
    /// Twice the CV limit, because a photographed multi-page contract is
    /// genuinely bigger than a CV, and still small enough that this cannot
    /// quietly fill a disk. Note that Kestrel's own request limit is separate
    /// and larger; this is the rule anybody is told about.
    /// </remarks>
    public const long MaximumBytes = 10 * 1024 * 1024;

    public static IReadOnlySet<string> Extensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".csv",
            ".xls", ".xlsx", ".ods",
            ".jpg", ".jpeg", ".png", ".heic", ".webp",
        };

    public static bool Accepts(string fileName, long bytes, out string? why)
    {
        if (bytes <= 0)
        {
            why = "That file is empty.";

            return false;
        }

        if (bytes > MaximumBytes)
        {
            why = $"That file is larger than {MaximumBytes / 1024 / 1024}MB. "
                + "A photograph of a receipt does not need to be a full-resolution one.";

            return false;
        }

        if (!Extensions.Contains(Path.GetExtension(fileName)))
        {
            why = "That is not a document this system accepts. A PDF, an office file, "
                + "plain text, or a photograph.";

            return false;
        }

        why = null;

        return true;
    }

    /// <summary>
    /// The permission that governs each kind of attachment.
    /// </summary>
    /// <remarks>
    /// One table, read by both the upload and the download, so the two cannot
    /// disagree. A document inherits the permission of the thing it is attached
    /// to rather than carrying one of its own: a contract attached to a client
    /// is as confidential as the client, and inventing a separate
    /// "documents.view" would mean somebody holding it could read every
    /// client's contracts without being able to open a single client.
    /// </remarks>
    public static string PermissionToSee(AttachedTo kind) => kind switch
    {
        AttachedTo.Client => Permissions.ClientsView,
        AttachedTo.Project => Permissions.ProjectsViewAll,
        AttachedTo.Invoice => Permissions.InvoicesView,
        AttachedTo.ExpenseClaim => Permissions.ExpensesViewAll,
        AttachedTo.Employee => Permissions.EmployeesView,
        AttachedTo.Contract => Permissions.ContractsView,

        // A face, not a personnel file. See the remarks on AttachedTo.Photo for why
        // this is the wide permission where Employee is the narrow one.
        AttachedTo.Photo => Permissions.EmployeesView,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attachment kind."),
    };

    /// <summary>The permission that governs attaching one, which is not always the same.</summary>
    public static string PermissionToAttach(AttachedTo kind) => kind switch
    {
        AttachedTo.Client => Permissions.ClientsManage,
        AttachedTo.Project => Permissions.ProjectsManage,
        AttachedTo.Invoice => Permissions.InvoicesManage,
        AttachedTo.ExpenseClaim => Permissions.ExpensesClaim,
        AttachedTo.Employee => Permissions.EmployeesManage,

        // Attaching the signed copy is part of agreeing the thing, so it is the
        // same permission. Anybody who may put a signature into the record may
        // also have agreed what it signs.
        AttachedTo.Contract => Permissions.ContractsManage,
        AttachedTo.Photo => Permissions.EmployeesManage,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attachment kind."),
    };
}

public sealed class DocumentService(
    IAttachmentRepository attachments,
    IDocumentStore store,
    Abstractions.IClock clock)
{
    /// <summary>
    /// Store the bytes, then record the row.
    /// </summary>
    /// <remarks>
    /// In that order, and it matters which way round. A row written before the
    /// file is saved points at nothing if the save fails; a file saved before
    /// the row fails leaves an orphan on disk that nobody can see and nothing
    /// reads. The second is a wasted few kilobytes, the first is a broken link
    /// in a list somebody is looking at — so the cheap failure is the one this
    /// chooses, and the orphan is cleaned up if the row cannot be written.
    /// </remarks>
    public async Task<Attachment> AttachAsync(
        AttachedTo kind,
        Guid ownerId,
        Stream contents,
        string fileName,
        long sizeBytes,
        Guid? uploadedById,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (!Documents.Accepts(fileName, sizeBytes, out var why))
        {
            throw new InvalidOperationException(why);
        }

        if (!await attachments.OwnerExistsAsync(kind, ownerId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"There is no {kind.ToString().ToLowerInvariant()} with that identifier to attach "
                + "anything to.");
        }

        var stored = await store.SaveAsync(contents, fileName, cancellationToken);

        try
        {
            var attachment = Attachment.Of(
                kind, ownerId, fileName, stored, sizeBytes, uploadedById, clock.Now, note);

            attachments.Add(attachment);
            await attachments.SaveAsync(cancellationToken);

            return attachment;
        }
        catch
        {
            await store.DeleteAsync(stored, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Attach a new version of something already here.
    /// </summary>
    /// <remarks>
    /// The old file is kept and marked superseded rather than replaced. Replacing it would
    /// destroy the evidence of what was agreed before, and the question asked two years
    /// later is "what did we agree in March" — not "what is current".
    ///
    /// The two writes are one save, so a crash between them cannot leave two versions both
    /// claiming to be current. The alternative — save the new one, then mark the old — has a
    /// window in which the list shows two contracts and nobody can tell which the client
    /// signed.
    /// </remarks>
    public async Task<Attachment> AttachVersionAsync(
        Guid supersedes,
        Stream contents,
        string fileName,
        long sizeBytes,
        Guid? uploadedById,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var previous = await attachments.FindAsync(supersedes, cancellationToken)
            ?? throw new InvalidOperationException("There is no such document to replace.");

        if (!previous.IsCurrent)
        {
            throw new InvalidOperationException(
                "That version has already been replaced. Add the new one to whichever version "
                + "is current, so the chain stays a chain rather than a fork.");
        }

        if (!Documents.Accepts(fileName, sizeBytes, out var why))
        {
            throw new InvalidOperationException(why);
        }

        var stored = await store.SaveAsync(contents, fileName, cancellationToken);

        try
        {
            var attachment = Attachment.Of(
                previous.Kind,
                previous.OwnerId,
                fileName,
                stored,
                sizeBytes,
                uploadedById,
                clock.Now,
                note);

            // Carried forward, because a new version of a tagged document is about the same
            // thing — and a version that lost its tags would vanish from every search that
            // found the one before it.
            attachment.Tagged(previous.Tags);

            attachments.Add(attachment);
            previous.SupersededBy(attachment.Id, clock.Now);

            await attachments.SaveAsync(cancellationToken);

            return attachment;
        }
        catch
        {
            await store.DeleteAsync(stored, cancellationToken);

            throw;
        }
    }

    /// <summary>Say what a document is about, for finding it again.</summary>
    public async Task TagAsync(
        Guid attachmentId, string? tags, CancellationToken cancellationToken = default)
    {
        var attachment = await attachments.FindAsync(attachmentId, cancellationToken)
            ?? throw new InvalidOperationException("There is no such document.");

        attachment.Tagged(tags);
        await attachments.SaveAsync(cancellationToken);
    }

    public Task<List<Attachment>> ForAsync(
        AttachedTo kind, Guid ownerId, CancellationToken cancellationToken = default) =>
        attachments.ForAsync(kind, ownerId, cancellationToken);

    /// <summary>
    /// Every version of one document, oldest first.
    /// </summary>
    /// <remarks>
    /// Walked forwards from whichever version was asked for rather than gathered by a shared
    /// identifier, because the chain is what exists — there is no "document" row above the
    /// versions, and inventing one would be a second thing to keep in step with them.
    /// </remarks>
    public async Task<List<Attachment>> VersionsOfAsync(
        Guid attachmentId, CancellationToken cancellationToken = default)
    {
        if (await attachments.FindAsync(attachmentId, cancellationToken) is not { } start)
        {
            return [];
        }

        var chain = new List<Attachment> { start };
        var seen = new HashSet<Guid> { start.Id };
        var walking = start;

        while (walking.SupersededById is { } next && seen.Add(next))
        {
            if (await attachments.FindAsync(next, cancellationToken) is not { } newer)
            {
                break;
            }

            chain.Add(newer);
            walking = newer;
        }

        return chain;
    }

    public Task<Attachment?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        attachments.FindAsync(id, cancellationToken);

    public Task<Stream?> OpenAsync(
        Attachment attachment, CancellationToken cancellationToken = default) =>
        store.OpenAsync(attachment.StoredName, cancellationToken);

    /// <summary>
    /// Remove an attachment, row first.
    /// </summary>
    /// <remarks>
    /// The other way round from attaching, and for the same reason: whichever
    /// half fails, the state left behind should be a file nobody can reach
    /// rather than a row pointing at nothing.
    /// </remarks>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await attachments.FindAsync(id, cancellationToken) is not { } attachment)
        {
            return;
        }

        attachments.Remove(attachment);
        await attachments.SaveAsync(cancellationToken);

        await store.DeleteAsync(attachment.StoredName, cancellationToken);
    }
}
