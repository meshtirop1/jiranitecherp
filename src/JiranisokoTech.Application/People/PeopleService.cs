using JiranisokoTech.Domain.Common;
using JiranisokoTech.Domain.People;

namespace JiranisokoTech.Application.People;

/// <summary>
/// Everything anybody does to a person or a department.
/// </summary>
/// <remarks>
/// The only way in. The pages, the API and anything added later call these
/// methods; none of them contains a rule, which is what makes a second front end
/// cheap rather than a second implementation of the business.
///
/// The entities hold the rules that need only themselves — nobody reports to
/// themselves, nobody leaves before starting. This holds the ones that need more
/// than one row: a reporting line that would close a loop, a handle already
/// taken, a head who does not work here.
/// </remarks>
public sealed class PeopleService(IPeopleRepository people, Abstractions.IClock clock)
{
    public async Task<Department> OpenDepartmentAsync(
        string name,
        string? slug = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var handle = Slug.From(slug ?? name);

        if (await people.SlugTakenAsync(handle.Value, null, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another department already uses the address '{handle.Value}'. "
                + "Give this one a different short name.");
        }

        var department = Department.Open(name, handle.Value, description);

        people.Add(department);
        await people.SaveAsync(cancellationToken);

        return department;
    }

    /// <summary>
    /// Put somebody in charge of a department.
    /// </summary>
    /// <remarks>
    /// The head must work here and must not have left. Somebody who has left
    /// stays on their past work by design, and a system that lets them keep a
    /// post keeps granting them the role that comes with it.
    /// </remarks>
    public async Task AppointHeadAsync(
        Guid departmentId, Guid? employeeId, CancellationToken cancellationToken = default)
    {
        var department = await Required(departmentId, cancellationToken);

        if (employeeId is { } id)
        {
            var head = await people.FindAsync(id, cancellationToken)
                ?? throw new InvalidOperationException("That person is not on the staff list.");

            if (!head.IsEmployed)
            {
                throw new InvalidOperationException(
                    $"{head.FullName} has left, so they cannot be put in charge of "
                    + $"{department.Name}.");
            }
        }

        department.AppointHead(employeeId);
        await people.SaveAsync(cancellationToken);
    }

    public async Task CloseDepartmentAsync(
        Guid departmentId, CancellationToken cancellationToken = default)
    {
        var department = await Required(departmentId, cancellationToken);

        department.Close();
        await people.SaveAsync(cancellationToken);
    }

    public async Task ReopenDepartmentAsync(
        Guid departmentId, CancellationToken cancellationToken = default)
    {
        var department = await Required(departmentId, cancellationToken);

        department.Reopen();
        await people.SaveAsync(cancellationToken);
    }

    public async Task RenameDepartmentAsync(
        Guid departmentId,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var department = await Required(departmentId, cancellationToken);

        department.Rename(name);
        department.Describe(description);

        await people.SaveAsync(cancellationToken);
    }

    public async Task<Employee> HireAsync(
        string fullName,
        DateOnly startsOn,
        Guid? departmentId = null,
        string? jobTitle = null,
        Guid? reportsToId = null,
        CancellationToken cancellationToken = default)
    {
        if (departmentId is { } department
            && !await people.DepartmentExistsAsync(department, cancellationToken))
        {
            throw new InvalidOperationException("That department does not exist.");
        }

        var employee = Employee.Hire(fullName, startsOn, departmentId, jobTitle);

        // Set before saving, so a new joiner never exists for even one commit
        // with nobody answerable for them. A new person cannot close a loop —
        // nothing points at them yet — so the graph does not need consulting.
        if (reportsToId is { } manager)
        {
            if (await people.FindAsync(manager, cancellationToken) is null)
            {
                throw new InvalidOperationException("That manager is not on the staff list.");
            }

            employee.ReportsTo(manager);
        }

        people.Add(employee);
        await people.SaveAsync(cancellationToken);

        return employee;
    }

