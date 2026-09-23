namespace JiranisokoTech.Application.Authorization;

/// <summary>
/// Every permission in the system, and the roles that hold them.
/// </summary>
/// <remarks>
/// One file, deliberately. It drives the seeder, the role screen, the
/// authorization policies and the tests — so a permission cannot exist in the
/// database while being unknown to the code, and a role cannot quietly acquire
/// something nobody declared. A permission that lives only in a seeder is a
/// permission nobody can audit.
///
/// Named <c>resource.action</c> throughout. The resource is the noun a person
/// would use, and the action is one of a small fixed set, so a new module adds
/// rows to a familiar shape rather than inventing its own vocabulary.
///
/// Holding a permission answers "may this kind of person do this kind of thing".
/// Whether they may do it to <em>this particular record</em> is a separate
/// question, answered by resource-based authorization at the point of use.
/// Conflating the two is how systems end up with a permission per record.
/// </remarks>
public static class Permissions
{
    // --- identity and administration ---------------------------------------
    public const string UsersView = "users.view";
    public const string UsersInvite = "users.invite";
    public const string UsersManage = "users.manage";
    public const string UsersAssignRoles = "users.assign_roles";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SettingsManage = "settings.manage";

    // --- people ------------------------------------------------------------
    public const string EmployeesView = "employees.view";
    public const string EmployeesManage = "employees.manage";
    public const string DepartmentsView = "departments.view";
    public const string DepartmentsManage = "departments.manage";

    // --- recruitment -------------------------------------------------------
    public const string RequisitionsCreate = "requisitions.create";
    public const string RequisitionsView = "requisitions.view";
    public const string PostingsManage = "postings.manage";
    public const string CandidatesView = "candidates.view";
    public const string ApplicationsManage = "applications.manage";
    public const string ApplicationsHire = "applications.hire";
    public const string InterviewsSchedule = "interviews.schedule";

    /// <summary>
    /// See an interview and what the panel said.
    /// </summary>
    /// <remarks>
    /// Separate from scheduling one, and the reason is the same one that forced
    /// tasks.view_own into being: an interviewer who may submit a scorecard but
    /// cannot open the interview has been given a permission they cannot use.
    /// </remarks>
    public const string InterviewsView = "interviews.view";
    public const string ScorecardsSubmit = "scorecards.submit";

    // --- clients -----------------------------------------------------------
    public const string ClientsView = "clients.view";
    public const string ClientsManage = "clients.manage";

    // --- time and leave ----------------------------------------------------

    /// <summary>
    /// Log and correct one's own hours.
    /// </summary>
    /// <remarks>
    /// Held by every role that a person actually is, rather than granted. The
    /// same goes for asking for leave and claiming an expense: these are things
    /// an employee does about themselves, and a role that cannot do them
    /// describes somebody who does not work here.
    /// </remarks>
    public const string TimeLogOwn = "time.log_own";
    public const string TimeViewAll = "time.view_all";
    public const string TimeApprove = "time.approve";
    public const string LeaveAsk = "leave.ask";
    public const string LeaveViewAll = "leave.view_all";
    public const string LeaveApprove = "leave.approve";

    // --- money -------------------------------------------------------------
    public const string ExpensesClaim = "expenses.claim";
    public const string ExpensesViewAll = "expenses.view_all";
    public const string ExpensesApprove = "expenses.approve";

    /// <summary>
    /// Mark a claim paid.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same permission as approving one. Approval says the
    /// claim is legitimate; payment says money has left the account. One person
    /// holding both can approve their own reimbursement and record it as paid,
    /// which is the oldest expense fraud there is.
    /// </remarks>
    public const string ExpensesPay = "expenses.pay";
    public const string InvoicesView = "invoices.view";
    public const string InvoicesManage = "invoices.manage";

    /// <summary>
    /// Send an invoice to a client, which freezes it.
    /// </summary>
    /// <remarks>
    /// Separate from drafting one for the same reason payment is separate from
    /// approval: drafting is bookkeeping, sending is a demand for money going
    /// out of the firm under its name.
    /// </remarks>
    public const string InvoicesSend = "invoices.send";

