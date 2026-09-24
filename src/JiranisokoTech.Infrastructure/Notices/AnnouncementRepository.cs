using JiranisokoTech.Application.Notices;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Notices;

/// <summary>The notice board's reads and writes.</summary>
public sealed class AnnouncementRepository(AppDbContext database) : IAnnouncementRepository
{
    public Task<Announcement?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Announcements
            .Include(one => one.Acknowledgements)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    /// <remarks>
    /// Posted ones by when they went up and drafts by when they were written, newest first —
    /// because the person keeping the board is looking either for what they wrote this morning
    /// or for what went out last week, and both are at the top.
    /// </remarks>
    public Task<List<Announcement>> AllAsync(CancellationToken cancellationToken = default) =>
        database.Announcements
            .AsNoTracking()
            .Include(one => one.Acknowledgements)
            .OrderByDescending(one => one.PostedAt ?? one.WrittenAt)
            .ThenByDescending(one => one.Id)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// Firm-wide ones and this person's department, and nothing that has come off the board.
    /// Somebody with no department sees the firm-wide ones only, which is right: a contractor or
    /// somebody yet to be placed has no department's business to read.
    /// </remarks>
    public Task<List<Announcement>> UpForAsync(
        Guid? departmentId, DateOnly today, CancellationToken cancellationToken = default) =>
        Up(today)
            .Where(one => one.DepartmentId == null || one.DepartmentId == departmentId)
            .OrderByDescending(one => one.PostedAt)
            .ThenByDescending(one => one.Id)
            .ToListAsync(cancellationToken);

    public Task<List<Announcement>> OutstandingForAsync(
        Guid employeeId,
        Guid? departmentId,
        DateOnly today,
        CancellationToken cancellationToken = default) =>
        Up(today)
            .Where(one => one.DepartmentId == null || one.DepartmentId == departmentId)
            .Where(one => one.NeedsAcknowledgement)
            .Where(one => !one.Acknowledgements.Any(said => said.EmployeeId == employeeId))
            .OrderBy(one => one.PostedAt)
            .ToListAsync(cancellationToken);

    public void Add(Announcement announcement) => database.Announcements.Add(announcement);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// What is on the board today.
    /// </summary>
    /// <remarks>
    /// Written as a query rather than by filtering <c>IsUpOn</c> in memory, because the board is
    /// read on every page load by everybody: a computed property would mean loading every
    /// announcement the firm has ever made in order to show the three that are up.
    /// </remarks>
    private IQueryable<Announcement> Up(DateOnly today) =>
        database.Announcements
            .AsNoTracking()
            .Include(one => one.Acknowledgements)
            .Where(one => one.State == AnnouncementState.Posted)
            .Where(one => one.ExpiresOn == null || one.ExpiresOn >= today);
}