    /// <summary>
    /// Change who somebody answers to.
    /// </summary>
    /// <remarks>
    /// The loop check is the whole reason this is a service call rather than a
    /// property. A to B to C to A is not a strange organisation, it is data that
    /// nothing can draw and that makes "who approves this?" run forever.
    /// </remarks>
    public async Task SetReportingLineAsync(
        Guid employeeId, Guid? managerId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        if (managerId is { } id)
        {
            var manager = await people.FindAsync(id, cancellationToken)
                ?? throw new InvalidOperationException("That manager is not on the staff list.");

            if (!manager.IsEmployed)
            {
                throw new InvalidOperationException(
                    $"{manager.FullName} has left, so nobody can be made to report to them.");
            }

            var lines = await people.ReportingLinesAsync(cancellationToken);

            if (ReportingLine.WouldCycle(employeeId, id, person => lines.GetValueOrDefault(person)))
            {
                throw new InvalidOperationException(
                    $"{employee.FullName} cannot report to {manager.FullName}: it would close a "
                    + "loop in the reporting lines.");
            }
        }

        employee.ReportsTo(managerId);
        await people.SaveAsync(cancellationToken);
    }

    public async Task MoveAsync(
        Guid employeeId, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        if (departmentId is { } id && !await people.DepartmentExistsAsync(id, cancellationToken))
        {
            throw new InvalidOperationException("That department does not exist.");
        }

        employee.MoveTo(departmentId);
        await people.SaveAsync(cancellationToken);
    }

