using JiranisokoTech.Domain.Renewals;

namespace JiranisokoTech.Tests.Settings;

/// <summary>
/// When a deadline is worth speaking up about, and how often.
/// </summary>
/// <remarks>
/// These exist because of a fault in committed code. Two scheduled jobs each asked "what
/// expires within the next N days" and mailed every department head about all of it, every
/// morning — so a contract ending in forty-five days produced forty-five identical emails and
/// a qualification lapsing in two months produced about sixty. Both jobs declare
/// <c>IRecurringJob</c>, whose own contract says every job must be safe to run twice and that
/// running twice finds nothing the second time. Neither was, and nothing failed, because
/// nobody writes a test asserting that an email was not sent again.
/// </remarks>
public class ReminderLadderTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    /// <summary>
    /// Nothing is said until the first threshold is reached.
    /// </summary>
    /// <remarks>
    /// The half that stops the ladder becoming the window it replaced. A contract ending in
    /// two years is not something anybody needs an email about this morning.
    /// </remarks>
    [Fact]
    public void A_deadline_far_off_is_not_worth_saying_anything_about()
    {
        var ladder = ReminderLadder.For(ReminderKind.ContractRenewal);

        Assert.Null(ladder.StageDueOn(Today, Today.AddDays(200)));
        Assert.Null(ladder.StageDueOn(Today, Today.AddDays(91)));
    }

    /// <summary>
    /// Each threshold reached moves it up a stage.
    /// </summary>
    /// <remarks>
    /// Three notices rather than one, because a letter sent ninety days out is forgotten by
    /// the time it matters — and rather than forty-five, because that is a filter rule.
    /// </remarks>
    [Theory]
    [InlineData(90, ReminderStage.First)]
    [InlineData(60, ReminderStage.First)]
    [InlineData(45, ReminderStage.Second)]
    [InlineData(20, ReminderStage.Second)]
    [InlineData(14, ReminderStage.Final)]
    [InlineData(1, ReminderStage.Final)]
    [InlineData(0, ReminderStage.Final)]
    public void Each_threshold_reached_raises_the_stage(int daysAway, ReminderStage expected)
    {
        var ladder = ReminderLadder.For(ReminderKind.ContractRenewal);

        Assert.Equal(expected, ladder.StageDueOn(Today, Today.AddDays(daysAway)));
    }

    /// <summary>
    /// A job that has been off comes back with the notice that is true now.
    /// </summary>
    /// <remarks>
    /// The nearest threshold rather than the earliest one not yet sent. A scheduler stopped
    /// for a fortnight, restarted with a deadline eleven days away, should send the fourteen-day
    /// letter — not the ninety-day one, which would tell somebody they have three months to
    /// arrange something due next week. Being late with the right message beats being on time
    /// with the wrong one.
    /// </remarks>
    [Fact]
    public void A_late_run_sends_the_notice_that_is_true_today()
    {
        var ladder = ReminderLadder.For(ReminderKind.ContractRenewal);

        Assert.Equal(ReminderStage.Final, ladder.StageDueOn(Today, Today.AddDays(11)));
    }

    /// <summary>
    /// An invoice's ladder counts forwards, because the deadline has already gone.
    /// </summary>
    /// <remarks>
    /// The one kind whose thresholds are negative. Nobody chases an invoice before it is due;
    /// the escalation starts a week after and gets louder.
    /// </remarks>
    [Theory]
    [InlineData(-3, null)]
    [InlineData(-7, ReminderStage.First)]
    [InlineData(-21, ReminderStage.Second)]
    [InlineData(-45, ReminderStage.Final)]
    [InlineData(-90, ReminderStage.Final)]
    public void An_overdue_invoice_escalates_after_the_date_rather_than_before_it(
        int daysAway, ReminderStage? expected)
    {
        var ladder = ReminderLadder.For(ReminderKind.InvoiceOverdue);

        Assert.Equal(expected, ladder.StageDueOn(Today, Today.AddDays(daysAway)));
    }

    /// <summary>
    /// Thresholds that do not come strictly closer together are refused.
    /// </summary>
    /// <remarks>
    /// Two equal thresholds make one stage unreachable — the earlier one always matches first
    /// — and an unreachable stage is a reminder somebody is relying on and will never get.
    /// Refused at the factory, because the symptom is silence and silence is the one fault
    /// nobody reports.
    ///
    /// A negative threshold is NOT refused, and the case is deliberately absent from this
    /// list: a threshold is days until the deadline, so it is negative once the deadline has
    /// gone, and an overdue invoice is the only kind of deadline anybody actually chases.
    /// </remarks>
    [Theory]
    [InlineData(90, 45, 45)]
    [InlineData(45, 45, 14)]
    [InlineData(14, 45, 90)]
    public void Thresholds_that_do_not_close_in_are_refused(int first, int second, int final)
    {
        Assert.ThrowsAny<ArgumentException>(() => ReminderLadder.Of(first, second, final));
    }

    /// <summary>A reminder nobody was told about is not recorded.</summary>
    /// <remarks>
    /// The row exists to stop a second notice, so writing one when no email went out would
    /// silence the real notice for good. A firm with no department head configured would then
    /// be warned about nothing, for ever, with the job reporting success every morning.
    /// </remarks>
    [Fact]
    public void A_reminder_nobody_was_told_about_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Reminder.Issued(
            ReminderKind.ContractRenewal,
            Guid.CreateVersion7(),
            "JTS-C-2026-001 — Fleet tracking",
            ReminderStage.First,
            new DateOnly(2026, 12, 31),
            told: 0,
            at: DateTimeOffset.UtcNow));
    }

    /// <summary>A reminder about nothing identifiable is refused.</summary>
    /// <remarks>
    /// Without the subject's identifier the row cannot match a second run, which is the only
    /// reason it is written — so it would be a row that looks like protection and is not.
    /// </remarks>
    [Fact]
    public void A_reminder_about_nothing_identifiable_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Reminder.Issued(
            ReminderKind.ContractRenewal,
            Guid.Empty,
            "JTS-C-2026-001 — Fleet tracking",
            ReminderStage.First,
            new DateOnly(2026, 12, 31),
            told: 2,
            at: DateTimeOffset.UtcNow));
    }
}
