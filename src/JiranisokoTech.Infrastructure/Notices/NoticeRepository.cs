using JiranisokoTech.Application.Notices;
using JiranisokoTech.Domain.Notices;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Notices;

/// <summary>The reads and writes the notice centre needs.</summary>
public sealed class NoticeRepository(AppDbContext database) : INoticeRepository
{
    /// <remarks>
    /// Newest first and bounded, because this list is read from the top and nobody scrolls to
    /// the bottom of a year of it.
    /// </remarks>
    public Task<List<Notice>> ForAsync(
        Guid employeeId, int most, CancellationToken cancellationToken = default) =>
        database.Notices
            .AsNoTracking()
            .Where(one => one.ForEmployeeId == employeeId)
            .OrderByDescending(one => one.At)
            .Take(most)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// The number in the navigation, asked on every page load. It is a count over the index on
    /// the person and the read timestamp, which is why that index exists.
    /// </remarks>
    public Task<int> UnreadForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Notices.CountAsync(
            one => one.ForEmployeeId == employeeId && one.ReadAt == null, cancellationToken);

    public Task<Notice?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Notices.FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<Notice>> UnreadListAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.Notices
            .Where(one => one.ForEmployeeId == employeeId && one.ReadAt == null)
            .ToListAsync(cancellationToken);

    public Task<List<NoticeRule>> RulesForAsync(
        Guid employeeId, CancellationToken cancellationToken = default) =>
        database.NoticeRules
            .AsNoTracking()
            .Where(one => one.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public Task<NoticeRule?> RuleForAsync(
        Guid employeeId, NoticeKind kind, CancellationToken cancellationToken = default) =>
        database.NoticeRules.FirstOrDefaultAsync(
            one => one.EmployeeId == employeeId && one.Kind == kind, cancellationToken);

    /// <remarks>
    /// The account's address, never the personal one on the staff record, and only while the
    /// account is active. Both halves are the lesson from the reminder jobs, which were found
    /// mailing the firm's internal lists to people's personal inboxes and going on mailing
    /// leavers.
    /// </remarks>
    public async Task<(string Address, string Name)?> MailboxAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var found = await database.Employees
            .AsNoTracking()
            .Where(employee => employee.Id == employeeId && employee.AccountId != null)
            .Join(
                database.Users.AsNoTracking(),
                employee => employee.AccountId,
                account => account.Id,
                (employee, account) => new
                {
                    employee.FullName,
                    account.Email,
                    account.IsActive,
                })
            .FirstOrDefaultAsync(cancellationToken);

        return found is { IsActive: true, Email: { Length: > 0 } address }
            ? (address, found.FullName)
            : null;
    }

    public void Add(Notice notice) => database.Notices.Add(notice);

    public void Add(NoticeRule rule) => database.NoticeRules.Add(rule);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