    // --- contracts ---------------------------------------------------------

    /// <summary>
    /// Read what a client has agreed to, and for how much.
    /// </summary>
    /// <remarks>
    /// Separate from clients.view, because a client record is a name and an
    /// address and a contract is a commercial term. Somebody who needs to write
    /// to a client does not thereby need to know what the firm charges them.
    /// </remarks>
    public const string ContractsView = "contracts.view";

    /// <summary>
    /// Agree, extend or terminate one.
    /// </summary>
    /// <remarks>
    /// Deliberately not held by the roles that raise invoices, and it is the
    /// same separation as approving a claim against paying it. A contract is the
    /// authority for a bill; one person holding both can invent the authority
    /// for their own invoice and there is nothing in the record to show they
    /// did. A test asserts no role below the top holds both.
    /// </remarks>
    public const string ContractsManage = "contracts.manage";

    // --- delivery ----------------------------------------------------------
    public const string ProjectsViewAll = "projects.view_all";
    public const string ProjectsViewMember = "projects.view_member";
    public const string ProjectsManage = "projects.manage";
    public const string TasksViewAll = "tasks.view_all";

    /// <summary>
    /// See the board, and on it whatever is yours.
    /// </summary>
    /// <remarks>
    /// Separate from view_all so that an engineer can open the board at all.
    /// Without it the only people who could see any work were the ones who
    /// could see everybody's, which in a delivery system means the people doing
    /// the work cannot look at it.
    /// </remarks>
    public const string TasksViewOwn = "tasks.view_own";
    public const string TasksCreate = "tasks.create";
    public const string TasksAssign = "tasks.assign";
    public const string TasksUpdateOwn = "tasks.update_own";
    public const string TasksSubmit = "tasks.submit";
    public const string TasksReview = "tasks.review";

    /// <summary>
    /// Release accepted work, which puts it in front of the client.
    /// </summary>
    /// <remarks>
    /// Separate from reviewing it, for the reason that separates sending an
    /// invoice from drafting one: review is the team's own judgement that the
    /// work is right, and a release is the firm acting on the outside world with
    /// something that cannot be taken back quietly.
    ///
    /// This constant existed once before and was deleted, because it named a
    /// state the work state machine did not have — a permission guarding
    /// nothing, granted to roles, and asserted by the matrix tests. It is back
    /// with the state under it, and the state machine will not move without it.
    /// </remarks>
    public const string TasksDeploy = "tasks.deploy";

    /// <summary>
    /// See everybody on the staff list, not only your own department.
    /// </summary>
    /// <remarks>
    /// A new permission rather than a narrowing of employees.view, and the difference
    /// matters. employees.view is granted to every department head and every delivery
    /// manager because a roster is meant to be read widely; redefining it as
    /// "your own department" would have silently taken the roster away from all of them.
    ///
    /// So employees.view keeps its meaning of "may open the staff list", and this says how
    /// much of it. Without it a head sees their own department and whatever they head —
    /// which is what they answer for — and nothing else.
    /// </remarks>
    public const string EmployeesViewAll = "employees.view_all";

    /// <summary>
    /// See and set what somebody is paid, and their identity and tax numbers.
    /// </summary>
    /// <remarks>
    /// Separate from employees.manage, which is the permission to keep the staff
    /// record — names, departments, reporting lines, start dates. This one is for the
    /// two things on that record whose disclosure is a different order of problem:
    /// what a person earns, and the numbers that identify them to the state.
    ///
    /// A system where seeing the staff list means seeing everybody's salary is a
    /// system where the staff list is a salary list, and the person who notices that
    /// first is whoever is paid least.
    /// </remarks>
    public const string EmployeesPay = "employees.pay";

    // --- the code repositories ---------------------------------------------

    /// <summary>See which repositories are watched, and what came out of them.</summary>
    public const string ReposView = "repos.view";

