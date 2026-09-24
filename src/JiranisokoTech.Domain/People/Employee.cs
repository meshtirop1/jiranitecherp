using JiranisokoTech.Domain.Audit;
using JiranisokoTech.Domain.Common;

namespace JiranisokoTech.Domain.People;

/// <summary>
/// Somebody who works here.
/// </summary>
/// <remarks>
/// Deliberately not the same thing as an account. An account is how a person
/// signs in and lives with ASP.NET Identity in the infrastructure; this is the
/// person the business has a relationship with, and it lives in the domain where
/// the rules about them can be tested without a database or a web server in the
/// room.
///
/// Keeping them apart costs one nullable column and buys three things the single
/// row cannot do: somebody can be on the payroll before their login exists;
/// service accounts and, later, client logins can exist without pretending to be
/// staff; and a person who leaves keeps their name on everything they did while
/// their account is closed the same afternoon.
///
/// The department and the line manager are separate facts on purpose. An
/// engineer can answer to a team lead rather than to the head of engineering,
/// and a system that derives one from the other cannot express that at all.
/// </remarks>
public sealed class Employee : Entity, IAuditable
{
    private readonly List<Skill> _skills = [];
    private readonly List<Certification> _certifications = [];

    private Employee()
    {
        FullName = string.Empty;
    }

    private Employee(string fullName, DateOnly startsOn, Guid? departmentId, string? jobTitle)
    {
        FullName = Require(fullName);
        StartsOn = startsOn;
        DepartmentId = departmentId;
        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : jobTitle.Trim();
        Status = EmploymentStatus.Invited;

        Raise(new EmployeeHired(Id, FullName, departmentId, startsOn));
    }

    public static Employee Hire(
        string fullName,
        DateOnly startsOn,
        Guid? departmentId = null,
        string? jobTitle = null) =>
        new(fullName, startsOn, departmentId, jobTitle);

    public string FullName { get; private set; }

    public string? JobTitle { get; private set; }

    /// <summary>
    /// The account this person signs in with, if they have one.
    /// </summary>
    /// <remarks>
    /// Null is ordinary, not a gap waiting to be filled: somebody joining next
    /// month has a record before they have a login, and not every employee ever
    /// needs one.
    /// </remarks>
    public Guid? AccountId { get; private set; }

    public Guid? DepartmentId { get; private set; }

    /// <summary>Who they answer to. Null at the top of the firm, and nowhere else.</summary>
    public Guid? ReportsToId { get; private set; }

    public EmploymentStatus Status { get; private set; }

    /// <summary>Their first day. In the future for somebody who has not started.</summary>
    public DateOnly StartsOn { get; private set; }

    public DateOnly? LeftOn { get; private set; }

    /// <summary>
    /// Hours a week this person is available for planned work.
    /// </summary>
    /// <remarks>
    /// Capacity, not a contract. It is what scheduling divides by, so it is kept
    /// here rather than inferred from a job title — a part-timer and a
    /// contractor are both normal and both wrong at forty.
    /// </remarks>
    public int WeeklyCapacityHours { get; private set; } = 40;

    /// <summary>Can be given work: here, started, and not stopped.</summary>
    public bool IsAssignable => Status == EmploymentStatus.Active;

    /// <summary>Still employed, whatever today looks like.</summary>
    public bool IsEmployed => Status is not EmploymentStatus.Left;

