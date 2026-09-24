using JiranisokoTech.Domain.Money;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Money = JiranisokoTech.Domain.Common.Money;

/*
 * The report totals are keyed by a nullable account id, and null is a key this file means
 * rather than tolerates: it is the unclassified bucket, which every invoice and claim
 * written before the chart of accounts existed falls into. The report names that row
 * explicitly, because one that quietly dropped it would show a firm that earned nothing.
 *
 * CS8714 objects because Dictionary declares its key notnull. A Dictionary whose key is a
 * Nullable<T> does accept null — the runtime null check is folded away for a value-type
 * key — so the warning is about the annotation rather than the behaviour. Suppressed here
 * with the reason rather than worked around with a sentinel, because Guid.Empty standing
 * for an absent account is a convention somebody has to be told, while a nullable key is a
 * fact the type states.
 *
 * These three sat in the build for two commits without anybody noticing, which is what a
 * repository that tolerates any warnings at all costs: the wall is only useful while it is
 * exactly zero high.
 */
#pragma warning disable CS8714

namespace JiranisokoTech.Infrastructure.Accounting;

/// <summary>
/// The reads the accounting screens do.
/// </summary>
/// <remarks>
/// Every figure here is summed from the documents themselves — the invoices, the claims and
/// the charges — and none of it is read from a stored balance, because there is no stored
/// balance anywhere. That is the decision of section 18: a balance column is a number that
/// can disagree with the documents it was added up from, and on the day it does nobody can
/// tell which is wrong.
/// </remarks>
public sealed class AccountingQueries(AppDbContext database)
{
    public async Task<List<AccountRow>> AccountsAsync(
        CancellationToken cancellationToken = default)
    {
        var accounts = await database.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Kind)
            .ThenBy(account => account.Code)
            .Select(account => new
            {
                account.Id,
                account.Code,
                account.Name,
                account.Kind,
                account.RetiredAt,
            })
            .ToListAsync(cancellationToken);