    /// <summary>Connect a repository, move it to a project, disconnect it.</summary>
    /// <remarks>
    /// Separate from repos.view because connecting one means holding the secret
    /// that signs its deliveries, and because a repository pointed at the wrong
    /// project silently files everybody's work under the wrong name.
    /// </remarks>
    public const string ReposManage = "repos.manage";

    /// <summary>
    /// Read the delivery log, and replay what failed.
    /// </summary>
    /// <remarks>
    /// Its own permission, and the narrowest of the three, because a delivery
    /// body is the richest thing this system stores about a repository: branch
    /// names, commit messages, the contents of a private codebase's history.
    /// Somebody who may see that a repository exists should not thereby be able
    /// to read everything that has ever happened inside it.
    /// </remarks>
    public const string ReposDeliveries = "repos.deliveries";

    // --- approvals and reporting -------------------------------------------
    public const string ApprovalsDecide = "approvals.decide";
    public const string ReportsView = "reports.view";

    /// <summary>Every declared permission, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UsersView, UsersInvite, UsersManage, UsersAssignRoles, RolesManage,
        AuditView, SettingsManage,

        EmployeesView, EmployeesViewAll, EmployeesManage, EmployeesPay,
        DepartmentsView, DepartmentsManage,

        RequisitionsCreate, RequisitionsView, PostingsManage, CandidatesView,
        ApplicationsManage, ApplicationsHire, InterviewsSchedule, InterviewsView,
        ScorecardsSubmit,

        ClientsView, ClientsManage,

        TimeLogOwn, TimeViewAll, TimeApprove,
        LeaveAsk, LeaveViewAll, LeaveApprove,

        ExpensesClaim, ExpensesViewAll, ExpensesApprove, ExpensesPay,
        InvoicesView, InvoicesManage, InvoicesSend,

        ContractsView, ContractsManage,

        ProjectsViewAll, ProjectsViewMember, ProjectsManage,
        TasksViewAll, TasksViewOwn, TasksCreate, TasksAssign, TasksUpdateOwn, TasksSubmit,
        TasksReview, TasksDeploy,

        ReposView, ReposManage, ReposDeliveries,

        ApprovalsDecide, ReportsView,
    ];
}