    public void Start()
    {
        if (Status != EmploymentStatus.Invited)
        {
            throw new InvalidOperationException(
                $"{FullName} cannot start: they are already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = EmploymentStatus.Active;

        Raise(new EmployeeStarted(Id, FullName));
    }

    /// <summary>
    /// They have left.
    /// </summary>
    /// <remarks>
    /// The record stays and the name stays on their work. Deleting a leaver
    /// turns every task, approval and audit entry they touched into "unknown
    /// user", which is how a system loses the half of its history that people
    /// actually read.
    /// </remarks>
    public void Leave(DateOnly on, string reason)
    {
        if (Status == EmploymentStatus.Left)
        {
            throw new InvalidOperationException($"{FullName} has already left.");
        }

        if (on < StartsOn)
        {
            throw new ArgumentException(
                $"{FullName} cannot leave on {on:d MMM yyyy}, before starting on "
                + $"{StartsOn:d MMM yyyy}.",
                nameof(on));
        }

        Status = EmploymentStatus.Left;
        LeftOn = on;

        Raise(new EmployeeLeft(Id, FullName, on, Require(reason, nameof(reason))));
    }

    public void Suspend(string reason)
    {
        if (Status != EmploymentStatus.Active)
        {
            throw new InvalidOperationException(
                $"Only somebody active can be suspended; {FullName} is "
                + $"{Status.ToString().ToLowerInvariant()}.");
        }

        Status = EmploymentStatus.Suspended;

        Raise(new EmployeeSuspended(Id, Require(reason, nameof(reason))));
    }

    /// <summary>
    /// Back at work, from suspension or from having left.
    /// </summary>
    /// <remarks>
    /// Re-hiring somebody who left is a real thing and is done on the record
    /// they already have, so that their history stays one person rather than
    /// two. The leaving date is cleared, because it is no longer true.
    /// </remarks>
    public void Reinstate()
    {
        if (Status is EmploymentStatus.Active or EmploymentStatus.Invited)
        {
            throw new InvalidOperationException(
                $"{FullName} is {Status.ToString().ToLowerInvariant()} and has nothing to be "
                + "reinstated from.");
        }

        Status = EmploymentStatus.Active;
        LeftOn = null;

        Raise(new EmployeeReinstated(Id));
    }

    public void MoveTo(Guid? departmentId)
    {
        if (DepartmentId == departmentId)
        {
            return;
        }

        var from = DepartmentId;
        DepartmentId = departmentId;

        Raise(new EmployeeMoved(Id, from, departmentId));
    }

    /// <summary>
    /// Set who this person answers to.
    /// </summary>
    /// <remarks>
    /// Reporting to themselves is refused here because it needs nothing but this
    /// object to detect. A longer loop — A to B to C to A — needs the rest of the
    /// graph and is refused by <see cref="ReportingLine"/>, which the caller
    /// consults first.
    /// </remarks>
    public void ReportsTo(Guid? managerId)
    {
        if (managerId == Id)
        {
            throw new InvalidOperationException($"{FullName} cannot report to themselves.");
        }

        if (ReportsToId == managerId)
        {
            return;
        }

        var from = ReportsToId;
        ReportsToId = managerId;

        Raise(new ReportingLineChanged(Id, from, managerId));
    }

    public void LinkAccount(Guid accountId)
    {
        if (AccountId == accountId)
        {
            return;
        }

        if (AccountId is not null)
        {
            throw new InvalidOperationException(
                $"{FullName} already has an account. Unlink the old one first, so that the "
                + "change is a deliberate act and not a typo.");
        }

        AccountId = accountId;

        Raise(new EmployeeAccountLinked(Id, accountId));
    }

    public void UnlinkAccount() => AccountId = null;

    public void Rename(string fullName) => FullName = Require(fullName);

    public void SetJobTitle(string? jobTitle) =>
        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : jobTitle.Trim();

    /// <summary>
    /// How many hours a week they are available.
    /// </summary>
    /// <remarks>
    /// Bounded by the number of hours in a week, which is the only limit this
    /// object can be sure of. A figure above it is always a typo, and scheduling
    /// would quietly plan around it.
    /// </remarks>
    public void SetWeeklyCapacity(int hours)
    {
        if (hours is < 0 or > 168)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hours), hours, "A week has 168 hours, and capacity cannot be negative.");
        }

