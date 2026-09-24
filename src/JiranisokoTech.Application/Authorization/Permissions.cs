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

    /// <summary>
    /// See the teams and who is on them.
    /// </summary>
    /// <remarks>
    /// Separate from departments.view, because they answer different questions and are held by
    /// different people. A department is a piece of the firm's structure; a team is who is
    /// working on what this month, and that is something everybody who works here needs to be
    /// able to look up — an engineer who cannot find out which team owns a service has to ask
    /// somebody, and asking somebody is the thing this replaces.
    /// </remarks>
    public const string TeamsView = "teams.view";

    /// <summary>
    /// Form a team, and move people on and off it.
    /// </summary>
    /// <remarks>
    /// Wider than departments.manage on purpose. Opening a department is a change to the shape
    /// of the firm and belongs to HR and the owner; putting three people on a team for a
    /// quarter is a delivery decision, and a system where a delivery manager has to raise a
    /// ticket with HR to do it is one where the teams in the system stop matching the teams in
    /// the building.
    /// </remarks>
    public const string TeamsManage = "teams.manage";

    /// <summary>
    /// Say something to everybody who works here.
    /// </summary>
    /// <remarks>
    /// Its own permission and a narrow one. A firm-wide announcement is the single message in
    /// this system that cannot be unsaid — everybody sees it, it is attributed to whoever posted
    /// it, and taking it down leaves a record that it was up. Reading the board needs no
    /// permission at all, for the reason the notice centre needs none: it is addressed to
    /// everybody who works here, and a door to a room everybody may enter is just a door.
    /// </remarks>
    public const string AnnouncementsPost = "announcements.post";

    /// <summary>
    /// Open the performance area: your own goals, and your own review once it has been shared.
    /// </summary>
    /// <remarks>
    /// Held by everybody, and it is not a privilege — a performance system whose subject cannot
    /// read their own goals is a file kept about somebody. It says nothing about anybody else's:
    /// that is the next two.
    /// </remarks>
    public const string GoalsView = "goals.view";

    /// <summary>
    /// Set and close goals with other people, run review cycles, write the manager's half.
    /// </summary>
    /// <remarks>
    /// One permission rather than three, because the three are the same job: the person who sets
    /// a goal with somebody is the person who closes it with them and writes about it at review
    /// time, and splitting them would produce a manager who can start a conversation and not
    /// finish it.
    ///
    /// It does not carry the right to read everybody's. Whose reviews somebody may open is
    /// decided by the reporting line — see Reaches.PerformanceAsync — because performance is a
    /// line-management relationship and not a departmental one. A head of department who is not
    /// in somebody's chain has no business in their appraisal.
    /// </remarks>
    public const string GoalsManage = "goals.manage";

    /// <summary>
    /// Read anybody's goals and shared reviews.
    /// </summary>
    /// <remarks>
    /// HR, and nobody else by default. A separate permission for the same reason
    /// employees.view_all is separate from employees.view: the narrowing has to be additive, or
    /// granting somebody the ability to run reviews for their own team would quietly hand them
    /// the appraisals of everybody in the firm.
    /// </remarks>
    public const string GoalsViewAll = "goals.view_all";

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

    /// <summary>
    /// See the suppliers and who to ring at each of them.
    /// </summary>
    /// <remarks>
    /// Its own permission rather than clients.view, because they are opposite relationships and
    /// held by different people: a delivery manager needs the client book and has no business
    /// in what the firm pays its landlord. Read widely enough that somebody chasing a late
    /// delivery can find the number without asking.
    /// </remarks>
    public const string VendorsView = "vendors.view";

    /// <summary>
    /// Put a supplier on the books, change their terms, keep the contact book.
    /// </summary>
    /// <remarks>
    /// Narrow. A supplier's payment terms decide when money leaves, and their tax number is what
    /// the firm files against — neither is something a delivery manager should be able to edit
    /// while chasing a delivery.
    /// </remarks>
    public const string VendorsManage = "vendors.manage";

    /// <summary>Ask the firm to buy something.</summary>
    /// <remarks>
    /// Held by everybody who works here, like logging hours and claiming expenses. An engineer
    /// who needs a second monitor and cannot ask for one through the system asks in a corridor,
    /// and the firm loses the record of what it spends and why.
    /// </remarks>
    public const string PurchasesRequest = "purchases.request";

    /// <summary>Read the requests and the orders.</summary>
    public const string PurchasesView = "purchases.view";

    /// <summary>
    /// Commit the firm to a supplier.
    /// </summary>
    /// <remarks>
    /// Narrow, and deliberately not held by whoever can approve a request. Approving says the
    /// firm agrees to spend; placing an order is the act that spends it, and a system where one
    /// person can do both unaided has no separation at the only point money leaves.
    /// </remarks>
    public const string PurchasesOrder = "purchases.order";

    /// <summary>
    /// Record what arrived.
    /// </summary>
    /// <remarks>
    /// Separate from ordering on purpose, and the separation is the point rather than an
    /// accident of naming: somebody who can both place an order and sign for its arrival can
    /// record goods that never came. Wider than ordering, because whoever is at the door when
    /// the boxes arrive is the person who should be typing it in.
    /// </remarks>
    public const string PurchasesReceive = "purchases.receive";

    /// <summary>
    /// Record that a supplier was paid.
    /// </summary>
    /// <remarks>
    /// The same split expenses already make between approving a claim and paying one, and for
    /// the same reason: approval says the figures are right and payment says the money has gone.
    /// </remarks>
    public const string PurchasesPay = "purchases.pay";
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
    /// <summary>
    /// Read the chart of accounts and the income-and-expenditure report.
    /// </summary>
    /// <remarks>
    /// Its own permission rather than invoices.view, because the two answer different
    /// questions about different people. An invoice is one client's bill; this report is what
    /// the firm earned and spent, and a delivery manager who legitimately reads the invoices
    /// they raise has no business reading the firm's own margin.
    /// </remarks>
    public const string AccountingView = "accounting.view";

    /// <summary>Open and retire accounts, and set up standing costs.</summary>
    public const string AccountingManage = "accounting.manage";
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

    /// <summary>
    /// Plan the work: sprints, the backlog, what sits under what, what waits on what.
    /// </summary>
    /// <remarks>
    /// Its own permission rather than tasks.create, because they are different acts by different
    /// people. Raising a card is something everybody doing the work does; deciding what is in
    /// this sprint, what the epics are and what has to happen before what is the thing somebody
    /// runs the delivery with — and a system where anybody can move cards into the running
    /// sprint has a sprint that means nothing by Wednesday.
    ///
    /// Reading the plan needs no permission of its own: the backlog and the sprint board are
    /// behind tasks.view_all and tasks.view_own like every other view of the same work. A
    /// separate read permission would be a second answer to "may this person see this card".
    /// </remarks>
    public const string PlanningManage = "planning.manage";
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

    // --- payroll -----------------------------------------------------------

    /// <summary>
    /// Open the payroll and read what a run pays everybody.
    /// </summary>
    /// <remarks>
    /// Its own permission rather than employees.pay, although the two will usually be held by
    /// the same people. They answer different questions: employees.pay is "what has this person
    /// been agreed", which is a fact about one staff record, and this is "what did the firm pay
    /// everybody in March", which is every salary in the firm on one screen. Somebody standing
    /// in for HR for a fortnight may reasonably be given the first and not the second.
    ///
    /// Nobody needs it to read their own payslip. That is theirs, the way their own profile is.
    /// </remarks>
    public const string PayrollView = "payroll.view";

    /// <summary>Draft a period's pay and agree the figures.</summary>
    /// <remarks>
    /// Drafting and approving are one permission on purpose. A draft changes nothing and can be
    /// rebuilt as often as anybody likes, so a permission to draft without approving would be a
    /// door with nothing behind it — and the approval is the act that matters, because it is
    /// what the figures are frozen by.
    /// </remarks>
    public const string PayrollRun = "payroll.run";

    /// <summary>Record that a run has been paid.</summary>
    /// <remarks>
    /// Separate from running it, for the reason paying an expense claim is separate from
    /// approving one: approval says the figures are right and payment says money has left the
    /// account. The same person will often do both, and the point is that they need not.
    /// </remarks>
    public const string PayrollPay = "payroll.pay";

    /// <summary>
    /// Record the statutory rates that payroll works from.
    /// </summary>
    /// <remarks>
    /// Its own permission because it is a different kind of act. Running the payroll applies the
    /// rates; this decides what they are, and a mistyped band is wrong on every payslip in the
    /// firm at once and on the return that follows.
    /// </remarks>
    public const string PayrollRates = "payroll.rates";

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

    /// <summary>
    /// Name a version, say what is in it, and withdraw one that failed.
    /// </summary>
    /// <remarks>
    /// One permission for declaring and for rolling back, deliberately, and it is the kind of
    /// decision worth writing down rather than splitting to look thorough. Withdrawing a
    /// version is the same authority as declaring one exercised in the other direction: whoever
    /// may say what the firm is running may say it is no longer running it. Splitting them
    /// would produce a person who can put 1.4.0 out and cannot take it back, which is the one
    /// combination nobody wants at two in the morning.
    ///
    /// Separate from repos.manage because connecting a repository is an administrative act and
    /// this is an engineering one — a lead who should never hold a webhook secret is exactly
    /// the person who should be naming releases.
    /// </remarks>
    public const string ReleasesDeclare = "releases.declare";

    // --- what the firm runs -------------------------------------------------

    /// <summary>Read the service catalogue and the resource register.</summary>
    /// <remarks>
    /// Wide, like incidents.view and for the same reason: the first thing anybody does in an
    /// incident is ask what the broken thing is and who owns it, and a permission that made them
    /// ask a person instead adds twenty minutes to the top of every one.
    /// </remarks>
    public const string PlatformView = "platform.view";

    /// <summary>
    /// Add to the catalogue and the register, and retire from them.
    /// </summary>
    /// <remarks>
    /// Narrower, because a register everybody can edit is a register with three entries called
    /// "the despatch board" — and because what is written here is what an incident will be filed
    /// against for as long as the firm exists.
    /// </remarks>
    public const string PlatformManage = "platform.manage";

    /// <summary>
    /// Turn a feature flag on or off.
    /// </summary>
    /// <remarks>
    /// Its own permission rather than platform.manage, and held wider, because turning a flag
    /// off is the fastest mitigation there is and the person doing it at two in the morning is
    /// whoever is awake. A model where the engineer watching the graphs has to find a head of
    /// department first is a model that adds twenty minutes to an outage.
    /// </remarks>
    public const string FlagsSet = "flags.set";

    // --- what the firm owns -------------------------------------------------

    /// <summary>See the asset register: what the firm has and who is holding it.</summary>
    /// <remarks>
    /// Not held by everybody, unlike incidents.view, and the reason is what is on the row rather
    /// than any sensitivity about laptops: the register carries what things cost and where each
    /// one lives. The question an ordinary member of staff has — what do I have — is answered on
    /// their own joining checklist by whoever set them up.
    /// </remarks>
    public const string AssetsView = "assets.view";

    /// <summary>Add things to the register, hand them over, take them back, retire them.</summary>
    /// <remarks>
    /// Separate from employees.manage although the joining and leaving screens both reach for
    /// it, because issuing a laptop is not the same authority as editing a staff record — and a
    /// firm that lets everybody who can edit people also write off equipment has no register
    /// worth reading.
    /// </remarks>
    public const string AssetsManage = "assets.manage";

    // --- when something is wrong -------------------------------------------

    /// <summary>Read the incidents and the reviews of them.</summary>
    /// <remarks>
    /// Wide on purpose — every working role holds it. An incident record is the one thing here
    /// that gets worse the fewer people can see it: somebody who cannot look at the open
    /// incident asks in a channel instead, and the answer they get is out of date. There is
    /// nothing in a timeline that a colleague should be kept from, and the one field that could
    /// have been sensitive — who caused it — deliberately does not exist.
    /// </remarks>
    public const string IncidentsView = "incidents.view";

    /// <summary>Say that something is wrong, and write in the timeline.</summary>
    /// <remarks>
    /// Separate from running one and held by everybody who works here, because the person who
    /// notices is very often not the person who will fix it. A model where whoever sees the
    /// first symptom has to find somebody else to raise it adds twenty minutes to the top of
    /// every incident.
    /// </remarks>
    public const string IncidentsRaise = "incidents.raise";

    /// <summary>
    /// Run one: severity, mitigation, resolution, and the review afterwards.
    /// </summary>
    /// <remarks>
    /// One permission rather than three, and the reasoning is the same as for releases. Saying
    /// how bad it is, saying it has stopped and saying what the firm learned are the same
    /// authority at three points in time; splitting them produces somebody who can declare an
    /// incident critical and cannot declare it over.
    /// </remarks>
    public const string IncidentsRun = "incidents.run";

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
        TeamsView, TeamsManage,
        AnnouncementsPost,
        GoalsView, GoalsManage, GoalsViewAll,

        PayrollView, PayrollRun, PayrollPay, PayrollRates,

        RequisitionsCreate, RequisitionsView, PostingsManage, CandidatesView,
        ApplicationsManage, ApplicationsHire, InterviewsSchedule, InterviewsView,
        ScorecardsSubmit,

        ClientsView, ClientsManage,

        VendorsView, VendorsManage,

        PurchasesRequest, PurchasesView, PurchasesOrder, PurchasesReceive,

        PurchasesPay,

        TimeLogOwn, TimeViewAll, TimeApprove,
        LeaveAsk, LeaveViewAll, LeaveApprove,

        ExpensesClaim, ExpensesViewAll, ExpensesApprove, ExpensesPay,
        AccountingView, AccountingManage,
        InvoicesView, InvoicesManage, InvoicesSend,

        ContractsView, ContractsManage,

        ProjectsViewAll, ProjectsViewMember, ProjectsManage,
        TasksViewAll, TasksViewOwn, TasksCreate, TasksAssign, TasksUpdateOwn, TasksSubmit,
        PlanningManage,
        TasksReview, TasksDeploy,

        ReposView, ReposManage, ReposDeliveries, ReleasesDeclare,

        IncidentsView, IncidentsRaise, IncidentsRun,

        AssetsView, AssetsManage,

        PlatformView, PlatformManage, FlagsSet,

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

                /*
                 * And runs it, literally. HR drafts a period, agrees the figures and keeps the
                 * statutory rates current — the last of those because the person who reads the
                 * Finance Act is the person who should type the bands in.
                 *
                 * Not payroll.pay. Money leaving the account is the office's job, which is the
                 * same separation expenses already make between approving a claim and paying
                 * one, and for the same reason: approval says the figures are right and payment
                 * says the money has gone.
                 */
                Permissions.PayrollView, Permissions.PayrollRun, Permissions.PayrollRates,

                Permissions.DepartmentsView,

                /*
                 * HR keeps the teams as well as the departments, and a team's membership is
                 * what tells HR who to ask about somebody at review time.
                 */
                Permissions.TeamsView, Permissions.TeamsManage,

                // HR says the firm-wide things: the office is shut, the leave rules have
                // changed, somebody has joined.
                Permissions.AnnouncementsPost,

                // HR runs the review cycles and is the one role that reads across the firm,
                // because somebody has to be able to answer "have the reviews been done".
                Permissions.GoalsView, Permissions.GoalsManage, Permissions.GoalsViewAll,

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

                // And asks the firm to buy things, for the same reason. Somebody who cannot ask
                // through the system asks in a corridor, and the record of what the firm spends
                // and why is lost.
                Permissions.PurchasesRequest,

                // Leave is HR's book to keep, and hours are how absence is
                // reconciled against it.
                Permissions.LeaveViewAll, Permissions.LeaveApprove,
                Permissions.TimeViewAll,

                Permissions.AuditView, Permissions.ReportsView,
            ],

            [DepartmentHead] =
            [
                Permissions.DepartmentsView, Permissions.EmployeesView, Permissions.UsersView,

                // A head forms the teams inside what they answer for, and a team that
                // crosses into another department is the ordinary case rather than a
                // trespass — which is the whole reason teams are not sub-departments.
                Permissions.TeamsView, Permissions.TeamsManage,

                // And addresses their own department, which is what the department field on
                // an announcement is for. Nothing stops them addressing the whole firm; the
                // post carries their name, which is the check that actually works.
                Permissions.AnnouncementsPost,

                // Sets goals and writes reviews for the people who report to them, and not for
                // anybody else — which is the reporting line's decision rather than this
                // permission's. No view_all: heading a department is not a reason to read the
                // appraisals of people in another one.
                Permissions.GoalsView, Permissions.GoalsManage,
                Permissions.ProjectsViewAll,

                /*
                 * The income-and-expenditure report, read and not written. A head signs off
                 * the claims their team makes and releases the work the firm bills for, so
                 * what the firm earned and spent is a figure they are answerable for — and
                 * opening or retiring an account is not their act.
                 */
                Permissions.AccountingView,

                /*
                 * The supplier book, read and not written. A head chasing a late delivery needs
                 * the number; changing a supplier's payment terms decides when money leaves the
                 * firm, and that is not their act either.
                 */
                Permissions.VendorsView,

                /*
                 * A head reads the purchasing for their part of the firm and signs for what
                 * arrives at the door. Placing the order and paying for it are deliberately not
                 * theirs: somebody who could order and also sign for the arrival could record
                 * goods that never came.
                 */
                Permissions.PurchasesView, Permissions.PurchasesReceive,

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

                // A head plans what their part of the firm is doing: the sprint, the backlog,
                // and the epics the work hangs off.
                Permissions.PlanningManage,

                // A head releases what the team finishes, and the evidence that
                // it is finished is a merged pull request. Seeing the board
                // without seeing that is being asked to sign for work on
                // somebody's word.
                Permissions.ReposView,

                // And here, rather than with the project managers or the
                // engineers, because naming a version is the moment somebody
                // takes responsibility for it in front of clients. A delivery
                // manager bills the work and an engineer builds it; the head is
                // the one who will be asked why 1.4.0 went out.
                Permissions.ReleasesDeclare,

                // A head is woken up about their team's service, so they run
                // incidents as well as read them.
                Permissions.IncidentsView, Permissions.IncidentsRaise,
                Permissions.IncidentsRun,

                Permissions.PlatformView, Permissions.PlatformManage,
                Permissions.FlagsSet,

                // A head signs for their team's equipment, and is the person
                // asked where a laptop went.
                Permissions.AssetsView, Permissions.AssetsManage,

                Permissions.ApprovalsDecide,
                Permissions.RequisitionsCreate, Permissions.RequisitionsView,
                Permissions.CandidatesView, Permissions.InterviewsSchedule,
                Permissions.InterviewsView, Permissions.ScorecardsSubmit,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // And asks the firm to buy things, for the same reason. Somebody who cannot ask
                // through the system asks in a corridor, and the record of what the firm spends
                // and why is lost.
                Permissions.PurchasesRequest,

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

                // A delivery manager assembles the team that does the work, which is the
                // reason teams.manage is not held by HR alone.
                Permissions.TeamsView, Permissions.TeamsManage,

                // And reads the purchasing, because the equipment a project is waiting on is
                // the thing they are chasing.
                Permissions.PurchasesView,

                // Their own goals and their own review. A delivery manager runs the board
                // rather than the line, so somebody's appraisal is not theirs to write.
                Permissions.GoalsView,

                // A delivery manager staffs work from across the firm, so their roster
                // cannot stop at the department they happen to sit in.
                Permissions.EmployeesViewAll,

                Permissions.UsersView,
                Permissions.ProjectsViewAll, Permissions.ProjectsManage,
                Permissions.TasksViewAll, Permissions.TasksViewOwn, Permissions.TasksCreate,
                Permissions.TasksAssign, Permissions.TasksReview,

                // Running the board is what a delivery manager does. If anybody in this firm
                // holds this one, it is them.
                Permissions.PlanningManage,
                Permissions.ReposView,
                Permissions.ApprovalsDecide,

                Permissions.IncidentsView, Permissions.IncidentsRaise,
                Permissions.IncidentsRun,

                Permissions.PlatformView, Permissions.FlagsSet,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // And asks the firm to buy things, for the same reason. Somebody who cannot ask
                // through the system asks in a corridor, and the record of what the firm spends
                // and why is lost.
                Permissions.PurchasesRequest,

                // A delivery manager bills the work, so they hold the clients
                // and the draft invoices. Sending one is somebody else's.
                Permissions.ClientsView, Permissions.ClientsManage,
                Permissions.TimeViewAll, Permissions.TimeApprove,
                Permissions.InvoicesView, Permissions.InvoicesManage,

                /*
                 * Deliberately NOT accounting.view. A delivery manager reads the invoices they
                 * raise, which is one client's bill at a time; the income-and-expenditure
                 * report is what the firm earned and spent, and that is a different question
                 * about different people.
                 */

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

                // Read, not write. Finding out which team owns a service is something
                // everybody who works here needs to do; deciding who is on one is not.
                Permissions.TeamsView,

                // Their own goals, and their own review once it has been shared. Withholding
                // this would make the performance system a file kept about somebody.
                Permissions.GoalsView,

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

                // An engineer sees the first symptom of nearly every incident,
                // and running one is ordinary engineering work rather than a
                // privilege. Withholding it would mean the person already
                // typing in the console has to find somebody to press a button.
                Permissions.IncidentsView, Permissions.IncidentsRaise,
                Permissions.IncidentsRun,

                Permissions.PlatformView, Permissions.FlagsSet,

                // Everybody who works here logs hours, asks for leave and
                // claims money back. These are not privileges; a role without
                // them describes somebody who does not work here.
                Permissions.TimeLogOwn, Permissions.LeaveAsk, Permissions.ExpensesClaim,

                // And asks the firm to buy things, for the same reason. Somebody who cannot ask
                // through the system asks in a corridor, and the record of what the firm spends
                // and why is lost.
                Permissions.PurchasesRequest,
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
