using JiranisokoTech.Application.Authorization;
using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Tests.Application;

/// <summary>
/// The rest of a staff record: details, next of kin, terms, skills, qualifications.
/// </summary>
/// <remarks>
/// Two of these are about keeping information away from people rather than about
/// recording it, and they are the two that matter. A staff list that doubles as a
/// salary list, and an audit trail that accumulates every national identity number
/// the firm has ever held, are both one careless change away.
/// </remarks>
public class StaffRecordTests
{
    private static Employee Somebody() =>
        Employee.Hire("Meshack Tirop", new DateOnly(2026, 1, 5));

    /// <summary>
    /// Pay is a permission of its own, held by fewer roles than the staff list.
    /// </summary>
    /// <remarks>
    /// Asserted against the role matrix rather than left to a screen, because the
    /// failure is silent: a page that used employees.view for a salary would look
    /// correct and would show every head of department what everybody earns.
    /// </remarks>
    [Fact]
    public void Seeing_the_staff_list_is_not_seeing_what_people_earn()
    {
        var canSeeStaff = Roles.All
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.EmployeesView))
            .ToList();

        var canSeePay = Roles.All
            .Where(role => Roles.PermissionsFor(role).Contains(Permissions.EmployeesPay))
            .ToList();

        // Somebody can see pay, or the permission guards nothing.
        Assert.NotEmpty(canSeePay);

        // And strictly fewer roles than can see the staff list, or the two are the
        // same permission wearing two names.
        Assert.True(
            canSeePay.Count < canSeeStaff.Count,
            "Every role that can read the staff list can also read salaries, which makes "
            + "employees.pay decorative.");
    }

    /// <summary>
    /// A head of department cannot see what their team is paid.
    /// </summary>
    /// <remarks>
    /// Named rather than left implicit in the count above. A head decides what their
    /// team does; what it costs is the firm's business with each person in it, and a
    /// head who could read it would learn what their colleagues at the same level earn.
    /// </remarks>
    [Fact]
    public void A_head_of_department_cannot_see_pay() =>
        Assert.DoesNotContain(
            Permissions.EmployeesPay, Roles.PermissionsFor(Roles.DepartmentHead));

    [Fact]
    public void Human_resources_can_see_pay() =>
        Assert.Contains(Permissions.EmployeesPay, Roles.PermissionsFor(Roles.HumanResources));

    /// <summary>
    /// The identity numbers and the terms never reach the audit trail.
    /// </summary>
    /// <remarks>
    /// The trail is append-only and kept for years, so a national identity number
    /// written into it is there for good — and it would be a second copy, in a table
    /// read by more people than the staff record is. The salary is excluded for a
    /// different reason: a change history containing every salary the firm has ever
    /// paid is a payroll report by another name.
    /// </remarks>
    [Fact]
    public void The_sensitive_parts_of_a_record_are_kept_out_of_the_audit_trail()
    {
        Assert.Contains(nameof(Employee.Details), Employee.AuditExcludes);
        Assert.Contains(nameof(Employee.Terms), Employee.AuditExcludes);
    }

    /// <summary>
    /// An identity number is masked to its last four characters.
    /// </summary>
    /// <remarks>
    /// Enough for somebody holding the paper copy to confirm it is the same number, and
    /// not enough to be worth taking from a screenshot.
    /// </remarks>
    [Theory]
    [InlineData("12345678", "••••5678")]
    [InlineData("A012345678B", "•••••••678B")]
    [InlineData("1234", "••••")]
    [InlineData("12", "••")]
    public void An_identity_number_is_masked(string given, string expected) =>
        Assert.Equal(expected, new PersonalDetails { NationalId = given }.MaskedNationalId);

    [Fact]
    public void A_missing_identity_number_masks_to_nothing() =>
        Assert.Null(new PersonalDetails().MaskedNationalId);

    /// <summary>
    /// A contact with a name and no number is distinguishable from none at all.
    /// </summary>
    /// <remarks>
    /// Both are gaps, and only one of them looks like a gap. "Nobody recorded" is
    /// something somebody fills in; a name with no way to reach them looks filled and
    /// is worse, because it will be relied on once.
    /// </remarks>
    [Fact]
    public void A_contact_with_no_number_is_not_reachable()
    {
        var named = new EmergencyContact { Name = "Grace Tirop", Relationship = "Sister" };

        Assert.False(named.IsEmpty);
        Assert.False(named.IsReachable);

        Assert.True((named with { Phone = "+254700000000" }).IsReachable);
    }

    [Fact]
    public void A_salary_needs_both_a_number_and_a_currency()
    {
        Assert.Null(new EmploymentTerms { SalaryMinorUnits = 250_000_00 }.Salary);
        Assert.Null(new EmploymentTerms { SalaryCurrency = "KES" }.Salary);

        var paid = new EmploymentTerms
        {
            SalaryMinorUnits = 250_000_00,
            SalaryCurrency = "KES",
        };

        Assert.Equal(Money.Of(250_000_00, "KES"), paid.Salary);
    }

    /// <summary>
    /// Probation ending is not something the system acts on.
    /// </summary>
    /// <remarks>
    /// A probation that ends is a conversation somebody has to have. A system that
    /// quietly confirmed an appointment because a date passed would be making that
    /// decision on the firm's behalf, so this only answers the question.
    /// </remarks>
    [Fact]
    public void Probation_is_a_question_and_not_an_action()
    {
        var terms = new EmploymentTerms { ProbationEndsOn = new DateOnly(2026, 4, 5) };

        Assert.True(terms.IsOnProbation(new DateOnly(2026, 4, 5)));
        Assert.False(terms.IsOnProbation(new DateOnly(2026, 4, 6)));
    }

    /// <summary>A skill recorded twice is one skill at the newer level.</summary>
    /// <remarks>
    /// Somebody who was learning Rust last year and is strong at it now has one skill.
    /// A list showing both is a list nobody trusts.
    /// </remarks>
    [Fact]
    public void Recording_a_skill_again_replaces_how_well()
    {
        var person = Somebody();

        person.Knows("Rust", SkillLevel.Learning);
        person.Knows("rust", SkillLevel.Strong);

        var skill = Assert.Single(person.Skills);

        Assert.Equal(SkillLevel.Strong, skill.Level);

        // And the newer spelling wins, because it is what somebody typed most recently.
        Assert.Equal("rust", skill.Name);
    }

    [Fact]
    public void A_skill_with_no_name_is_refused() =>
        Assert.Throws<ArgumentException>(() => Somebody().Knows("  ", SkillLevel.Strong));

    /// <summary>
    /// A qualification needs whoever issued it.
    /// </summary>
    /// <remarks>
    /// Without the issuer it cannot be verified, and verification is the only reason to
    /// record one — a client asking for evidence during a tender wants to know who says
    /// so, not that somebody typed it in.
    /// </remarks>
    [Fact]
    public void A_qualification_without_an_issuer_is_refused() =>
        Assert.Throws<ArgumentException>(() => Somebody().Holds("AWS", "  ", null, null));

    /// <summary>
    /// A qualification about to lapse says so two months out.
    /// </summary>
    /// <remarks>
    /// Which is enough time to renew one. The whole reason expiry is stored is that a
    /// certification nobody tracks lapses quietly, and the moment it matters is the
    /// moment somebody is asked to produce it.
    /// </remarks>
    [Fact]
    public void A_qualification_warns_before_it_lapses()
    {
        var person = Somebody();

        person.Holds("AWS Solutions Architect", "Amazon", null, new DateOnly(2026, 12, 1));

        var held = Assert.Single(person.Certifications);

        Assert.False(held.LapsesSoon(new DateOnly(2026, 9, 1)));
        Assert.True(held.LapsesSoon(new DateOnly(2026, 10, 15)));
        Assert.False(held.HasLapsed(new DateOnly(2026, 12, 1)));
        Assert.True(held.HasLapsed(new DateOnly(2026, 12, 2)));
    }

    /// <summary>
    /// Changing pay raises an event that carries no amounts.
    /// </summary>
    /// <remarks>
    /// An outbox row is JSON in a table with its own retention and its own readers, and
    /// a salary does not belong in two places. What the event is for is telling the rest
    /// of the system that the terms moved.
    /// </remarks>
    [Fact]
    public void Changing_pay_is_announced_without_the_figure()
    {
        var person = Somebody();
        person.ClearEvents();

        person.Agree(new EmploymentTerms
        {
            Contract = ContractType.Permanent,
            SalaryMinorUnits = 250_000_00,
            SalaryCurrency = "KES",
            Frequency = PayFrequency.Monthly,
        });

        var raised = Assert.Single(person.Events);
        var announced = Assert.IsType<EmployeeTermsChanged>(raised);

        Assert.Equal(ContractType.Permanent, announced.Contract);

        // No property on the event carries an amount.
        Assert.DoesNotContain(
            announced.GetType().GetProperties(),
            property => property.Name.Contains("Salary", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Amount", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Agreeing the same pay again announces nothing.</summary>
    [Fact]
    public void Agreeing_the_same_pay_again_announces_nothing()
    {
        var person = Somebody();

        var terms = new EmploymentTerms
        {
            SalaryMinorUnits = 250_000_00,
            SalaryCurrency = "KES",
        };

        person.Agree(terms);
        person.ClearEvents();
        person.Agree(terms with { NoticeDays = 30 });

        Assert.Empty(person.Events);
    }
}
