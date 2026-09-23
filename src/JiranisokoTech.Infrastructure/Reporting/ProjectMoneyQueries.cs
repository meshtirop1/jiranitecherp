using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Settings;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Reporting;

/// <summary>
/// What a project was agreed to cost, what it earned, and what it took.
/// </summary>
/// <remarks>
/// Section 10 asked for budget, revenue, costs and profitability. Only the budget is
/// stored; the other three are computed here every time, and that is the design rather
/// than laziness.
///
/// A stored revenue figure is a figure that disagrees with the invoices the moment one
/// is voided, and nobody can tell which of the two anybody acted on. The same goes for
/// cost. So the numbers are derived from the records that are the truth — invoices,
/// approved hours, paid expenses — and the only thing a person sets is the budget,
/// because that is a decision rather than a consequence.
///
/// The cost side uses the firm's standard hourly rate rather than each person's actual
/// pay, and the reason is a permission boundary rather than convenience. See
/// FirmSettings.StandardCostPerHourMinorUnits: a delivery manager holds projects.manage
/// and time.view_all and not employees.pay, and a cost derived from real salaries would
/// let them recover any one person's rate by dividing.
/// </remarks>
public sealed class ProjectMoneyQueries(AppDbContext database)
{
    public async Task<List<ProjectMoney>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await database.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        var currency = settings?.Currency ?? "KES";
        var costPerHour = settings?.StandardCostPerHourMinorUnits;

        var projects = await database.Projects
            .AsNoTracking()
            .OrderBy(project => project.Status == ProjectStatus.Active ? 0 : 1)
            .ThenBy(project => project.Name)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Status,
                project.BudgetMinorUnits,
                project.BudgetCurrency,
            })
            .ToListAsync(cancellationToken);

        if (projects.Count == 0)
        {
            return [];
        }

        var ids = projects.Select(project => project.Id).ToList();

        /*
         * Invoiced, and only what was actually sent. A draft invoice is a document
         * somebody is still writing, and counting it as revenue would let a project look
         * profitable because of an intention.
         *
         * Voided invoices are excluded for the same reason from the other direction: a
         * void says the amount was never owed.
         */
        var invoiced = await database.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.ProjectId != null
                && ids.Contains(invoice.ProjectId!.Value)
                && invoice.Status != InvoiceStatus.Draft
                && invoice.Status != InvoiceStatus.Void)
            /*
             * Summed from the lines rather than from a total column, because an invoice
             * has no total column — the total is the lines, which is what stops a header
             * figure and its detail from ever disagreeing. The cost of that decision is
             * paid here, in one join.
             */
            .SelectMany(invoice => invoice.Lines.Select(line => new
            {
                invoice.ProjectId,
                invoice.Currency,
                Amount = line.UnitMinorUnits * line.Quantity,
            }))
            .GroupBy(row => new { row.ProjectId, row.Currency })
            .Select(group => new
            {
                group.Key.ProjectId,
                group.Key.Currency,
                Total = group.Sum(row => row.Amount),
            })
            .ToListAsync(cancellationToken);

        /*
         * Approved hours only. Unapproved time is a claim rather than a fact, and a cost
         * line built from it moves every time somebody edits a timesheet.
         */
        var minutes = await database.TimeEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId != null
                && ids.Contains(entry.ProjectId!.Value)
                && entry.ApprovedAt != null)
            .GroupBy(entry => entry.ProjectId)
            .Select(group => new
            {
                ProjectId = group.Key,
                Minutes = group.Sum(entry => entry.Minutes),
            })
            .ToListAsync(cancellationToken);

        // Expenses that were actually paid out, not merely claimed or approved.
        var expenses = await database.Expenses
            .AsNoTracking()
            .Where(claim => claim.ProjectId != null
                && ids.Contains(claim.ProjectId!.Value)
                && claim.Status == ClaimStatus.Paid)
            .GroupBy(claim => new { claim.ProjectId, claim.Currency })
            .Select(group => new
            {
                group.Key.ProjectId,
                group.Key.Currency,
                Total = group.Sum(claim => claim.MinorUnits),
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. projects.Select(project =>
            {
                var earned = invoiced
                    .Where(row => row.ProjectId == project.Id)
                    .ToList();

                var spentOnExpenses = expenses
                    .Where(row => row.ProjectId == project.Id)
                    .ToList();

                var workedMinutes = minutes
                    .FirstOrDefault(row => row.ProjectId == project.Id)?.Minutes ?? 0;

                var labour = costPerHour is { } rate
                    ? (long)Math.Round(workedMinutes / 60m * rate, MidpointRounding.AwayFromZero)
                    : (long?)null;

                /*
                 * Amounts in more than one currency are reported separately rather than
                 * added. Money refuses cross-currency arithmetic, and this respects the
                 * refusal: the screen says which currencies are involved and declines to
                 * produce a margin, because a margin across two currencies needs a rate
                 * and a date that nothing here has been given.
                 */
                var currencies = earned.Select(row => row.Currency)
                    .Concat(spentOnExpenses.Select(row => row.Currency))
                    .Append(project.BudgetCurrency ?? currency)
                    .Append(currency)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var mixed = currencies.Count > 1;

                return new ProjectMoney(
                    project.Id,
                    project.Name,
                    project.Status,
                    project.BudgetMinorUnits is { } budget
                        ? Money.Of(budget, project.BudgetCurrency ?? currency)
                        : null,
                    mixed
                        ? null
                        : Money.Of(earned.Sum(row => row.Total), currency),
                    workedMinutes,
                    labour is { } cost && !mixed ? Money.Of(cost, currency) : null,
                    mixed
                        ? null
                        : Money.Of(spentOnExpenses.Sum(row => row.Total), currency),
                    mixed,
                    costPerHour is not null);
            }),
        ];
    }
}

