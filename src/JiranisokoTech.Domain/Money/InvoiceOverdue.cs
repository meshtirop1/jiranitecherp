using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.Renewals;

namespace JiranisokoTech.Domain.Money;

/// <summary>
/// An invoice has reached a rung of the overdue ladder: seven, twenty-one or forty-five days
/// late and not paid.
/// </summary>
/// <remarks>
/// The one event in this application that nothing a person does raises. An invoice going
/// overdue is an absence — nobody paid — and an absence raises no event, which is why section
/// 31's own example ("WHEN invoice overdue, IF 7 days overdue, THEN notify finance") had
/// nothing to hang on. A daily job raises this at each rung of the ladder
/// <see cref="ReminderKind.InvoiceOverdue"/> already declared, once per rung, so a rule on it
/// fires three times for a bad debt rather than every morning.
///
/// Deliberately not a status on the invoice, for the reason the invoice gives: being late is
/// what is true of it at the moment somebody looks, not something anybody decided.
/// </remarks>
public sealed record InvoiceOverdue(
    Guid InvoiceId,
    Guid ClientId,
    string Number,
    DateOnly DueOn,
    int DaysOverdue,
    ReminderStage Stage,
    long OutstandingMinorUnits,
    string Currency) : DomainEvent;