    public async Task StartAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Start();
        await people.SaveAsync(cancellationToken);
    }

    public async Task SuspendAsync(
        Guid employeeId, string reason, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Suspend(reason);
        await people.SaveAsync(cancellationToken);
    }

    public async Task ReinstateAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Reinstate();
        await people.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record that somebody has left, and tidy up what depended on them.
    /// </summary>
    /// <remarks>
    /// Two consequences, done here because neither belongs to the leaver:
    ///
    /// Their reports are moved up to whoever the leaver answered to. Left alone
    /// they would answer to somebody who has gone, and the next approval routed
    /// up the chain would stop at a person who cannot act. Moving them up is a
    /// guess, but it is the guess an administrator would make, and the People
    /// page can correct it.
    ///
    /// Any department they headed is left without a head, rather than with one
    /// who is no longer here. That vacancy is visible on the departments page,
    /// which is the point — a post nobody noticed was empty is worse than one
    /// showing as empty.
    /// </remarks>
    public async Task<int> RecordLeavingAsync(
        Guid employeeId,
        DateOnly on,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Leave(on, reason);

        var reports = await people.DirectReportsAsync(employeeId, cancellationToken);

        foreach (var report in reports)
        {
            report.ReportsTo(employee.ReportsToId);
        }

        foreach (var department in await people.HeadedByAsync(employeeId, cancellationToken))
        {
            department.AppointHead(null);
        }

        /*
         * The checklist is started here rather than being something somebody remembers to
         * create. Section 9 released a leaver's work and left the laptop, the building pass
         * and the sign-in alive indefinitely with nothing anywhere saying so — and the way
         * that happens is never a decision, it is an omission.
         *
         * Reused rather than duplicated if one already exists, because somebody who leaves,
         * is reinstated and leaves again must not end up with two open checklists: the
         * laptop then appears on neither.
         */
        if (await people.OffboardingForAsync(employeeId, cancellationToken) is { } already)
        {
            already.LeavesOn(on);
        }
        else
        {
            people.Add(Offboarding.Begin(employeeId, on, clock.Now));
        }

        await people.SaveAsync(cancellationToken);

        return reports.Count;
    }

    /// <summary>Note that the firm lent somebody something they have to give back.</summary>
    public async Task LentAsync(
        Guid employeeId,
        AssetKind kind,
        string description,
        string? identifier,
        CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.Lent(kind, description, identifier);
        await people.SaveAsync(cancellationToken);
    }

    public async Task ReturnedAsync(
        Guid employeeId,
        Guid assetId,
        DateOnly on,
        string? condition,
        CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.Returned(assetId, on, condition);
        await people.SaveAsync(cancellationToken);
    }

    public async Task NotLentAsync(
        Guid employeeId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.Forget(assetId);
        await people.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Record that a leaver's sign-in has been closed.
    /// </summary>
    /// <remarks>
    /// Marked by a person rather than done on the leaving date. People leave on a date and
    /// then work a handover week, so an account cut off at midnight locks somebody out
    /// mid-sentence — and a leaving date entered wrongly, which happens, would destroy
    /// access with nobody having decided anything.
    /// </remarks>
    public async Task AccessRemovedAsync(
        Guid employeeId, Guid byEmployeeId, CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.AccessRemoved(byEmployeeId, clock.Now);
        await people.SaveAsync(cancellationToken);
    }

    public async Task ExitInterviewHeldAsync(
        Guid employeeId, string? notes, CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.ExitInterviewHeld(notes, clock.Now);
        await people.SaveAsync(cancellationToken);
    }

    /// <summary>Say the departure is dealt with.</summary>
    public async Task CompleteOffboardingAsync(
        Guid employeeId, CancellationToken cancellationToken = default)
    {
        var offboarding = await RequiredOffboarding(employeeId, cancellationToken);

        offboarding.Complete(clock.Now);
        await people.SaveAsync(cancellationToken);
    }

    private async Task<Offboarding> RequiredOffboarding(
        Guid employeeId, CancellationToken cancellationToken) =>
        await people.OffboardingForAsync(employeeId, cancellationToken)
        ?? throw new InvalidOperationException(
            "Nothing is being offboarded for that person. Record that they have left first.");

    public async Task LinkAccountAsync(
        Guid employeeId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.LinkAccount(accountId);
        await people.SaveAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        Guid employeeId,
        string fullName,
        string? jobTitle,
        int weeklyCapacityHours,
        CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Rename(fullName);
        employee.SetJobTitle(jobTitle);
        employee.SetWeeklyCapacity(weeklyCapacityHours);

        await people.SaveAsync(cancellationToken);
    }

    /// <summary>Record how to reach somebody, and where they are.</summary>
    public async Task RecordDetailsAsync(
        Guid employeeId, PersonalDetails details, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Record(details);
        await people.SaveAsync(cancellationToken);
    }

    /// <summary>Record who to call if something happens at work.</summary>
    public async Task RecordEmergencyAsync(
        Guid employeeId, EmergencyContact contact, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Record(contact);
        await people.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// Agree what somebody is paid and on what terms.
    /// </summary>
    /// <remarks>
    /// Refuses a fixed-term contract with no end date, because a fixed term without one
    /// is a permanent contract that nobody meant to offer — and the difference shows up
    /// years later in a dispute about notice.
    /// </remarks>
    public async Task AgreeTermsAsync(
        Guid employeeId, EmploymentTerms terms, CancellationToken cancellationToken = default)
    {
        if (terms.Contract == ContractType.FixedTerm && terms.EndsOn is null)
        {
            throw new InvalidOperationException(
                "A fixed-term contract needs the date it ends. Without one it is a permanent "
                + "contract that nobody meant to offer.");
        }

        if (terms is { SalaryMinorUnits: not null, SalaryCurrency: null or "" })
        {
            throw new InvalidOperationException(
                "A salary needs a currency. A bare number is not an amount of money.");
        }

        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Agree(terms);
        await people.SaveAsync(cancellationToken);
    }

    public async Task RecordSkillAsync(
        Guid employeeId,
        string name,
        SkillLevel level,
        CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Knows(name, level);
        await people.SaveAsync(cancellationToken);
    }

    public async Task RemoveSkillAsync(
        Guid employeeId, string name, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Forgets(name);
        await people.SaveAsync(cancellationToken);
    }

    public async Task RecordCertificationAsync(
        Guid employeeId,
        string name,
        string issuer,
        DateOnly? issuedOn,
        DateOnly? expiresOn,
        CancellationToken cancellationToken = default)
    {
        if (issuedOn is { } issued && expiresOn is { } expires && expires < issued)
        {
            throw new InvalidOperationException(
                "A certification cannot expire before it was issued.");
        }

        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.Holds(name, issuer, issuedOn, expiresOn);
        await people.SaveAsync(cancellationToken);
    }

    public async Task RemoveCertificationAsync(
        Guid employeeId, Guid certificationId, CancellationToken cancellationToken = default)
    {
        var employee = await RequiredEmployee(employeeId, cancellationToken);

        employee.NoLongerHolds(certificationId);
        await people.SaveAsync(cancellationToken);
    }

    private async Task<Employee> RequiredEmployee(Guid id, CancellationToken cancellationToken) =>
        await people.FindAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That person is not on the staff list.");

    private async Task<Department> Required(Guid id, CancellationToken cancellationToken) =>
        await people.FindDepartmentAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("That department does not exist.");
}
