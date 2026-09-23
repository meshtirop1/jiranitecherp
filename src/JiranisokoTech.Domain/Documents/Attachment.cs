using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.Documents;

/// <summary>What a document is attached to.</summary>
/// <remarks>
/// An enum rather than a table of types. The set is small, every member has a
/// permission and a page behind it in code, and a row somebody could add at
/// runtime would be a kind of attachment nothing knows how to authorise.
/// </remarks>
public enum AttachedTo
{
    Client = 1,
    Project = 2,
    Invoice = 3,
    ExpenseClaim = 4,
    Employee = 5,

    /// <summary>
    /// The signed agreement itself.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than a file on the client, because the client record
    /// holds however many contracts the relationship has run through and a pile
    /// of PDFs on the client cannot say which is which. The signature belongs
    /// with the terms it signed.
    /// </remarks>
    Contract = 6,

    /// <summary>
    /// Somebody's photograph, for the staff directory.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than another file on the employee, because the access rule
    /// has to be different and reusing Employee would have made it the same one.
    ///
    /// A personnel file holds a contract with a salary on it, a disciplinary letter, a
    /// scan of a passport, and is deliberately readable only by employees.manage and
    /// the person themselves. A face is not that. It is the thing a colleague looks at
    /// to work out who they are about to meet, and hiding it behind the permission that
    /// guards salaries would mean a directory of grey squares.
    ///
    /// So a photo is readable by anybody who may read the staff list, and is still
    /// replaceable only by whoever keeps the record or the person in it.
    /// </remarks>
    Photo = 7,
}

/// <summary>
/// A file somebody attached to something, and who attached it.
/// </summary>
/// <remarks>
/// One table for every kind of attachment rather than a column on each
/// aggregate. The alternative — client_contract_path, project_brief_path,
/// invoice_po_path — needs a schema change for every new thing anybody wants to
/// keep, and each one holds exactly one file.
///
/// The row records what the file was called when it arrived and what it is
/// called on disk, and those are deliberately different things: see
/// <see cref="StoredName"/>.
/// </remarks>
public sealed class Attachment : Entity, IAuditable
{
    private Attachment()
    {
        FileName = string.Empty;
        StoredName = string.Empty;
    }

    private Attachment(
        AttachedTo kind,
        Guid ownerId,
        string fileName,
        string storedName,
        long sizeBytes,
        Guid? uploadedById,
        DateTimeOffset at,
        string? note)
    {
        Kind = kind;
        OwnerId = ownerId;
        FileName = Required(fileName, nameof(fileName));
        StoredName = Required(storedName, nameof(storedName));
        SizeBytes = sizeBytes > 0
            ? sizeBytes
            : throw new ArgumentOutOfRangeException(
                nameof(sizeBytes), sizeBytes, "An empty file is not a document.");
        UploadedById = uploadedById;
        UploadedAt = at;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        Raise(new DocumentAttached(Id, kind, ownerId, FileName, at));
    }

    public static Attachment Of(
        AttachedTo kind,
        Guid ownerId,
        string fileName,
        string storedName,
        long sizeBytes,
        Guid? uploadedById,
        DateTimeOffset at,
        string? note = null) =>
        new(kind, ownerId, fileName, storedName, sizeBytes, uploadedById, at, note);

    public AttachedTo Kind { get; private init; }

    /// <summary>The client, contract, project, invoice, claim or person this belongs to.</summary>
    /// <remarks>
    /// Not a foreign key, because it points at one of six tables depending on
    /// <see cref="Kind"/>. The trade is real and is made deliberately: the
    /// database cannot stop a row pointing at nothing, so deleting an aggregate
    /// has to take its attachments with it rather than relying on a cascade.
    /// Six nullable foreign keys would let the database enforce it and would
    /// mean a schema change for the seventh kind, and five nulls on every row.
    /// </remarks>
    public Guid OwnerId { get; private init; }

    /// <summary>What the file was called when it arrived.</summary>
    public string FileName { get; private set; }

    /// <summary>
    /// What it is called on disk, which this system chose.
    /// </summary>
    /// <remarks>
    /// Generated, with no relationship to the name it arrived under. That stops
    /// two uploads of "scan.pdf" overwriting each other, stops a name like
    /// "../../appsettings.json" escaping the folder, and means the name on disk
    /// carries nothing about what is in the file.
    /// </remarks>
    public string StoredName { get; private init; }

    public long SizeBytes { get; private init; }

    public Guid? UploadedById { get; private init; }

    public DateTimeOffset UploadedAt { get; private init; }

    /// <summary>What it is, in the words of whoever attached it.</summary>
    public string? Note { get; private set; }

    /// <summary>The size as somebody would say it.</summary>
    public string Size => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} bytes",
        < 1024 * 1024 => $"{SizeBytes / 1024}KB",
        _ => $"{SizeBytes / 1024.0 / 1024.0:0.#}MB",
    };

    public void Describe(string? note) =>
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    /// <summary>
    /// Nothing here is a secret, and what was attached to what is the point.
    /// </summary>
    public static IReadOnlySet<string> AuditExcludes { get; } = new HashSet<string>();

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}

public sealed record DocumentAttached(
    Guid AttachmentId,
    AttachedTo Kind,
    Guid OwnerId,
    string FileName,
    DateTimeOffset At) : DomainEvent;
