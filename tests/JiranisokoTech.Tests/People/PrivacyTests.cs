using JiranisokoTech.Application.Privacy;
using JiranisokoTech.Domain.People;
using JiranisokoTech.Domain.Privacy;
using JiranisokoTech.Infrastructure.People;
using JiranisokoTech.Infrastructure.Privacy;
using JiranisokoTech.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Tests.People;

/// <summary>
/// Data protection requests, and the conflict that shapes the whole section.
/// </summary>
/// <remarks>
/// Section 55. A right to erasure runs against nearly everything else this system does on
/// purpose: leavers keep their name on their work, the audit trail is append-only and never
/// pruned, an incident timeline records who did what. The law lets a controller keep what it is
/// obliged to keep; what it does not allow is keeping things quietly.
///
/// So the tests here are about the resolution rather than about deletion. What is actually
/// erasable, what must be retained and on what stated basis, and — the one that matters most —
/// that the record of the decision cannot be empty.
/// </remarks>
public class PrivacyTests
{
    /// <summary>
    /// A request cannot be closed with nothing decided.
    /// </summary>
    /// <remarks>
    /// The state this entire register exists to prevent: a closed file saying a decision was
    /// taken and refusing to say what. "We forgot" and "we decided" look identical in an empty
    /// file, and a year later nobody can tell them apart.
    /// </remarks>
    [Fact]
    public async Task A_request_cannot_be_answered_with_nothing_decided()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Other, "Amina Wekesa", fixture.Clock.Today);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => privacy.AnsweredAsync(request.Id, "Done."));

        Assert.Contains("refuses to say what", refusal.Message);
    }

    /// <summary>
    /// Anything kept or refused has to say why, and a retention has to say until when.
    /// </summary>
    /// <remarks>
    /// "Retained" on its own is the least useful word in a privacy file. The date is what turns a
    /// policy into something somebody can act on later — without it, a retention period is
    /// keeping it for ever with a sentence attached.
    /// </remarks>
    [Fact]
    public async Task Keeping_something_needs_a_basis_and_an_end_date()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        // No basis.
        await Assert.ThrowsAsync<ArgumentException>(
            () => privacy.DecideAsync(
                request.Id, DataClass.PayAndTerms, OutcomeKind.Retained, who));

        // A basis, but no end date.
        await Assert.ThrowsAsync<ArgumentException>(
            () => privacy.DecideAsync(
                request.Id,
                DataClass.PayAndTerms,
                OutcomeKind.Retained,
                who,
                "Employment records are kept for five years"));

        await privacy.DecideAsync(
            request.Id,
            DataClass.PayAndTerms,
            OutcomeKind.Retained,
            who,
            "Employment records are kept for five years",
            fixture.Clock.Today.AddYears(5));

        var outcome = (await privacy.OneAsync(request.Id))!.Outcomes.Single();

        Assert.Equal(OutcomeKind.Retained, outcome.Kind);
        Assert.Equal(fixture.Clock.Today.AddYears(5), outcome.Until);
    }

    /// <summary>
    /// The audit trail is never erased or anonymised, for anybody.
    /// </summary>
    /// <remarks>
    /// It is the evidence that answers who changed what — including the erasure itself — so
    /// removing it would destroy the proof that the request was honoured. Written down as a
    /// refusal with a message rather than left implicit, because "we forgot" and "we decided"
    /// look the same afterwards.
    /// </remarks>
    [Fact]
    public async Task The_audit_trail_is_never_erased()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => privacy.DecideAsync(
                request.Id, DataClass.AuditTrail, OutcomeKind.Erased, who));

        Assert.Contains("never erased", refusal.Message);

        // Recorded as kept instead, which is the honest answer.
        await privacy.DecideAsync(
            request.Id,
            DataClass.AuditTrail,
            OutcomeKind.Retained,
            who,
            "The trail is the evidence that this erasure happened",
            fixture.Clock.Today.AddYears(7));

        Assert.Single((await privacy.OneAsync(request.Id))!.Outcomes);
    }

    /// <summary>
    /// One decision per class, replaced rather than added to.
    /// </summary>
    /// <remarks>
    /// Two answers to "what happened to their contact details" is a file where whichever a reader
    /// saw first would be whichever the database returned first.
    /// </remarks>
    [Fact]
    public async Task Deciding_the_same_class_twice_replaces_the_first()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        await privacy.DecideAsync(
            request.Id, DataClass.ContactDetails, OutcomeKind.Retained, who,
            "still employed", fixture.Clock.Today.AddYears(1));

        await privacy.DecideAsync(
            request.Id, DataClass.ContactDetails, OutcomeKind.Erased, who);

        var outcomes = (await privacy.OneAsync(request.Id))!.Outcomes;

        Assert.Single(outcomes);
        Assert.Equal(OutcomeKind.Erased, outcomes.Single().Kind);
    }

    /// <summary>
    /// Erasure empties the personal data and keeps the name.
    /// </summary>
    /// <remarks>
    /// The section's central decision. Deleting the row would turn every task, approval and audit
    /// entry the person touched into "unknown user", which destroys the firm's own record of what
    /// happened rather than the person's privacy — the same argument <see cref="Employee.Leave"/>
    /// already makes for keeping a leaver.
    /// </remarks>
    [Fact]
    public async Task Erasure_empties_the_personal_data_and_keeps_the_name()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo", left: true);

        var employee = await context.Employees.FirstAsync(one => one.Id == who);

        employee.Record(new PersonalDetails
        {
            Phone = "+254700000000",
            PersonalEmail = "brian@example.com",
            NationalId = "12345678",
            TaxNumber = "A001234567X",
            DateOfBirth = new DateOnly(1995, 4, 2),
            Address = "Nairobi",
        });

        employee.Record(new EmergencyContact { Name = "Mary Kiptoo", Relationship = "Mother" });
        await context.SaveChangesAsync();

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        await privacy.DecideAsync(
            request.Id, DataClass.ContactDetails, OutcomeKind.Erased, who);

        await privacy.ForgetAsync(request.Id);

        var after = await context.Employees.FirstAsync(one => one.Id == who);

        // Gone.
        Assert.Null(after.Details.Phone);
        Assert.Null(after.Details.PersonalEmail);
        Assert.Null(after.Details.NationalId);
        Assert.Null(after.Details.TaxNumber);
        Assert.Null(after.Details.DateOfBirth);
        Assert.Null(after.Emergency.Name);

        // Kept, so that everything they did still reads.
        Assert.Equal("Brian Kiptoo", after.FullName);
        Assert.Equal(EmploymentStatus.Left, after.Status);
    }

    /// <summary>
    /// A current employee's record cannot be emptied.
    /// </summary>
    /// <remarks>
    /// Their contact details are held under the employment contract, so there is no basis on
    /// which to erase them — and the firm would be unable to reach somebody it employs. The
    /// refusal says so rather than silently doing nothing.
    /// </remarks>
    [Fact]
    public async Task A_current_employee_cannot_be_forgotten()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        await privacy.DecideAsync(
            request.Id, DataClass.ContactDetails, OutcomeKind.Erased, who);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => privacy.ForgetAsync(request.Id));

        Assert.Contains("still works here", refusal.Message);
    }

    /// <summary>
    /// Nothing is emptied without a decision recorded against it.
    /// </summary>
    /// <remarks>
    /// A button that empties a personnel file on one person's say-so, with nothing afterwards
    /// able to say why, is the thing the whole register exists to prevent. Retaining everything
    /// and then pressing erase is exactly that, so it is refused.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_emptied_without_a_decision_saying_so()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo", left: true);

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Erasure, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        // Everything kept, nothing erased.
        await privacy.DecideAsync(
            request.Id, DataClass.PayAndTerms, OutcomeKind.Retained, who,
            "five years under the Employment Act", fixture.Clock.Today.AddYears(5));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => privacy.ForgetAsync(request.Id));

        Assert.Contains("no decision behind it", refusal.Message);
    }

    /// <summary>
    /// An answered request cannot have its decisions changed.
    /// </summary>
    /// <remarks>
    /// The subject has been told what was decided, so changing it afterwards would make the
    /// answer they were given untrue — with nothing on the file to show it had ever said
    /// anything else.
    /// </remarks>
    [Fact]
    public async Task An_answered_request_is_closed()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var who = await Somebody(context, fixture, "Brian Kiptoo");

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Access, SubjectKind.Employee, "Brian Kiptoo",
            fixture.Clock.Today, who);

        await privacy.DecideAsync(
            request.Id, DataClass.StaffRecord, OutcomeKind.Disclosed, who);

        await privacy.AnsweredAsync(request.Id, "Sent by email on the 24th.");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => privacy.DecideAsync(
                request.Id, DataClass.Documents, OutcomeKind.Disclosed, who));

        Assert.Contains("has been answered", refusal.Message);
    }

    /// <summary>
    /// The deadline is thirty days from when the request was made.
    /// </summary>
    /// <remarks>
    /// Computed rather than typed, because a deadline somebody types is one that can be typed
    /// wrong in the direction that suits whoever is typing. From receipt rather than from when it
    /// was recorded, because the statutory clock does not wait for the paperwork.
    /// </remarks>
    [Fact]
    public async Task The_deadline_runs_from_when_it_arrived()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var context = fixture.NewContext();
        var (privacy, _) = Services(fixture, context);

        var arrived = fixture.Clock.Today.AddDays(-25);

        var request = await privacy.ReceiveAsync(
            PrivacyAsk.Access, SubjectKind.Other, "Amina Wekesa", arrived);

        Assert.Equal(arrived.AddDays(30), request.DueOn);
        Assert.Equal(5, request.DaysLeftOn(fixture.Clock.Today));
        Assert.False(request.IsOverdueOn(fixture.Clock.Today));
        Assert.True(request.IsOverdueOn(fixture.Clock.Today.AddDays(6)));
    }

    private static (PrivacyService Privacy, PeopleRepository People) Services(
        DatabaseFixture fixture, TestDbContext context)
    {
        var people = new PeopleRepository(context);

        return (
            new PrivacyService(new PrivacyRepository(context), people, fixture.Clock),
            people);
    }

    private static async Task<Guid> Somebody(
        TestDbContext context, DatabaseFixture fixture, string name, bool left = false)
    {
        var employee = Employee.Hire(name, fixture.Clock.Today, null, "Engineer");

        employee.Start();

        if (left)
        {
            employee.Leave(fixture.Clock.Today, "took a job elsewhere");
        }

        context.Employees.Add(employee);
        await context.SaveChangesAsync();

        return employee.Id;
    }
}