        WeeklyCapacityHours = hours;
    }

    /// <summary>
    /// Nothing here is a secret, so nothing is withheld from the trail.
    /// </summary>
    /// <remarks>
    /// Named anyway rather than left to the default. An empty set says somebody
    /// thought about it; a missing member says nobody did, and the next person to
    /// add a national insurance number would have no prompt to reconsider.
    /// </remarks>
    /// <summary>How to reach them, and where they are.</summary>
    public PersonalDetails Details { get; private set; } = PersonalDetails.Empty;

    /// <summary>Who to call if something happens at work.</summary>
    public EmergencyContact Emergency { get; private set; } = EmergencyContact.Empty;

    /// <summary>
    /// What they are paid, and on what terms.
    /// </summary>
    /// <remarks>
    /// Behind employees.pay everywhere it appears. The permission to see that somebody
    /// works here is not the permission to see what they earn, and a system where those
    /// are one permission is a system where the staff list is a salary list.
    /// </remarks>
    public EmploymentTerms Terms { get; private set; } = EmploymentTerms.Empty;

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<Skill> Skills => _skills.ToList();

    /// <remarks>Returns a copy — see the note on Invoice.Lines for why.</remarks>
    public IReadOnlyList<Certification> Certifications => _certifications.ToList();

    public void Record(PersonalDetails details) => Details = details;

    /// <summary>
    /// Empty the personal data around the name, at the subject's request.
    /// </summary>
    /// <remarks>
    /// Section 55, and the shape of it is the section's central decision: <b>erasure means
    /// emptying the shell, not removing the person.</b>
    ///
    /// The name, job title, dates, department and status stay. <see cref="Leave"/> already says
    /// why in this same file — deleting a leaver turns every task, approval, incident timeline
    /// and audit entry they touched into "unknown user", which destroys the firm's own record of
    /// what happened rather than the person's privacy. What goes is everything that is personal
    /// to them and serves no retained purpose: telephone, personal email, address, date of birth,
    /// identity and tax numbers, and next of kin.
    ///
    /// <b>Refused while they still work here.</b> Erasure is a right against data held without a
    /// continuing basis; a current employee's contact details are held under the employment
    /// contract, and emptying them would leave the firm unable to reach somebody it employs. The
    /// refusal says so rather than silently doing nothing.
    ///
    /// The complex properties are replaced wholesale rather than mutated, because a replacement
    /// is the only assignment EF sees — and because the audit capture walks complex properties
    /// specifically so that this change is recorded as having happened, with the values withheld.
    /// </remarks>
    public void Forget()
    {
        if (Status != EmploymentStatus.Left)
        {
            throw new InvalidOperationException(
                $"{FullName} still works here. Their contact details are held under the "
                + "employment contract, so there is no basis on which to erase them — and the "
                + "firm would be unable to reach somebody it employs.");
        }

        /*
         * Replaced, not mutated. Anything not named here is deliberately kept: the location and
         * time zone describe where the work was done rather than who did it, and are what a
         * historical timesheet is read against.
         */
        Details = Details with
        {
            Phone = null,
            PersonalEmail = null,
            DateOfBirth = null,
            NationalId = null,
            TaxNumber = null,
            Address = null,
        };

        Emergency = EmergencyContact.Empty;
    }

    public void Record(EmergencyContact contact) => Emergency = contact;

    /// <summary>
    /// Set what somebody is paid.
    /// </summary>
    /// <remarks>
    /// Raises an event of its own rather than folding into a general change, because
    /// this is the one field on a staff record where "who changed it, when, and from
    /// what" is a question somebody will eventually be asked under oath. The amounts
    /// are deliberately not in the event: an outbox row is JSON in a table with its own
    /// retention, and a salary does not belong in two places.
    /// </remarks>
    public void Agree(EmploymentTerms terms)
    {
        var wasPaid = Terms.SalaryMinorUnits;

        Terms = terms;

        if (wasPaid != terms.SalaryMinorUnits)
        {
            Raise(new EmployeeTermsChanged(Id, FullName, terms.Contract, terms.Frequency));
        }
    }

    /// <summary>
    /// Say somebody has a skill, or change how well.
    /// </summary>
    /// <remarks>
    /// Replaces rather than adding a second row for the same name. Somebody who was
    /// learning Rust last year and is strong at it now has one skill, and a list that
    /// showed both would be a list nobody trusts.
    /// </remarks>
    public void Knows(string name, SkillLevel level)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A skill has to have a name.", nameof(name));
        }

        _skills.RemoveAll(skill =>
            string.Equals(skill.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        _skills.Add(Skill.Of(name, level));
    }

    public void Forgets(string name) =>
        _skills.RemoveAll(skill =>
            string.Equals(skill.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Holds(string name, string issuer, DateOnly? issuedOn, DateOnly? expiresOn)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException(
                "A certification needs a name and whoever issued it. Without the issuer it "
                + "cannot be verified, which is the only reason to record one.",
                nameof(name));
        }

        _certifications.RemoveAll(one =>
            string.Equals(one.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(one.Issuer, issuer.Trim(), StringComparison.OrdinalIgnoreCase));

        _certifications.Add(Certification.Of(name, issuer, issuedOn, expiresOn));
    }

    public void NoLongerHolds(Guid certificationId) =>
        _certifications.RemoveAll(one => one.Id == certificationId);

    /// <summary>
    /// The identity and tax numbers never reach the audit trail.
    /// </summary>
    /// <remarks>
    /// These are the two fields in this system whose disclosure does a person lasting
    /// harm rather than embarrassing the firm. The trail is append-only and kept for
    /// years, so a national identity number written into it is written there for good —
    /// and it would be a second copy, in a table read by more people than the staff
    /// record is.
    ///
    /// The salary is excluded for a different reason: not because it is dangerous, but
    /// because the trail is read by anybody holding audit.view, and a change history
    /// containing every salary the firm has ever paid is a payroll report by another
    /// name. That the terms changed is recorded; what they changed to is on the record
    /// itself, behind employees.pay.
    /// </remarks>
    public static IReadOnlySet<string> AuditExcludes { get; } =
        new HashSet<string> { nameof(Details), nameof(Emergency), nameof(Terms) };

    private static string Require(string value, string parameter = "fullName") =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("This cannot be blank.", parameter)
            : value.Trim();
}
