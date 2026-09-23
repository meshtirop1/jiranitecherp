using JiranisokoTech.Application.Accounting;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Accounting;

public sealed class AccountingRepository(AppDbContext database) : IAccountingRepository
{
    public Task<Account?> FindAccountAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Accounts.FirstOrDefaultAsync(account => account.Id == id, cancellationToken);

    /// <remarks>
    /// Compared against the code as the domain stores it — trimmed and upper-cased — because
    /// otherwise "4200" typed with a trailing space would miss the row that already has it and
    /// the caller would be handed a constraint violation instead of a sentence.
    /// </remarks>
    public Task<Account?> AccountWithCodeAsync(
        string code, CancellationToken cancellationToken = default)
    {
        var wanted = (code ?? string.Empty).Trim().ToUpperInvariant();

        return database.Accounts.FirstOrDefaultAsync(
            account => account.Code == wanted, cancellationToken);
    }

    public Task<List<Account>> AccountsAsync(CancellationToken cancellationToken = default) =>
        database.Accounts
            .OrderBy(account => account.Kind)
            .ThenBy(account => account.Code)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// With its charges, always. Every reason to load one — raising the next charge, settling
    /// one — either reads the collection or appends to it, and an owned collection that was not
    /// included is one EF will replace with an empty list on the next save.
    /// </remarks>
    public Task<RecurringExpense?> FindScheduleAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.RecurringExpenses
            .Include(one => one.Charges)
            .FirstOrDefaultAsync(one => one.Id == id, cancellationToken);

    public Task<List<RecurringExpense>> ActiveSchedulesAsync(
        CancellationToken cancellationToken = default) =>
        database.RecurringExpenses
            .Include(one => one.Charges)
            .Where(one => one.Status == RecurrenceStatus.Active)
            .ToListAsync(cancellationToken);

    /// <remarks>
    /// Three tables, because money reaches an account by three routes and a retirement that
    /// checked only one would be refused by the database on the other two — with a constraint
    /// name rather than a sentence.
    /// </remarks>
    public async Task<bool> AccountInUseAsync(
        Guid accountId, CancellationToken cancellationToken = default) =>
        await database.Invoices.AnyAsync(
            invoice => invoice.AccountId == accountId, cancellationToken)
        || await database.Expenses.AnyAsync(
            claim => claim.AccountId == accountId, cancellationToken)
        || await database.RecurringExpenses.AnyAsync(
            one => one.AccountId == accountId, cancellationToken);

    public void Add(Account account) => database.Accounts.Add(account);

    public void Add(RecurringExpense schedule) => database.RecurringExpenses.Add(schedule);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
