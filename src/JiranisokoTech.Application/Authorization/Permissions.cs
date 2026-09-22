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
    public const string ScorecardsSubmit = "scorecards.submit";

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
    public const string TasksDeploy = "tasks.deploy";

    // --- approvals and reporting -------------------------------------------
    public const string ApprovalsDecide = "approvals.decide";
    public const string ReportsView = "reports.view";

    /// <summary>Every declared permission, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UsersView, UsersInvite, UsersManage, UsersAssignRoles, RolesManage,
        AuditView, SettingsManage,

        EmployeesView, EmployeesManage, DepartmentsView, DepartmentsManage,

        RequisitionsCreate, RequisitionsView, PostingsManage, CandidatesView,
        ApplicationsManage, ApplicationsHire, InterviewsSchedule, ScorecardsSubmit,

        ProjectsViewAll, ProjectsViewMember, ProjectsManage,
        TasksViewAll, TasksViewOwn, TasksCreate, TasksAssign, TasksUpdateOwn, TasksSubmit,
        TasksReview, TasksDeploy,

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
                Permissions.EmployeesView, Permissions.EmployeesManage,
                Permissions.DepartmentsView,

                // HR sits at step one of the hiring chain, so HR must be able to
                // decide one. Without this the chain opens on a step nobody in
                // the firm can answer.
                Permissions.ApprovalsDecide,

                Permissions.RequisitionsView, Permissions.PostingsManage,
                Permissions.CandidatesView, Permissions.ApplicationsManage,
                Permissions.ApplicationsHire, Permissions.InterviewsSchedule,
                Permissions.AuditView, Permissions.ReportsView,
            ],

            [DepartmentHead] =
            [
                Permissions.DepartmentsView, Permissions.EmployeesView, Permissions.UsersView,
                Permissions.ProjectsViewAll,

                // A head gives their own team work and releases what it
                // finishes. Review without create means they can only ever react
                // to work somebody else set.
                Permissions.TasksViewAll, Permissions.TasksViewOwn, Permissions.TasksCreate,
                Permissions.TasksAssign, Permissions.TasksReview, Permissions.TasksDeploy,

                Permissions.ApprovalsDecide,
                Permissions.RequisitionsCreate, Permissions.RequisitionsView,
                Permissions.CandidatesView, Permissions.InterviewsSchedule,
                Permissions.ScorecardsSubmit,
                Permissions.AuditView, Permissions.ReportsView,
            ],

            [ProjectManager] =
            [
                Permissions.DepartmentsView, Permissions.EmployeesView, Permissions.UsersView,
                Permissions.ProjectsViewAll, Permissions.ProjectsManage,
                Permissions.TasksViewAll, Permissions.TasksViewOwn, Permissions.TasksCreate,
                Permissions.TasksAssign, Permissions.TasksReview,
                Permissions.ApprovalsDecide,
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
            ],

            [Interviewer] =
            [
                Permissions.CandidatesView, Permissions.ScorecardsSubmit,
            ],
        };

    public static IReadOnlyList<string> All { get; } = Matrix.Keys.ToList();

    public static IReadOnlyList<string> PermissionsFor(string role) =>
        Matrix.TryGetValue(role, out var permissions) ? permissions : [];
}