/// <summary>
/// The roles, and what each one carries.
/// </summary>
/// <remarks>
/// Additive: somebody may hold several, and what they can do is the union.
/// <see cref="Interviewer"/> is designed to be worn alongside another role
/// rather than on its own.
/// </remarks>
public static class Roles
{
    public const string Owner = "owner";
    public const string Administrator = "administrator";
    public const string HumanResources = "hr";
    public const string DepartmentHead = "department_head";
    public const string ProjectManager = "project_manager";
    public const string Developer = "developer";
    public const string Interviewer = "interviewer";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Matrix { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            /*
             * The owner holds everything, including the permissions that grant
             * permissions. There is exactly one thing preventing an accident
             * here, and it is not a permission: the seeder refuses to leave the
             * system with no owner.
             */
            [Owner] = Permissions.All,

            [Administrator] = Permissions.All
                .Where(p => p != Permissions.RolesManage)
                .ToList(),

            [HumanResources] =
            [
                Permissions.UsersView, Permissions.UsersInvite, Permissions.UsersManage,
                Permissions.EmployeesView, Permissions.EmployeesViewAll,
                Permissions.EmployeesManage,

                // HR keeps the contracts and runs the payroll, so HR holds this.
                // A head of department deliberately does not: they decide what their
                // team does, not what it costs.
                Permissions.EmployeesPay,

                Permissions.DepartmentsView,

                // HR sits at step one of the hiring chain, so HR must be able to
                // decide one. Without this the chain opens on a step nobody in
                // the firm can answer.
                Permissions.ApprovalsDecide,

                Permissions.RequisitionsView, Permissions.PostingsManage,
                Permissions.CandidatesView, Permissions.ApplicationsManage,
                Permissions.ApplicationsHire, Permissions.InterviewsSchedule,
                Permissions.InterviewsView,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // Leave is HR's book to keep, and hours are how absence is
                // reconciled against it.
                Permissions.LeaveViewAll, Permissions.LeaveApprove,
                Permissions.TimeViewAll,

                Permissions.AuditView, Permissions.ReportsView,
            ],

            [DepartmentHead] =
            [
                Permissions.DepartmentsView, Permissions.EmployeesView, Permissions.UsersView,
                Permissions.ProjectsViewAll,

                // A head gives their own team work and releases what it
                // finishes. Review without create means they can only ever react
                // to work somebody else set.
                //
                // The release is theirs alone among the delivery roles, which is
                // how the firm already works: a delivery manager runs the board
                // and bills the work, and a head of department says what goes
                // out. The same split as drafting an invoice and sending it.
                Permissions.TasksViewAll, Permissions.TasksViewOwn, Permissions.TasksCreate,
                Permissions.TasksAssign, Permissions.TasksReview, Permissions.TasksDeploy,

                // A head releases what the team finishes, and the evidence that
                // it is finished is a merged pull request. Seeing the board
                // without seeing that is being asked to sign for work on
                // somebody's word.
                Permissions.ReposView,

                Permissions.ApprovalsDecide,
                Permissions.RequisitionsCreate, Permissions.RequisitionsView,
                Permissions.CandidatesView, Permissions.InterviewsSchedule,
                Permissions.InterviewsView, Permissions.ScorecardsSubmit,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // A head signs off their team's hours, absence and spending.
                // Not payment: that is the office's job, and a head who could
                // both approve and pay could reimburse themselves.
                Permissions.TimeViewAll, Permissions.TimeApprove,
                Permissions.LeaveViewAll, Permissions.LeaveApprove,
                Permissions.ExpensesViewAll, Permissions.ExpensesApprove,

                Permissions.AuditView, Permissions.ReportsView,
            ],

            [ProjectManager] =
            [
                Permissions.DepartmentsView, Permissions.EmployeesView,

                // A delivery manager staffs work from across the firm, so their roster
                // cannot stop at the department they happen to sit in.
                Permissions.EmployeesViewAll,

                Permissions.UsersView,
                Permissions.ProjectsViewAll, Permissions.ProjectsManage,
                Permissions.TasksViewAll, Permissions.TasksViewOwn, Permissions.TasksCreate,
                Permissions.TasksAssign, Permissions.TasksReview,
                Permissions.ReposView,
                Permissions.ApprovalsDecide,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // A delivery manager bills the work, so they hold the clients
                // and the draft invoices. Sending one is somebody else's.
                Permissions.ClientsView, Permissions.ClientsManage,
                Permissions.TimeViewAll, Permissions.TimeApprove,
                Permissions.InvoicesView, Permissions.InvoicesManage,

                // They read the contract because they bill against it, and they
                // do not write it. Agreeing what a client may be charged and
                // charging them are the same separation as approving a claim and
                // paying it: one pair of hands holding both can invent the
                // authority for its own invoice.
                Permissions.ContractsView,

                Permissions.AuditView, Permissions.ReportsView,
            ],

            [Developer] =
            [
                Permissions.ProjectsViewMember,

                // An engineer can open the board and see what is theirs. This
                // is a delivery system; the people doing the delivering are its
                // main users, and a board they cannot look at is not one.
                Permissions.TasksViewOwn,
                Permissions.TasksUpdateOwn, Permissions.TasksSubmit,

                // The repositories are where an engineer's work actually
                // happens, and the whole point of watching them is that the
                // engineer does not have to report what they did. Withholding
                // the view would mean the one group whose work is being
                // recorded is the one group that cannot check the record.
                Permissions.ReposView,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,
            ],

            [Interviewer] =
            [
                // Worn alongside another role, so no self-service here — the
                // other role carries it. But scoring an interview you cannot
                // open is the tasks.view_own mistake all over again.
                Permissions.CandidatesView, Permissions.InterviewsView,
                Permissions.ScorecardsSubmit,
            ],
        };

    public static IReadOnlyList<string> All { get; } = Matrix.Keys.ToList();

    public static IReadOnlyList<string> PermissionsFor(string role) =>
        Matrix.TryGetValue(role, out var permissions) ? permissions : [];
}