/// <summary>
/// One project's money, as far as this system can honestly say.
/// </summary>
/// <remarks>
/// Every nullable figure here is nullable for a stated reason rather than for
/// convenience, and the screen says which reason applies. A dash because no budget was
/// set, a dash because no hourly cost has been configured, and a dash because the
/// project's money is in two currencies are three different situations, and reporting
/// zero for any of them would be a lie somebody would act on.
/// </remarks>
public sealed record ProjectMoney(
    Guid Id,
    string Name,
    ProjectStatus Status,
    Money? Budget,
    Money? Invoiced,
    int ApprovedMinutes,
    Money? Labour,
    Money? Expenses,
    bool HasMixedCurrencies,
    bool HasCostRate)
{
    /// <summary>What the project took, when both parts of it are known.</summary>
    public Money? Cost => Labour is { } labour && Expenses is { } expenses
        ? labour + expenses
        : null;

    /// <summary>What is left of the money after what it took.</summary>
    public Money? Margin => Invoiced is { } invoiced && Cost is { } cost
        ? invoiced - cost
        : null;

    /// <summary>
    /// The margin as a share of what was invoiced.
    /// </summary>
    /// <remarks>
    /// Nothing invoiced means no percentage rather than a division by zero or a
    /// hundred-per-cent loss. A project that has cost money and billed none is not
    /// minus infinity per cent profitable; it is a project that has not been billed.
    /// </remarks>
    public int? MarginShare => Invoiced is { MinorUnits: > 0 } invoiced && Margin is { } margin
        ? (int)Math.Round(margin.MinorUnits * 100.0 / invoiced.MinorUnits)
        : null;

    /// <summary>Has it spent more than was agreed?</summary>
    public bool IsOverBudget =>
        Budget is { } budget && Cost is { } cost
        && budget.Currency == cost.Currency && cost > budget;

    /// <summary>How much of the budget has gone.</summary>
    public int? BudgetUsedShare =>
        Budget is { MinorUnits: > 0 } budget && Cost is { } cost
        && budget.Currency == cost.Currency
            ? (int)Math.Round(cost.MinorUnits * 100.0 / budget.MinorUnits)
            : null;
}
