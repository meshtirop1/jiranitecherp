using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Identity;

/// <summary>The reads the account screens do.</summary>
public sealed class UserDirectory(AppDbContext database, IClock clock)
{
    public async Task<List<AccountRow>> AllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await database.Users
            .AsNoTracking()
            .OrderBy(user => user.DisplayName)
            .Select(user => new
            {
                user.Id,
                user.DisplayName,
                user.Email,
                user.JobTitle,
                user.IsActive,
                user.EmailConfirmed,
                user.LastSignedInAt,
                user.InvitedAt,
                user.LockoutEnd,
            })
            .ToListAsync(cancellationToken);

        // Roles in one pass rather than a query per row: this list is opened by
        // an administrator looking for one person, and N+1 over the whole staff
        // is a noticeable pause for no reason.
        var assignments = await database.UserRoles
            .AsNoTracking()
            .Join(
                database.Roles.AsNoTracking(),
                assignment => assignment.RoleId,
                role => role.Id,
                (assignment, role) => new { assignment.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var byUser = assignments
            .GroupBy(entry => entry.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Name ?? string.Empty)
                    .OrderBy(name => name)
                    .ToList());

        var linked = await database.Employees
            .AsNoTracking()
            .Where(employee => employee.AccountId != null)
            .ToDictionaryAsync(
                employee => employee.AccountId!.Value,
                employee => employee.FullName,
                cancellationToken);

        var now = clock.Now;

        return accounts.Select(account => new AccountRow(
            account.Id,
            account.DisplayName,
            account.Email ?? string.Empty,
            account.JobTitle,
            account.IsActive,
            account.EmailConfirmed,
            account.LastSignedInAt,
            account.InvitedAt,
            account.LockoutEnd > now,
            byUser.GetValueOrDefault(account.Id) ?? [],
            linked.GetValueOrDefault(account.Id))).ToList();
    }

    public async Task<AccountRow?> FindAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).FirstOrDefault(account => account.Id == id);
}

public sealed record AccountRow(
    Guid Id,
    string DisplayName,
    string Email,
    string? JobTitle,
    bool IsActive,
    bool HasPassword,
    DateTimeOffset? LastSignedInAt,
    DateTimeOffset? InvitedAt,
    bool IsLockedOut,
    IReadOnlyList<string> Roles,
    string? EmployeeName)
{
    /// <summary>
    /// What an administrator needs to know at a glance, in one phrase.
    /// </summary>
    /// <remarks>
    /// Four separate columns of ticks make somebody work out the state for
    /// themselves, every time, and the one they misread is the withdrawn
    /// account that still looks fine.
    /// </remarks>
    public string State => this switch
    {
        { IsActive: false } => "Withdrawn",
        { IsLockedOut: true } => "Locked out",
        { HasPassword: false } => "Invited, never signed in",
        { LastSignedInAt: null } => "Never signed in",
        _ => "Active",
    };
}
