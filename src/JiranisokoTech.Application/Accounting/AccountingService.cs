using JiranisokoTech.Application.Abstractions;
using JiranisokoTech.Domain.Money;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Application.Accounting;

/// <summary>What the chart of accounts needs from storage.</summary>
public interface IAccountingRepository
{
    Task<Account?> FindAccountAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account already using this code, if one is.
    /// </summary>
    /// <remarks>
    /// The unique index is what actually holds when two people add 4200 at the same moment.
    /// This exists so that one of them is told which account already has that code, rather
    /// than being shown a constraint name.
    /// </remarks>
    Task<Account?> AccountWithCodeAsync(
        string code, CancellationToken cancellationToken = default);

    Task<List<Account>> AccountsAsync(CancellationToken cancellationToken = default);

    Task<RecurringExpense?> FindScheduleAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every standing cost that is still raising charges.</summary>
    Task<List<RecurringExpense>> ActiveSchedulesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Is anything coded to this account?</summary>
    /// <remarks>
    /// Asked before retiring one, so the refusal can say what is in the way. The database
    /// would refuse the delete anyway — the relationships are restricted — but a foreign key
    /// violation is a constraint name in somebody's face rather than a sentence.
    /// </remarks>
    Task<bool> AccountInUseAsync(Guid accountId, CancellationToken cancellationToken = default);

    void Add(Account account);

    void Add(RecurringExpense schedule);

    Task SaveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The chart of accounts, and the costs that fall due on a timetable.
/// </summary>
/// <remarks>
/// Section 18 had invoices, payments and expense claims and no way to say what any of it was
/// for. "We spent four million shillings last quarter" was answerable and "on what" was not.
///
/// The rules here are the ones that need a second row to answer: that a code is not already
/// somebody else's, and that an account with money coded to it is not retired out from under
/// a report. Everything else lives on the aggregates.
/// </remarks>
public sealed class AccountingService(IAccountingRepository accounting, IClock clock)
{
    public async Task<Account> OpenAccountAsync(
        string code,
        string name,
        AccountKind kind,
        CancellationToken cancellationToken = default)
    {
        var account = Account.Open(code, name, kind);

        if (await accounting.AccountWithCodeAsync(account.Code, cancellationToken) is { } taken)
        {
            throw new InvalidOperationException(
                $"{taken.Code} is already {taken.Name}. Two accounts with one code would split "
                + "that account's money across two lines of the report, and neither line would "
                + "look wrong.");
        }

        accounting.Add(account);
        await accounting.SaveAsync(cancellationToken);

        return account;
    }

    public async Task RenameAccountAsync(
        Guid accountId, string name, CancellationToken cancellationToken = default)
    {
        var account = await RequiredAccount(accountId, cancellationToken);

        account.Rename(name);
        await accounting.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Stop new money being coded to an account.
    /// </summary>
    /// <remarks>
    /// Retired rather than deleted, and refused outright while anything is coded to it. An
    /// account with history behind it cannot be removed without making last year's report
    /// unreadable — and "retire" is what people actually mean when they ask to delete one.
    /// </remarks>
    public async Task RetireAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await RequiredAccount(accountId, cancellationToken);

        account.Retire(clock.Now);
        await accounting.SaveAsync(cancellationToken);
    }

    public async Task ReopenAccountAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await RequiredAccount(accountId, cancellationToken);

        account.Reopen();
        await accounting.SaveAsync(cancellationToken);
    }

    /// <summary>Put a standing cost on the timetable.</summary>
    public async Task<RecurringExpense> ScheduleAsync(
        Guid accountId,
        string description,
        string payee,
        Money amount,
        Recurrence every,
        DateOnly startsOn,
        DateOnly? endsOn = null,
        CancellationToken cancellationToken = default)
    {
        var account = await RequiredAccount(accountId, cancellationToken);

        if (account.Kind != AccountKind.Expense)
        {
            throw new InvalidOperationException(
                $"{account.Code} {account.Name} is an income account. A standing cost coded to "
                + "income would be counted as money coming in.");
        }

        if (!account.IsOpen)
        {
            throw new InvalidOperationException(
                $"{account.Code} {account.Name} is retired, so nothing new may be coded to it.");
        }

        var schedule = RecurringExpense.Scheduled(
            accountId, description, payee, amount, every, startsOn, endsOn);

        accounting.Add(schedule);
        await accounting.SaveAsync(cancellationToken);

        return schedule;
    }

    /// <summary>
    /// Raise whatever has fallen due, on every active timetable.
    /// </summary>
    /// <remarks>
    /// Catches up rather than skipping. A scheduler that has been off for two months raises
    /// the two charges it missed, each carrying the date it was actually due rather than the
    /// day the job happened to run — because a cost missing from a report looks exactly like a
    /// cost that was not incurred, and the month somebody was on leave is the month the figure
    /// is wrong.
    ///
    /// The loop is bounded. A timetable starting years ago with nothing raised would otherwise
    /// walk month by month to today on every pass, and a job that takes longer every morning
    /// is a job somebody eventually turns off.
    /// </remarks>
    public async Task<int> RaiseWhatIsDueAsync(CancellationToken cancellationToken = default)
    {
        const int MostPerScheduleAtOnce = 24;

        var today = clock.Today;
        var raised = 0;

        foreach (var schedule in await accounting.ActiveSchedulesAsync(cancellationToken))
        {
            for (var caught = 0; caught < MostPerScheduleAtOnce; caught++)
            {
                if (schedule.NextDueOn() is not { } due || due > today)
                {
                    break;
                }

                if (schedule.RaiseFor(due, clock.Now) is null)
                {
                    break;
                }

                raised++;
            }
        }

        if (raised > 0)
        {
            await accounting.SaveAsync(cancellationToken);
        }

        return raised;
    }

    /// <summary>Somebody says the money went out.</summary>
    public async Task SettleAsync(
        Guid scheduleId,
        Guid chargeId,
        DateOnly on,
        CancellationToken cancellationToken = default)
    {
        var schedule = await RequiredSchedule(scheduleId, cancellationToken);

        schedule.Settled(chargeId, on);
        await accounting.SaveAsync(cancellationToken);
    }

    public async Task PauseAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await RequiredSchedule(scheduleId, cancellationToken);

        schedule.Pause();
        await accounting.SaveAsync(cancellationToken);
    }

    public async Task ResumeAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await RequiredSchedule(scheduleId, cancellationToken);

        schedule.Resume();
        await accounting.SaveAsync(cancellationToken);
    }

    /// <summary>Change what a standing cost is, or when it stops.</summary>
    public async Task AmendAsync(
        Guid scheduleId,
        Money amount,
        string payee,
        DateOnly? endsOn,
        CancellationToken cancellationToken = default)
    {
        var schedule = await RequiredSchedule(scheduleId, cancellationToken);

        schedule.Costs(amount, payee);
        schedule.Until(endsOn);

        await accounting.SaveAsync(cancellationToken);
    }

    private async Task<Account> RequiredAccount(Guid id, CancellationToken cancellationToken) =>
        await accounting.FindAccountAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no account with that identifier.");

    private async Task<RecurringExpense> RequiredSchedule(
        Guid id, CancellationToken cancellationToken) =>
        await accounting.FindScheduleAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("There is no standing cost with that identifier.");
}