        /*
         * How much is coded to each, counted rather than summed. A count is what the chart
         * screen needs — it answers "is anything using this" before somebody retires one — and
         * summing three tables of mixed currencies to put a figure beside an account name
         * would be the report, on the wrong page.
         */
        var onInvoices = await database.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.AccountId != null)
            .GroupBy(invoice => invoice.AccountId!.Value)
            .Select(group => new { Account = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Account, row => row.Count, cancellationToken);

        var onClaims = await database.Expenses
            .AsNoTracking()
            .Where(claim => claim.AccountId != null)
            .GroupBy(claim => claim.AccountId!.Value)
            .Select(group => new { Account = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Account, row => row.Count, cancellationToken);

        var onStanding = await database.RecurringExpenses
            .AsNoTracking()
            .GroupBy(one => one.AccountId)
            .Select(group => new { Account = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Account, row => row.Count, cancellationToken);

        return accounts.Select(account => new AccountRow(
            account.Id,
            account.Code,
            account.Name,
            account.Kind,
            account.RetiredAt is null,
            onInvoices.GetValueOrDefault(account.Id)
                + onClaims.GetValueOrDefault(account.Id)
                + onStanding.GetValueOrDefault(account.Id))).ToList();
    }

    /// <summary>
    /// What the firm earned and spent over a window, by account.
    /// </summary>
    /// <remarks>
    /// <b>Cash out, invoiced in, and the report says so on its face.</b> Income is what was
    /// invoiced and not what was collected; expenditure is what was claimed or fell due and
    /// not what left the bank. Mixing the two — invoiced income against paid costs — produces
    /// a margin that is neither accrual nor cash and is wrong in a direction that always
    /// flatters. The page names which it is rather than leaving somebody to assume.
    ///
    /// <b>Drafts are left out of income.</b> A draft invoice is a number somebody is still
    /// arguing about, and counting it would let the report be improved by typing.
    ///
    /// <b>One currency at a time.</b> Sums across currencies are refused by Money itself, and
    /// rightly — adding shillings to dollars is a figure with no meaning. The report is asked
    /// for a currency and reports on that, saying how many documents it left out.
    /// </remarks>
    public async Task<IncomeAndExpenditure> ReportAsync(
        DateOnly from,
        DateOnly to,
        string currency,
        CancellationToken cancellationToken = default)
    {
        var accounts = await database.Accounts
            .AsNoTracking()
            .ToDictionaryAsync(
                account => account.Id,
                account => new Named(account.Code, account.Name),
                cancellationToken);

        // --- income: invoices issued in the window, excluding drafts -------------
        var invoices = await database.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Where(invoice => invoice.Status != InvoiceStatus.Draft
                && invoice.IssuedOn >= from
                && invoice.IssuedOn <= to)
            .ToListAsync(cancellationToken);

        var income = new Dictionary<Guid?, long>();
        var wrongCurrency = 0;

        foreach (var invoice in invoices)
        {
            if (invoice.Total.Currency != currency)
            {
                wrongCurrency++;
                continue;
            }

            income[invoice.AccountId] =
                income.GetValueOrDefault(invoice.AccountId) + invoice.Total.MinorUnits;
        }

        // --- expenditure: claims, and standing charges that fell due -------------
        /*
         * Approved and paid only. A submitted claim nobody has looked at is a number one
         * person typed, and counting it would let the cost report be moved by anybody with a
         * receipt and an opinion. A refused one is not a cost at all.
         *
         * Dated by when the money was spent rather than by when the claim was decided,
         * because that is the month the cost belongs to — a January taxi approved in March is
         * a January cost, and filing it in March is how a quarter's figures come out wrong in
         * both directions at once.
         */
        var claims = await database.Expenses
            .AsNoTracking()
            .Where(claim => (claim.Status == ClaimStatus.Approved
                    || claim.Status == ClaimStatus.Paid)
                && claim.SpentOn >= from
                && claim.SpentOn <= to)
            .Select(claim => new
            {
                claim.AccountId,
                claim.MinorUnits,
                claim.Currency,
            })
            .ToListAsync(cancellationToken);

        var spent = new Dictionary<Guid?, long>();

        foreach (var claim in claims)
        {
            if (claim.Currency != currency)
            {
                wrongCurrency++;
                continue;
            }

            spent[claim.AccountId] =
                spent.GetValueOrDefault(claim.AccountId) + claim.MinorUnits;
        }

        var standing = await database.RecurringExpenses
            .AsNoTracking()
            .Include(one => one.Charges)
            .ToListAsync(cancellationToken);

        foreach (var schedule in standing)
        {
            foreach (var charge in schedule.Charges
                .Where(charge => charge.DueOn >= from && charge.DueOn <= to))
            {
                if (charge.Currency != currency)
                {
                    wrongCurrency++;
                    continue;
                }

                spent[schedule.AccountId] =
                    spent.GetValueOrDefault(schedule.AccountId) + charge.AmountMinorUnits;
            }
        }

        /*
         * Payroll, section 22, and the reason this report's bottom line could not be called a
         * profit until now.
         *
         * Approved and paid runs only. A draft is a calculation somebody is still checking and
         * can be rebuilt or abandoned, so counting one would put a cost in the accounts that
         * might never be incurred — the same reason a draft invoice is not income.
         *
         * Dated by the period it pays for rather than by when it was approved or paid, which is
         * what accrual means and what the rest of this report already does: a run for March
         * approved in April is March's cost.
         *
         * Gross plus the firm's own contributions, never net. What leaves the firm is the gross
         * — the deductions go to KRA and the funds rather than staying behind — so a cost built
         * from net would understate the firm's largest cost by roughly a third, quietly.
         *
         * One firm-wide figure and no breakdown, deliberately. ProjectMoneyQueries costs
         * projects at a blended rate precisely so that nobody without employees.pay can divide
         * their way to a colleague's salary, and a payroll line split by department would hand
         * back exactly that: a department of two is one subtraction from an individual.
         */
        var runs = await database.PayRuns
            .AsNoTracking()
            .Include(run => run.Payslips)
            .ThenInclude(slip => slip.Lines)
            .Where(run => (run.Status == Domain.Payroll.PayRunStatus.Approved
                    || run.Status == Domain.Payroll.PayRunStatus.Paid)
                && run.PeriodEnd >= from
                && run.PeriodEnd <= to)
            .ToListAsync(cancellationToken);

        var payroll = 0L;

        foreach (var run in runs)
        {
            if (run.Currency != currency)
            {
                wrongCurrency++;
                continue;
            }

            payroll += run.Gross.MinorUnits;

            foreach (var slip in run.Payslips)
            {
                payroll += slip.EmployerPays.MinorUnits;
            }
        }

        return new IncomeAndExpenditure(
            from,
            to,
            currency,
            [.. Lines(income, accounts, AccountKind.Income, currency)],
            [.. Lines(spent, accounts, AccountKind.Expense, currency)],
            wrongCurrency,
            payroll);
    }

    /// <summary>
    /// One side of the report, with anything uncoded named rather than hidden.
    /// </summary>
    /// <remarks>
    /// The unclassified line is the important one. Every invoice and claim already in the
    /// database has no account, so the first time this report is opened almost everything will
    /// be in that row — and a report that quietly dropped them would show a firm that earned
    /// nothing. Naming it is also the only thing that makes the coding get done.
    /// </remarks>
    private static IEnumerable<ReportLine> Lines(
        Dictionary<Guid?, long> totals,
        Dictionary<Guid, Named> accounts,
        AccountKind kind,
        string currency)
    {
        foreach (var (accountId, minorUnits) in totals.OrderByDescending(one => one.Value))
        {
            if (accountId is { } id && accounts.TryGetValue(id, out var account))
            {
                yield return new ReportLine(
                    account.Code, account.Name, Money.Of(minorUnits, currency));

                continue;
            }

            yield return new ReportLine(
                "—",
                kind == AccountKind.Income ? "Not coded to an account" : "Not coded to an account",
                Money.Of(minorUnits, currency));
        }
    }

    /// <summary>
    /// The standing costs, and what is outstanding on each.
    /// </summary>
    /// <remarks>
    /// The next due date is computed by the aggregate rather than stored, so the list is loaded
    /// with its charges. That is the one query in this file that cannot be a projection, and
    /// the number of standing costs a firm has is small enough that it does not matter.
    /// </remarks>
    public async Task<List<StandingCostRow>> StandingCostsAsync(
        CancellationToken cancellationToken = default)
    {
        var costs = await database.RecurringExpenses
            .AsNoTracking()
            .Include(one => one.Charges)
            .OrderBy(one => one.Description)
            .ToListAsync(cancellationToken);

        var accounts = await database.Accounts
            .AsNoTracking()
            .ToDictionaryAsync(
                account => account.Id, account => account.Code, cancellationToken);

        return costs.Select(cost =>
        {
            var unsettled = cost.Charges.Where(charge => !charge.IsSettled).ToList();
            var oldest = unsettled.OrderBy(charge => charge.DueOn).FirstOrDefault();

            return new StandingCostRow(
                cost.Id,
                accounts.GetValueOrDefault(cost.AccountId) ?? "—",
                cost.Description,
                cost.Payee,
                cost.Amount,
                cost.Every,
                cost.IsActive,
                cost.NextDueOn(),
                unsettled.Count,
                oldest?.Id,
                oldest?.DueOn);
        }).ToList();
    }

}

/// <summary>One standing cost, as the accounting screen shows it.</summary>
public sealed record StandingCostRow(
    Guid Id,
    string AccountCode,
    string Description,
    string Payee,
    Money Amount,
    Recurrence Every,
    bool IsActive,
    DateOnly? NextDueOn,
    int Unsettled,
    Guid? OldestUnsettled,
    DateOnly? OldestUnsettledOn);

/// <summary>An account's code and name, for labelling a report line.</summary>
internal sealed record Named(string Code, string Name);

/// <summary>One account, as the chart screen shows it.</summary>
public sealed record AccountRow(
    Guid Id,
    string Code,
    string Name,
    AccountKind Kind,
    bool IsOpen,
    int CodedToIt);

/// <summary>One line of the report.</summary>
public sealed record ReportLine(string Code, string Name, Money Total);

/// <summary>What the firm earned and spent over a window.</summary>
public sealed record IncomeAndExpenditure(
    DateOnly From,
    DateOnly To,
    string Currency,
    IReadOnlyList<ReportLine> Income,
    IReadOnlyList<ReportLine> Expenditure,
    int LeftOutForCurrency,
    long PayrollMinorUnits = 0)
{
    public Money TotalIncome => Sum(Income);

    /// <summary>
    /// Everything the period cost, payroll included.
    /// </summary>
    /// <remarks>
    /// Payroll is added here rather than being made an account in the chart, because it is not
    /// one: an account is something somebody codes an invoice or a claim against, and a pay run
    /// is neither. Keeping it outside also keeps it unmissable — the firm's largest cost is its
    /// own line rather than one row among forty.
    /// </remarks>
    public Money TotalExpenditure => Sum(Expenditure) + Payroll;

    /// <summary>
    /// What one period's pay cost the firm, from the approved runs covering it.
    /// </summary>
    /// <remarks>
    /// One figure for the whole firm and no breakdown behind it. Splitting it by department
    /// would undo the reasoning ProjectMoneyQueries is built on — a department of two is one
    /// subtraction away from an individual's salary, and this report is read by people who do
    /// not hold employees.pay.
    /// </remarks>
    public Money Payroll => Money.Of(PayrollMinorUnits, Currency);

    /// <summary>
    /// What is left, which is still not a profit figure and is still not called one.
    /// </summary>
    /// <remarks>
    /// Income less costs, and the costs now include payroll — which closes the largest of the
    /// three gaps this remark used to name. It is still not profit, and the other two are why:
    /// there is no depreciation here and no corporation tax. Calling it profit would put a
    /// figure in front of somebody that is wrong by whatever those come to, and being wrong by
    /// less is not the same as being right.
    /// </remarks>
    public Money Difference => TotalIncome - TotalExpenditure;

    /// <summary>
    /// There is genuinely nothing to show for this window.
    /// </summary>
    /// <remarks>
    /// Payroll counts, and forgetting it here made the whole report disappear behind "nothing
    /// was invoiced or claimed" on a window whose only activity was a month's wages — which is
    /// the firm's largest cost being hidden by the sentence that says there are no costs.
    /// </remarks>
    public bool IsEmpty =>
        Income.Count == 0 && Expenditure.Count == 0 && PayrollMinorUnits == 0;

    private Money Sum(IReadOnlyList<ReportLine> lines) =>
        lines.Count == 0
            ? Money.Zero(Currency)
            : lines.Aggregate(
                Money.Zero(Currency), (running, line) => running + line.Total);
}

#pragma warning restore CS8714
