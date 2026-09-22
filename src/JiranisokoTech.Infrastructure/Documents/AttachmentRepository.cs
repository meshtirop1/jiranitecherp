using JiranisokoTech.Application.Documents;
using JiranisokoTech.Domain.Documents;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Documents;

public sealed class AttachmentRepository(AppDbContext database) : IAttachmentRepository
{
    public Task<Attachment?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Attachments.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Attachment>> ForAsync(
        AttachedTo kind, Guid ownerId, CancellationToken cancellationToken = default) =>
        database.Attachments
            .AsNoTracking()
            .Where(one => one.Kind == kind && one.OwnerId == ownerId)
            .OrderByDescending(one => one.UploadedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Does the thing being attached to actually exist?
    /// </summary>
    /// <remarks>
    /// Asked because the owner is not a foreign key — it points at one of five
    /// tables depending on the kind, so the database cannot refuse a row that
    /// points at nothing. This is that check, done in the one place that knows
    /// which table each kind means.
    /// </remarks>
    public Task<bool> OwnerExistsAsync(
        AttachedTo kind, Guid ownerId, CancellationToken cancellationToken = default) =>
        kind switch
        {
            AttachedTo.Client =>
                database.Clients.AnyAsync(one => one.Id == ownerId, cancellationToken),
            AttachedTo.Project =>
                database.Projects.AnyAsync(one => one.Id == ownerId, cancellationToken),
            AttachedTo.Invoice =>
                database.Invoices.AnyAsync(one => one.Id == ownerId, cancellationToken),
            AttachedTo.ExpenseClaim =>
                database.Expenses.AnyAsync(one => one.Id == ownerId, cancellationToken),
            AttachedTo.Employee =>
                database.Employees.AnyAsync(one => one.Id == ownerId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Unknown attachment kind."),
        };

    public void Add(Attachment attachment) => database.Attachments.Add(attachment);

    public void Remove(Attachment attachment) => database.Attachments.Remove(attachment);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
