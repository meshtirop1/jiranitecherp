using System.Globalization;
using JiranisokoTech.Infrastructure.Reporting;
using Money = JiranisokoTech.Domain.Common.Money;

namespace JiranisokoTech.Web.Reporting;

/// <summary>
/// "Where things stand" as a file somebody can send on.
/// </summary>
/// <remarks>
/// The same figures as the page and nothing else. An export that shows more than
/// the screen it came from is how a department head ends up holding client debt
/// they cannot see anywhere in the application, and an export that shows less is
/// a file somebody reconciles against the page by hand.
///
/// That is why the two permission questions arrive here as arguments rather than
/// being asked here: the endpoint asks the same authorization service the page's
/// AuthorizeView asks, and this file cannot get them from anywhere else even by
/// accident.
/// </remarks>
public static class StandingCsv
{
    public static string For(FirmState state, DateOnly today, bool money, bool owed)
    {
        var rows = new List<IReadOnlyList<string?>>();

        rows.Add(["Section", "Figure", "Value"]);
        rows.Add(["Where things stand", "As at", Date(today)]);

        if (money)
        {
            rows.Add(["Money owed to us", "Outstanding on invoices sent and not settled",
                Figure(state.Outstanding)]);
            rows.Add(["Money owed to us", "Of that, overdue", Figure(state.Overdue)]);
            rows.Add(["Money owed to us", "Overdue invoices", Count(state.OverdueCount)]);
            rows.Add(["Money owed to us", "Oldest overdue, due on", Date(state.OldestOverdue)]);

            rows.Add(["Work done and not billed", "Approved and billable, on no invoice",
                Words.Hours(state.UnbilledMinutes)]);
        }

        rows.Add(["Waiting on somebody", "Logged time not yet approved",
            Words.Hours(state.UnapprovedMinutes)]);
        rows.Add(["Waiting on somebody", "Time entries not yet approved",
            Count(state.UnapprovedEntries)]);
        rows.Add(["Waiting on somebody", "Leave requests waiting for a decision",
            Count(state.LeaveWaiting)]);
        rows.Add(["Waiting on somebody", "Expense claims waiting for a decision",
            Count(state.ClaimsWaiting)]);

        if (owed)
        {
            rows.Add(["Money we owe our own people", "Approved and not yet paid",
                Figure(state.OwedToStaff)]);
            rows.Add(["Money we owe our own people", "Claims approved and not yet paid",
                Count(state.OwedToStaffCount)]);
        }

        if (state.AwaySoon.Count == 0)
        {
            rows.Add([Away, "Nobody is booked off", null]);
        }

        foreach (var away in state.AwaySoon)
        {
            // The name is the field this file is least entitled to trust: it is
            // typed by whoever set the person up, and it reaches a spreadsheet
            // that executes cells. Csv.Field is what stops that; here it is
            // enough to know that nothing sanitises it on the way in.
            rows.Add([Away, away.Name,
                $"{Words.For(away.Kind)}, {Date(away.From)} to {Date(away.To)}"]);
        }

        rows.Add(["Delivery", "Projects running", Count(state.ProjectsRunning)]);
        rows.Add(["Delivery", "Work blocked", Count(state.WorkBlocked)]);
        rows.Add(["Delivery", "Work past the date it was due", Count(state.WorkOverdue)]);

        return Csv.Sheet(rows);
    }

    /// <summary>The file this is offered under, dated, because it is a snapshot.</summary>
    /// <remarks>
    /// Two of these in a downloads folder are two different days, and a name
    /// without the date means the second one arrives as
    /// "where-things-stand (1).csv" and nobody can tell them apart afterwards.
    /// </remarks>
    public static string FileName(DateOnly today) => $"where-things-stand-{Date(today)}.csv";

    private const string Away = "Who is away, next fortnight";

    /// <summary>
    /// An amount of money, written so that no spreadsheet can reinterpret it.
    /// </summary>
    /// <remarks>
    /// Two separate hazards, one answer.
    ///
    /// The type's own ToString formats the decimal in the ambient culture, so on
    /// a container whose locale uses a comma for the decimal point it produces
    /// "KES 1234,56" — which is not a mangled figure but a broken file, because
    /// the comma ends the field and every column after it shifts by one. So the
    /// figure is composed here under the invariant culture instead.
    ///
    /// A bare number would then still be at the mercy of the machine the file is
    /// opened on: the same locales read "1234.00" as a thousands separator and
    /// show 123400, silently, with no error anywhere. The currency code in front
    /// of the amount is what settles it — the cell is unambiguously text, no
    /// locale rewrites it, and it reads exactly as the page reads. Nothing in
    /// this report is a figure anybody adds up in the spreadsheet; it is a
    /// statement of where the firm stands, and that is worth more than a cell
    /// that arithmetic can be done on.
    ///
    /// An absent figure is left blank rather than described. The query returns
    /// nothing both when there is nothing outstanding and when the amounts are
    /// in more than one currency and cannot be totalled, and the page's sentence
    /// for that case — every invoice sent has been paid — is true of the first
    /// and false of the second. An empty cell claims neither.
    /// </remarks>
    private static string? Figure(Money? amount) => amount is { } value
        ? string.Create(
            CultureInfo.InvariantCulture, $"{value.Currency} {value.MinorUnits / 100m:0.00}")
        : null;

    /// <summary>
    /// A date as the calendar, not as a locale.
    /// </summary>
    /// <remarks>
    /// The page writes dates out in words for somebody reading them. A file is
    /// opened by a spreadsheet that guesses, and 09/10/2026 is two different days
    /// depending on where the machine was bought — a guess it makes silently and
    /// which cannot be seen afterwards in the cell. ISO 8601 is the one form
    /// nothing re-reads, and it sorts.
    /// </remarks>
    private static string? Date(DateOnly? on) => on?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Count(int number) => number.ToString(CultureInfo.InvariantCulture);
}
