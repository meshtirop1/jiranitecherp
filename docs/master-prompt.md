# MASTER PROMPT: BUILD A COMPLETE DEVELOPER-NATIVE ERP

This is the brief, kept here verbatim so that "is it done?" is a question anybody can answer by reading rather than a matter of opinion. It was pasted in on 25 September 2026, having until then lived only in a chat window — which is why `docs/implementation-checklist.md` had drifted from it and could not be checked against it. Do not edit it to match what was built. The one agreed departure is recorded below, at section 3, and nowhere else.

---

You are the lead software architect, senior full-stack engineer, DevOps engineer, database architect, security engineer, QA engineer, and product engineer for this project.

Your task is to design and implement a production-grade ERP specifically designed for software development companies, technology companies, software agencies, startups, and IT departments.

This must NOT be a simple CRUD ERP, dashboard template, mockup, or UI-only application.

Build a real, modular, extensible system where business operations, software engineering, HR, recruitment, finance, projects, clients, infrastructure, DevOps, documentation, automation, and AI are connected.

The central philosophy is:

Developers should spend as little time as possible entering ERP information manually.

The system should automatically collect information from Git repositories, pull requests, CI/CD systems, deployments, tasks, calendars, infrastructure, invoices, expenses, and other integrations.

The ERP should function as a:

Software Company Operating System

## 1. CORE PRODUCT PRINCIPLES

The system must follow these principles:

1. Everything should be connected.
2. Avoid duplicate data entry.
3. Automate repetitive administrative work.
4. Developers should interact primarily with engineering tools while the ERP synchronizes information automatically.
5. Every important action should generate an event.
6. Every important change should be auditable.
7. Permissions must be granular.
8. The system must be multi-company capable.
9. The architecture must support future modules without major rewrites.
10. AI should be integrated throughout the system rather than being a separate isolated page.
11. All modules must expose APIs.
12. All modules must support automation.
13. The system must be responsive.
14. The system must work well on desktop, tablet, and mobile.
15. The UI must be fast and professional.
16. Errors must be understandable and actionable.
17. The system must be production-ready.
18. Do not create fake functionality where real functionality can be implemented.
19. Do not leave core features as TODO placeholders.
20. Do not implement only the frontend while pretending the backend exists.

## 2. FIRST STEP — ANALYZE THE EXISTING PROJECT

Before writing significant code:

1. Inspect the entire repository.
2. Identify the existing framework.
3. Identify the existing database.
4. Identify existing authentication.
5. Identify existing modules.
6. Identify existing APIs.
7. Identify existing frontend architecture.
8. Identify existing deployment configuration.
9. Identify existing tests.
10. Identify technical debt.
11. Identify reusable components.
12. Identify missing infrastructure.

Do not unnecessarily rewrite working code.

Create a project architecture document containing:

* current architecture
* proposed architecture
* database strategy
* module structure
* authentication strategy
* authorization strategy
* integration strategy
* event architecture
* background jobs
* AI architecture
* deployment architecture
* testing strategy

Then implement the system incrementally.

## 3. MULTI-TENANT ORGANIZATION SYSTEM

> **AGREED DEPARTURE — OUT OF SCOPE.** This system is built for Jiranisoko Tech Solutions and no other company. Organization switching, per-organisation isolation and organisation-scoped permissions are not built, and `FirmSettings` is a single row with a fixed key rather than a table of organisations. This is the only section of the brief that has been set aside, and it was set aside by agreement rather than by omission.

The ERP must support multiple companies/organizations.

Each organization should have:

* Organization ID
* Legal name
* Trading name
* Registration number
* Tax information
* Country
* Currency
* Time zone
* Address
* Phone
* Email
* Website
* Logo
* Fiscal year
* Business settings
* Security settings
* Notification settings

Users can belong to one or multiple organizations.

Support:

* Organization switching
* Multiple departments
* Multiple branches
* Multiple teams
* Multiple locations

All business data must be correctly isolated between organizations.

## 4. USER AND IDENTITY MANAGEMENT

Implement:

* User registration
* Login
* Logout
* Password reset
* Email verification
* MFA/2FA
* Session management
* Device management
* Login history
* Suspicious login detection
* Account locking
* Account deactivation
* User invitations
* Organization invitations
* Profile management

User profile:

* Name
* Profile photo
* Email
* Phone
* Job title
* Department
* Team
* Skills
* Certifications
* Employment status
* Start date
* Manager
* Location
* Time zone

## 5. ROLE AND PERMISSION SYSTEM

Implement proper RBAC.

Default roles may include:

* Owner
* Super Administrator
* Administrator
* HR
* Recruiter
* Finance Manager
* Accountant
* Project Manager
* Engineering Manager
* Tech Lead
* Developer
* QA Engineer
* DevOps Engineer
* Designer
* Sales
* Support
* Client
* Auditor

But do not hard-code permissions into individual pages.

Create a permission system.

Permissions should support:

* Organization
* Department
* Project
* Team
* Resource
* Record
* Action

Actions:

* View
* Create
* Update
* Delete
* Approve
* Export
* Assign
* Manage
* Execute

Support resource-level permissions where practical.

## 6. EMPLOYEE / HR MODULE

Build a complete HR system.

Employee records:

* Personal information
* Contact information
* Emergency contacts
* Employment information
* Job title
* Department
* Manager
* Team
* Employment type
* Contract
* Start date
* Probation
* Salary
* Benefits
* Work location
* Skills
* Certifications
* Documents

Employee lifecycle:

```
Applicant
→ Interview
→ Offer
→ Hired
→ Onboarding
→ Active
→ Promotion/Transfer
→ Leave
→ Offboarding
→ Former Employee
```

Implement:

* Employee directory
* Departments
* Teams
* Job titles
* Employment contracts
* Salary records
* Benefits
* Leave management
* Attendance
* Holidays
* Performance reviews
* Goals
* Employee documents
* Employee announcements
* Employee self-service
* Onboarding
* Offboarding

## 7. RECRUITMENT AND HIRING SYSTEM

Hiring MUST be implemented inside the ERP.

Create a complete ATS (Applicant Tracking System).

Recruitment workflow:

```
Workforce Need
→ Job Request
→ Approval
→ Job Opening
→ Job Description
→ Publish
→ Applications
→ Screening
→ Interview
→ Technical Assessment
→ Evaluation
→ Offer
→ Accepted
→ Onboarding
→ Employee
```

Job requisition:

* Position
* Department
* Hiring manager
* Number of vacancies
* Employment type
* Salary range
* Required skills
* Preferred skills
* Experience
* Location
* Remote/hybrid/on-site
* Budget
* Business justification
* Approval workflow

Job posting:

* Public job description
* Requirements
* Responsibilities
* Benefits
* Application form
* Screening questions

Candidate profile:

* Name
* Email
* Phone
* CV/resume
* Portfolio
* GitHub
* LinkedIn
* Skills
* Experience
* Education
* Certifications
* Salary expectation
* Availability
* Notes
* Interview history
* Assessment results

Candidate pipeline:

```
Applied
→ Screening
→ Shortlisted
→ Interview
→ Technical Test
→ Final Interview
→ Offer
→ Accepted
→ Rejected
```

Implement interview scheduling.

Interview types:

* HR interview
* Technical interview
* System design
* Coding interview
* Culture/behavioral interview
* Management interview

Interview scorecards:

* Technical skills
* Communication
* Problem solving
* Architecture
* Coding
* Collaboration
* Role-specific competencies

DO NOT create an automatic hiring decision system that secretly decides whether someone should be hired.

AI can assist recruiters by:

* summarizing CVs
* extracting skills
* generating interview questions
* summarizing interview notes
* identifying missing information
* comparing candidate qualifications against explicitly defined job requirements

Final hiring decisions remain with authorized humans.

## 8. OFFER AND ONBOARDING

Implement:

* Offer generation
* Offer approval
* Salary
* Benefits
* Start date
* Contract documents
* Electronic acceptance
* Onboarding checklist
* Equipment assignment
* Account creation
* Repository access request
* Email provisioning
* VPN access
* Documentation access
* Team assignment
* Manager assignment

Example onboarding:

```
New Employee
      ↓
HR
      ↓
Create accounts
      ↓
Assign laptop
      ↓
Create GitHub access
      ↓
Create email
      ↓
Assign team
      ↓
Assign mentor
      ↓
Security training
      ↓
Project assignment
      ↓
Onboarding complete
```

## 9. OFFBOARDING

Implement:

* Resignation
* Termination
* Exit interview
* Asset return
* Account disabling
* Git access removal
* Cloud access removal
* Email disabling
* VPN removal
* Documentation transfer
* Knowledge transfer
* Final payroll
* Final documents

Create automated offboarding workflows.

## 10. PROJECT MANAGEMENT

Projects must be connected to:

* Client
* Contract
* Budget
* Team
* Tasks
* Git repositories
* Deployments
* Time
* Expenses
* Invoices
* Documents
* Support
* Releases

Project states:

```
Proposal
→ Planning
→ Active
→ On Hold
→ Completed
→ Archived
```

Project fields:

* Name
* Client
* Manager
* Team
* Start date
* Deadline
* Budget
* Estimated hours
* Actual hours
* Revenue
* Costs
* Profitability
* Status

## 11. SOFTWARE DEVELOPMENT MANAGEMENT

This is one of the most important modules.

Implement:

* Epics
* Features
* User stories
* Tasks
* Bugs
* Subtasks
* Sprints
* Backlogs
* Kanban
* Scrum
* Milestones
* Releases

Task states:

```
Backlog
→ Ready
→ In Progress
→ Code Review
→ QA
→ Staging
→ Done
```

Tasks should support:

* Priority
* Assignee
* Team
* Labels
* Estimate
* Actual time
* Dependencies
* Attachments
* Comments
* Checklists
* Acceptance criteria
* Due date
* Sprint
* Epic
* Feature

## 12. GIT INTEGRATION

Integrate:

* GitHub
* GitLab
* Bitbucket
* Azure DevOps

Support:

* repositories
* branches
* commits
* pull requests
* merge requests
* reviews
* comments
* releases
* tags

Map:

```
Task
↔ Branch
↔ Commit
↔ Pull Request
↔ Review
↔ Build
↔ Deployment
```

Example:

```
Task #123
    ↓
feature/payment-api
    ↓
7 commits
    ↓
PR #142
    ↓
2 reviewers
    ↓
CI passed
    ↓
Merged
    ↓
Staging deployment
```

Use webhooks wherever possible.

Do not require developers to manually report Git activity.

## 13. CI/CD MODULE

Integrate with:

* GitHub Actions
* GitLab CI
* Jenkins
* Azure DevOps
* other CI systems through webhooks/API

Track:

* builds
* pipelines
* tests
* failures
* artifacts
* deployments
* deployment duration
* environment
* release

Environments:

```
Development
Staging
Production
```

## 14. DEVOPS / INFRASTRUCTURE MODULE

Track:

* Servers
* VPS
* Cloud accounts
* Containers
* Kubernetes clusters
* Databases
* DNS
* Domains
* SSL certificates
* Load balancers
* Storage
* Backups
* Monitoring
* CI/CD infrastructure

Server record:

* Provider
* IP
* OS
* CPU
* RAM
* Storage
* Region
* Environment
* Owner
* Project
* Services
* Status

Certificate monitoring:

```
SSL expires in 30 days
→ Create task
→ Notify DevOps
```

## 15. ASSET MANAGEMENT

Track:

* Laptops
* Desktops
* Phones
* Tablets
* Monitors
* Servers
* Networking equipment
* Software licenses
* Domains
* Cloud subscriptions

Asset lifecycle:

```
Purchased
→ In Stock
→ Assigned
→ Maintenance
→ Returned
→ Retired
```

Every asset must have an owner/history.

## 16. CLIENT / CRM MODULE

Implement:

* Leads
* Companies
* Contacts
* Opportunities
* Sales pipeline
* Activities
* Calls
* Meetings
* Notes
* Contracts
* Projects
* Support tickets
* Invoices

Client relationship:

```
Client
 ↓
Contacts
 ↓
Contracts
 ↓
Projects
 ↓
Tasks
 ↓
Invoices
 ↓
Support
```

## 17. CONTRACT MANAGEMENT

Implement:

* Client contracts
* Employee contracts
* Vendor contracts
* NDAs
* Maintenance agreements
* Service agreements

Track:

* Start date
* End date
* Renewal date
* Value
* Terms
* Documents
* Responsible person

Automated renewal reminders.

## 18. FINANCE MODULE

Implement:

* Chart of accounts
* Income
* Expenses
* Invoices
* Payments
* Vendors
* Purchase orders
* Budgets
* Project costs
* Recurring expenses
* Financial reports

Invoices:

```
Draft
→ Sent
→ Viewed
→ Partially Paid
→ Paid
→ Overdue
→ Cancelled
```

Support multiple currencies.

## 19. PROJECT PROFITABILITY

Every project should calculate:

```
Revenue
-
Employee cost
-
Contractor cost
-
Cloud cost
-
Software cost
-
Other expenses
=
Gross profit
```

Show:

* Budget
* Actual
* Forecast
* Variance
* Profitability

## 20. EXPENSE MANAGEMENT

Employees should be able to submit expenses.

Expense:

* Employee
* Project
* Category
* Amount
* Currency
* Date
* Receipt
* Description

Workflow:

```
Submitted
→ Manager Approval
→ Finance Approval
→ Reimbursed
```

## 21. TIME TRACKING

Implement:

* Manual timers
* Manual entries
* Project time
* Task time
* Billable/non-billable
* Timesheets

But also support automatic activity detection from:

* Git
* PRs
* task activity
* calendar
* deployments

Never pretend automatically inferred time is exact.

Mark it as:

```
Estimated
Confirmed
Manually adjusted
```

## 22. PAYROLL

Build payroll architecture that supports country-specific implementations.

Payroll should support:

* Employee salary
* Allowances
* Bonuses
* Deductions
* Taxes
* Benefits
* Overtime
* Payslips
* Payroll periods
* Approval
* Payment status

Do not hard-code one country's tax rules into the core system.

Create a country-specific payroll rules layer.

## 23. LEAVE AND ATTENDANCE

Implement:

* Annual leave
* Sick leave
* Personal leave
* Public holidays
* Leave requests
* Approval
* Leave balances
* Attendance
* Remote work
* Work schedules

## 24. DOCUMENT MANAGEMENT

Implement:

* Company documents
* Employee documents
* Contracts
* Project documentation
* Technical documentation
* Policies
* Procedures

Features:

* Versioning
* Permissions
* Search
* Tags
* Document relationships
* Audit history

## 25. KNOWLEDGE BASE

Create internal technical documentation.

Structure:

```
Engineering
├── Architecture
├── APIs
├── Databases
├── Deployment
├── Security
├── Troubleshooting
└── Runbooks
```

Allow documentation to connect to:

* projects
* repositories
* tasks
* servers
* incidents

## 26. SUPPORT / HELP DESK

Implement:

* Support tickets
* Client tickets
* Internal tickets
* Priority
* SLA
* Assignment
* Status
* Comments
* Attachments
* Escalation

Ticket workflow:

```
New
→ Assigned
→ In Progress
→ Waiting
→ Resolved
→ Closed
```

## 27. INCIDENT MANAGEMENT

For software companies:

Implement:

* Incidents
* Severity
* Detection
* Assignment
* Timeline
* Status
* Root cause
* Resolution
* Postmortem
* Follow-up tasks

Severity:

```
SEV1
SEV2
SEV3
SEV4
```

Connect incidents to:

* deployments
* services
* projects
* servers
* developers

## 28. SECURITY CENTER

Implement:

* Audit logs
* Login history
* MFA
* Access reviews
* API key management
* Secret references
* Security events
* Permission changes
* Device sessions

Never store raw secrets unnecessarily.

Secrets should be stored through a secure secrets mechanism.

## 29. AUDIT LOG

Every important mutation should generate an audit event.

Example:

```
WHO:
Meshack

ACTION:
Changed project budget

FROM:
$10,000

TO:
$12,000

TIME:
2026-09-21 14:32

RESOURCE:
Project #123
```

Audit logs should be immutable to normal users.

## 30. EVENT-DRIVEN ARCHITECTURE

Build an event system.

Examples:

```
UserCreated
EmployeeHired
EmployeeOffboarded
CandidateApplied
InterviewScheduled
TaskCreated
TaskCompleted
CommitCreated
PullRequestOpened
PullRequestMerged
BuildFailed
DeploymentCompleted
InvoiceCreated
InvoicePaid
ExpenseSubmitted
LeaveApproved
CertificateExpiring
IncidentCreated
```

Events can trigger automation.

## 31. AUTOMATION ENGINE

Build a generic automation engine.

Concept:

```
WHEN
→ IF
→ THEN
```

Example:

```
WHEN:
Pull Request merged

IF:
CI passed

THEN:
Deploy staging
```

Another:

```
WHEN:
Invoice overdue

IF:
7 days overdue

THEN:
Notify finance
AND notify account manager
AND create follow-up task
```

Another:

```
WHEN:
Employee hired

THEN:
Create onboarding checklist
AND create IT provisioning tasks
AND notify manager
```

Automation must support:

* conditions
* actions
* delays
* schedules
* webhooks
* notifications
* task creation
* emails
* API calls
* approvals

## 32. NOTIFICATION CENTER

Central notification system.

Channels:

* In-app
* Email
* Push
* Slack
* Microsoft Teams
* Webhooks

Notifications should be grouped intelligently.

Avoid notification spam.

## 33. CALENDAR

Implement:

* Meetings
* Interviews
* Deadlines
* Leave
* Holidays
* Project milestones
* Deployments
* Maintenance

Support calendar integrations later.

## 34. SEARCH

Build global search.

Users should be able to search:

```
Projects
Employees
Clients
Tasks
Commits
PRs
Invoices
Documents
Servers
Candidates
Tickets
Contracts
```

Search should support filters.

Example:

```
project:jiranisoko status:active
```

## 35. DASHBOARDS

Create dashboards based on role.

Developer:

```
My Tasks
My PRs
My Reviews
My Deployments
My Time
My Notifications
```

Manager:

```
Projects
Team workload
Deadlines
Risks
Budget
Performance
```

HR:

```
Employees
Recruitment
Leave
Onboarding
Offboarding
```

Finance:

```
Revenue
Expenses
Invoices
Payments
Project profitability
```

CEO/Owner:

```
Revenue
Costs
Profit
Projects
Employees
Hiring
Sales
Risks
```

## 36. AI ASSISTANT

Create an ERP AI assistant.

Users should be able to ask: "What projects are delayed?" "Why is Project X delayed?" "Show me unpaid invoices." "Who is assigned to the payment project?" "Summarize this client's activity." "Why did the deployment fail?" "Create a task to fix the issue." "Prepare a project status report." "Draft an invoice."

AI must respect permissions.

A user must NEVER receive information they are not authorized to access.

AI should be able to:

* search ERP data
* summarize
* generate reports
* explain
* create drafts
* create tasks
* trigger approved automations
* assist with documentation
* analyze project information

For destructive actions, require confirmation.

## 37. AI PROJECT ANALYSIS

For each project, AI can generate:

* Status summary
* Risks
* Blockers
* Upcoming deadlines
* Workload
* Open issues
* PR status
* Deployment status
* Financial status

Example:

```
PROJECT HEALTH

Schedule:
At Risk

Budget:
Within Budget

Engineering:
2 blockers

Deployment:
1 failed pipeline

Main Risk:
Payment API integration
```

Do not present AI-generated assessments as objective facts when they are inferences.

Clearly distinguish:

* actual data
* calculated metrics
* AI inference

## 38. REPORTING

Implement reports for:

Engineering

* Cycle time
* Lead time
* Deployment frequency
* Failed deployments
* Open bugs
* PR review time
* Release frequency

Projects

* Budget
* Schedule
* Progress
* Resource utilization

Finance

* Revenue
* Expenses
* Profit
* Receivables
* Payables

HR

* Headcount
* Hiring pipeline
* Turnover
* Leave
* Employee distribution

Do not use metrics to create simplistic employee "productivity scores."

Metrics should provide operational information, not unfair automated judgments.

## 39. API-FIRST DESIGN

Every major module must expose APIs.

Use consistent:

* REST APIs or appropriate API architecture
* Authentication
* Authorization
* Pagination
* Filtering
* Sorting
* Validation
* Error handling
* Versioning

Document APIs automatically.

## 40. WEBHOOK SYSTEM

Support incoming and outgoing webhooks.

Incoming:

```
GitHub
GitLab
CI/CD
Payment providers
Monitoring
```

Outgoing:

```
Slack
Teams
Custom systems
Client systems
Automation platforms
```

Implement webhook:

* signing
* verification
* retry
* failure handling
* logging
* idempotency

## 41. DATABASE DESIGN

Use a relational database suitable for production.

Prefer PostgreSQL unless the existing project has a strong reason to use another database.

Design proper relationships.

Avoid:

* unnecessary duplication
* giant tables
* unstructured JSON for core relational data
* hard-coded organization IDs
* hard-coded users
* hard-coded roles

Use migrations.

Create indexes for frequently queried fields.

## 42. BACKGROUND JOBS

Use background processing for:

* emails
* notifications
* webhook processing
* integrations
* reports
* scheduled automation
* AI processing
* certificate checks
* invoice reminders
* synchronization

Do not perform long-running jobs inside HTTP requests.

## 43. CACHING

Use caching where useful.

Possible technologies:

* Redis
* application-level caching
* query caching

Do not cache sensitive information incorrectly.

## 44. ERROR HANDLING

Implement consistent error responses.

Users should see useful errors.

Developers should receive detailed logs.

Never expose:

* stack traces
* secrets
* database credentials
* internal tokens

in production responses.

## 45. OBSERVABILITY

Implement:

* structured logging
* application metrics
* request tracing where appropriate
* background-job monitoring
* error tracking
* health checks
* database health
* integration health

Create:

```
/health
/ready
```

or equivalent endpoints.

## 46. TESTING

Do not consider the system complete without tests.

Implement:

Unit tests

For:

* business logic
* calculations
* permissions
* workflows

Integration tests

For:

* database
* APIs
* webhooks
* authentication
* integrations

End-to-end tests

For important workflows:

```
Create organization
→ Invite employee
→ Create project
→ Create task
→ Connect repository
→ Receive webhook
→ Create PR
→ Merge
→ Deployment
```

Recruitment:

```
Create job
→ Candidate applies
→ Screening
→ Interview
→ Offer
→ Accept
→ Employee created
→ Onboarding
```

Finance:

```
Create invoice
→ Send
→ Payment
→ Paid
```

## 47. UI/UX REQUIREMENTS

The UI must feel like a modern developer tool, not an old accounting ERP.

Use:

* clean navigation
* command palette
* keyboard shortcuts
* responsive tables
* filtering
* search
* bulk actions
* drag-and-drop where appropriate
* dark mode
* light mode
* contextual actions
* breadcrumbs
* side panels
* modal dialogs only when useful

Global command palette:

```
Ctrl/Cmd + K
```

Examples:

```
Create project
Create task
Find employee
Search invoice
Deploy project
Open repository
```

## 48. DEVELOPER EXPERIENCE

Make developer workflows extremely fast.

Keyboard shortcuts:

```
C = Create
T = Tasks
P = Projects
G = Git
D = Dashboard
```

Allow developers to:

* create tasks quickly
* update task status
* open PR
* see CI
* see deployments
* access documentation
* see incidents
* communicate with team

without navigating through many pages.

## 49. MOBILE EXPERIENCE

The system should be responsive.

Mobile users should be able to:

* approve leave
* approve expenses
* approve hiring
* view projects
* update tasks
* receive notifications
* view invoices
* approve documents
* view incidents

## 50. CLI

Create an optional CLI for developers.

Example:

```
erp login

erp project list

erp task create

erp task start 123

erp task done 123

erp deploy staging

erp project status

erp incident create
```

The CLI must use the same APIs as the web application.

Do not duplicate business logic in the CLI.

## 51. INTEGRATIONS

Design integration architecture so new providers can be added without changing core business logic.

Initial integration targets:

Development

* GitHub
* GitLab
* Bitbucket
* Azure DevOps

Communication

* Slack
* Microsoft Teams
* Email

Cloud

* AWS
* Azure
* Google Cloud
* Hetzner
* DigitalOcean

Payments

Use provider adapters rather than coupling the ERP to one payment provider.

Calendar

* Google Calendar
* Microsoft Calendar

## 52. IMPORT / EXPORT

Support:

* CSV
* Excel
* JSON
* PDF reports

Allow bulk import for:

* employees
* clients
* projects
* tasks
* products/services
* financial data

Validate imports before committing.

Show:

```
Valid records: 948
Invalid records: 12

Download error report
```

## 53. DATA RETENTION

Implement configurable retention policies.

Administrators should be able to configure retention for:

* audit logs
* application logs
* documents
* candidate records
* deleted accounts

Respect applicable privacy laws.

## 54. SECURITY

Security must be treated as a first-class requirement.

Implement:

* secure password hashing
* MFA
* CSRF protection
* XSS protection
* SQL injection protection
* rate limiting
* secure cookies
* session expiration
* authorization checks
* input validation
* encryption where appropriate
* secure headers
* secret management
* audit logs

Never trust frontend permissions.

All authorization must be enforced server-side.

## 55. DATA PRIVACY

Implement:

* data export
* account deletion workflow
* privacy settings
* consent records where applicable
* access logging
* data minimization

Sensitive employee and candidate data must be protected.

## 56. MULTI-LANGUAGE

Design the application for internationalization.

Support:

* English initially
* additional languages later

Do not hard-code UI strings directly into components.

## 57. MULTI-CURRENCY

Support:

* organization currency
* project currency
* invoice currency
* exchange rates
* currency conversion

Keep historical transaction currency intact.

Do not silently change historical financial records when exchange rates change.

## 58. MULTI-COUNTRY

The architecture must support different:

* taxes
* payroll
* currencies
* holidays
* employment rules
* invoices
* addresses

Do not put country-specific business logic into the core domain.

Use country-specific modules/providers.

## 59. NOTIFICATION RULES

Allow users to configure notifications.

Example:

```
PR assigned → immediate
Invoice overdue → daily
Project deadline → 7 days before
SSL expiry → 30 days before
Interview → 1 day before
```

## 60. APPROVAL ENGINE

Build a reusable approval engine.

Use it for:

* hiring
* expenses
* invoices
* leave
* purchase orders
* salary changes
* project budgets
* contracts

Example:

```
Expense
→ Manager
→ Finance
→ Approved
```

Different organizations should be able to configure approval chains.

## 61. PURCHASE / PROCUREMENT

Implement:

* Vendors
* Purchase requests
* Purchase orders
* Approval
* Receiving
* Vendor invoices
* Payments

Connect purchases to projects and departments.

## 62. VENDOR MANAGEMENT

Track:

* Vendor
* Contact
* Contracts
* Services
* Expenses
* Renewal dates
* Performance information
* Documents

## 63. COMPANY-WIDE HOME DASHBOARD

Create a useful company overview.

Show:

```
Revenue
Expenses
Outstanding invoices
Active projects
Projects at risk
Open incidents
Employees
Open positions
Candidates
Upcoming deadlines
Infrastructure alerts
```

But make all widgets permission-aware.

## 64. PROJECT AUTOMATION

Example:

```
New project created
→ Create project folder
→ Create default documentation
→ Create default task structure
→ Create Git repository if authorized
→ Add team
→ Create environments
→ Create project checklist
```

## 65. NEW EMPLOYEE AUTOMATION

Example:

```
Employee hired
→ Create accounts
→ Create onboarding tasks
→ Assign equipment
→ Assign team
→ Assign manager
→ Notify HR
→ Notify IT
→ Notify manager
```

## 66. NEW CLIENT AUTOMATION

Example:

```
Client created
→ Create CRM record
→ Create client workspace
→ Create document folder
→ Create billing profile
→ Create support profile
```

## 67. RELEASE MANAGEMENT

Implement:

* Releases
* Version numbers
* Release notes
* Changelog
* Tasks included
* PRs included
* Deployment status
* Rollback information

Example:

```
v2.4.0

12 tasks
8 PRs
2 bug fixes

Staging ✓
Production ✓
```

## 68. FEATURE FLAGS

Implement feature flags for:

* gradual releases
* beta features
* internal testing
* customer-specific features

Support:

* organization
* environment
* user
* percentage rollout where appropriate

## 69. INCIDENT → POSTMORTEM

When an incident is resolved:

Automatically create:

```
Incident
↓
Timeline
↓
Root Cause
↓
Impact
↓
Resolution
↓
Corrective Actions
↓
Tasks
```

## 70. KNOWLEDGE GRAPH / RELATIONSHIP MODEL

The ERP should understand relationships.

For example:

```
Client
 ↓
Contract
 ↓
Project
 ↓
Repository
 ↓
Task
 ↓
PR
 ↓
Deployment
 ↓
Server
```

This makes AI and reporting much more powerful.

## 71. GLOBAL ACTIVITY TIMELINE

Every important object should have an activity timeline.

Example:

```
Today

14:22 Meshack merged PR #182
14:10 CI passed
13:58 Sarah approved PR
13:32 Task moved to QA
12:20 Client approved requirement
```

## 72. OBJECT RELATIONSHIPS

Any object should be able to link to another relevant object.

Examples:

```
Task → Project
Task → PR
Task → Commit
Task → Client
Task → Incident

Employee → Project
Employee → Task
Employee → Asset

Invoice → Client
Invoice → Project
Invoice → Contract
```

## 73. API TOKENS

Implement developer API tokens.

Allow:

```
Create token
Set permissions
Set expiration
Revoke token
View usage
```

Never show token secrets again after creation.

## 74. SERVICE ACCOUNTS

Support machine identities for:

* CI/CD
* automation
* integrations
* background workers

Service accounts must have scoped permissions.

## 75. WEBHOOK SECURITY

All external webhooks must support:

* signature verification
* replay protection where possible
* idempotency
* retry
* dead-letter handling
* logs

## 76. IDE INTEGRATION — FUTURE READY

Design APIs so future plugins can support:

* VS Code
* JetBrains IDEs
* Visual Studio

Developers should eventually be able to:

```
View task
Update task
Create branch
Open PR
View CI
View deployment
```

without leaving their IDE.

## 77. PERFORMANCE

The application must remain responsive with:

* thousands of employees
* millions of tasks
* millions of events
* large Git histories

Use:

* pagination
* indexes
* caching
* asynchronous processing
* optimized queries
* lazy loading where appropriate

Never load massive datasets unnecessarily.

## 78. ARCHITECTURAL RULE

Keep business logic out of UI components.

Use a clean separation:

```
UI
 ↓
API
 ↓
Application Services
 ↓
Domain
 ↓
Infrastructure
 ↓
Database / External Services
```

Use modular architecture.

Avoid creating one giant application module.

## 79. MODULE STRUCTURE

Organize code into logical domains.

For example:

```
Identity
Organizations
HR
Recruitment
Projects
Tasks
Engineering
Git
CI/CD
DevOps
Assets
CRM
Contracts
Finance
Payroll
Expenses
Procurement
Support
Incidents
Documents
Knowledge
Notifications
Automation
AI
Reporting
Audit
Integrations
```

Exact folder structure should match the chosen framework, but maintain strong domain boundaries.

## 80. DEVELOPMENT PROCESS

Do NOT attempt to blindly implement every feature in one huge uncontrolled change.

Work in phases.

PHASE 1 — Foundation

Implement:

* architecture
* database
* authentication
* organizations
* users
* roles
* permissions
* audit
* settings
* navigation
* UI design system
* API foundation
* logging
* testing foundation

PHASE 2 — Core Business

Implement:

* employees
* departments
* teams
* clients
* CRM
* projects
* tasks
* documents

PHASE 3 — Engineering

Implement:

* repositories
* GitHub
* GitLab
* commits
* branches
* PRs
* CI/CD
* releases
* deployments

PHASE 4 — HR / Recruitment

Implement:

* job requisitions
* job postings
* candidates
* interviews
* assessments
* offers
* hiring
* onboarding
* offboarding

PHASE 5 — Finance

Implement:

* invoices
* expenses
* budgets
* project profitability
* vendors
* procurement
* payments

PHASE 6 — Infrastructure

Implement:

* servers
* cloud
* domains
* SSL
* assets
* monitoring
* incidents

PHASE 7 — Automation

Implement:

* event bus
* automation engine
* workflows
* approvals
* scheduled jobs

PHASE 8 — AI

Implement:

* AI assistant
* semantic search
* project summaries
* document analysis
* engineering summaries
* financial summaries
* automation assistance

PHASE 9 — Advanced

Implement:

* mobile optimization
* CLI
* advanced integrations
* feature flags
* advanced analytics
* external API
* developer integrations

## 81. DEFINITION OF DONE

A feature is NOT complete merely because the page exists.

Every feature must include, where applicable:

* Database model
* Migration
* Backend logic
* API
* Validation
* Authorization
* UI
* Loading states
* Empty states
* Error states
* Notifications
* Audit logging
* Tests
* Documentation
* Accessibility
* Mobile responsiveness

For integrations:

* Authentication
* Webhooks
* Sync
* Retry
* Error handling
* Logging
* Idempotency

## 82. NO FAKE FEATURES

Do not create buttons that only show:

"Coming soon"

unless the feature is explicitly scheduled for a future phase.

Do not use fake API responses.

Do not hard-code fake statistics.

Do not create fake GitHub data.

Do not create fake financial transactions.

If a feature cannot yet be implemented, clearly mark it as not implemented rather than pretending it works.

## 83. SEED DATA

Create development seed data.

Include:

* sample organization
* departments
* employees
* projects
* clients
* tasks
* repositories
* invoices
* candidates
* job openings
* assets
* servers
* incidents

Seed data must be clearly identifiable as development/demo data.

## 84. ADMIN TOOLS

Provide administrators with:

* system health
* background jobs
* failed jobs
* integrations
* webhooks
* audit logs
* user sessions
* permissions
* feature flags
* automation rules
* system configuration

## 85. DOCUMENTATION

Create project documentation:

```
/docs
  architecture.md
  setup.md
  development.md
  deployment.md
  database.md
  api.md
  authentication.md
  authorization.md
  integrations.md
  automation.md
  ai.md
  testing.md
  security.md
```

Keep documentation synchronized with implementation.

## 86. ENVIRONMENT CONFIGURATION

Support:

```
Development
Testing
Staging
Production
```

Never commit secrets.

Provide:

```
.env.example
```

Document all environment variables.

## 87. DEPLOYMENT

Prepare production deployment.

Depending on the existing stack, support:

* Docker
* reverse proxy
* HTTPS
* database migrations
* static files
* background workers
* scheduled jobs
* backups
* health checks

Provide deployment documentation.

## 88. BACKUPS

Implement/document:

* database backups
* backup verification
* retention
* restore procedures

A backup that has never been tested for restoration should not be considered reliable.

## 89. DISASTER RECOVERY

Document:

* recovery procedure
* database restoration
* application restoration
* secrets restoration
* infrastructure recovery
* RTO
* RPO

## 90. FINAL PRODUCT EXPERIENCE

The final application should feel like a combination of:

```
ERP
+
GitHub
+
Jira
+
Linear
+
HR system
+
CRM
+
Accounting
+
DevOps dashboard
+
Notion
+
Automation platform
+
AI assistant
```

But do NOT simply copy those products.

Create one coherent system where all information is connected.

## 91. MOST IMPORTANT WORKFLOW

The following workflow should work end-to-end:

```
Company
 ↓
Client
 ↓
Contract
 ↓
Project
 ↓
Team
 ↓
Developer
 ↓
Epic
 ↓
Feature
 ↓
Task
 ↓
Git Branch
 ↓
Commit
 ↓
Pull Request
 ↓
Code Review
 ↓
CI
 ↓
Staging
 ↓
Production
 ↓
Release
 ↓
Client
 ↓
Invoice
 ↓
Payment
 ↓
Project Profitability
```

The ERP should understand the entire lifecycle.

## 92. SECOND CRITICAL WORKFLOW — HIRING

This workflow must also work end-to-end:

```
Department needs developer
 ↓
Job requisition
 ↓
Manager approval
 ↓
HR approval
 ↓
Job opening
 ↓
Job published
 ↓
Candidate applies
 ↓
Candidate screening
 ↓
Interview
 ↓
Technical assessment
 ↓
Interview evaluation
 ↓
Offer
 ↓
Candidate accepts
 ↓
Employee created
 ↓
Onboarding
 ↓
Laptop assigned
 ↓
Accounts created
 ↓
Git access
 ↓
Team assigned
 ↓
Project assigned
```

## 93. THIRD CRITICAL WORKFLOW — INCIDENT

```
Production problem
 ↓
Monitoring/webhook
 ↓
Incident created
 ↓
Severity assigned
 ↓
Engineer assigned
 ↓
Incident timeline
 ↓
Resolution
 ↓
Deployment
 ↓
Verification
 ↓
Postmortem
 ↓
Corrective tasks
```

## 94. FOURTH CRITICAL WORKFLOW — FINANCE

```
Client
 ↓
Contract
 ↓
Project
 ↓
Work
 ↓
Invoice
 ↓
Payment
 ↓
Revenue
 ↓
Engineering cost
 ↓
Infrastructure cost
 ↓
Project profitability
```

## 95. DEVELOPMENT RULES FOR CLAUDE CODE

When implementing:

1. Inspect before changing.
2. Understand existing architecture.
3. Make incremental changes.
4. Keep changes modular.
5. Run tests after meaningful changes.
6. Fix errors before moving forward.
7. Do not ignore warnings that indicate real problems.
8. Do not break existing functionality.
9. Update documentation.
10. Use migrations for database changes.
11. Never hard-code secrets.
12. Never bypass authorization.
13. Never trust frontend validation alone.
14. Prefer reusable components.
15. Prefer reusable services.
16. Avoid unnecessary dependencies.
17. Keep APIs consistent.
18. Keep database relationships clean.
19. Use transactions where necessary.
20. Handle concurrency correctly.
21. Design integrations to be retryable.
22. Make webhook handlers idempotent.
23. Log important system events.
24. Write tests for business-critical behavior.
25. Treat security as a requirement, not a later improvement.

## 96. IMPLEMENTATION CHECKLIST

Maintain a live implementation checklist.

Example:

```
FOUNDATION
[ ] Authentication
[ ] Organizations
[ ] Users
[ ] RBAC
[ ] Audit
[ ] Settings

HR
[ ] Employees
[ ] Departments
[ ] Teams
[ ] Leave
[ ] Attendance
[ ] Payroll

RECRUITMENT
[ ] Job requisitions
[ ] Job postings
[ ] Candidates
[ ] Interviews
[ ] Assessments
[ ] Offers
[ ] Hiring
[ ] Onboarding
[ ] Offboarding

ENGINEERING
[ ] Projects
[ ] Tasks
[ ] Sprints
[ ] Git
[ ] PRs
[ ] CI/CD
[ ] Releases
[ ] Deployments

FINANCE
[ ] Invoices
[ ] Expenses
[ ] Vendors
[ ] Procurement
[ ] Budgets
[ ] Profitability

DEVOPS
[ ] Servers
[ ] Domains
[ ] SSL
[ ] Cloud
[ ] Assets
[ ] Incidents

AI
[ ] Assistant
[ ] Search
[ ] Summaries
[ ] Automation

AUTOMATION
[ ] Event system
[ ] Rules
[ ] Actions
[ ] Approvals
[ ] Scheduled jobs
```

Update this checklist as features are actually completed.

## 97. HOW YOU SHOULD WORK WITH ME

You are operating as the implementation agent.

Do not repeatedly ask me to make trivial architectural decisions that you can reasonably make yourself.

When a decision affects the architecture significantly:

1. Explain the decision.
2. Give the recommended approach.
3. Explain the tradeoff briefly.
4. Then implement it when appropriate.

If the repository already has a technology choice, prefer preserving it unless there is a strong technical reason to change.

Do not rewrite the entire project just because another architecture might be theoretically better.

## 98. QUALITY STANDARD

The final product should be something that a real software company could use internally.

It must not feel like:

* a student project
* a CRUD tutorial
* a template dashboard
* a collection of unrelated pages

It should feel like one unified product.

The user should be able to navigate naturally from:

```
Employee
→ Team
→ Project
→ Task
→ Repository
→ PR
→ Deployment
→ Invoice
→ Client
```

without losing context.

## 99. START NOW

Begin by:

1. Inspecting the repository.
2. Understanding the current implementation.
3. Creating the architecture plan.
4. Identifying the first implementation phase.
5. Creating the database/domain foundation.
6. Implementing authentication and organization management.
7. Implementing RBAC.
8. Implementing the core UI shell.
9. Implementing audit logging.
10. Adding tests.
11. Running the application.
12. Verifying the implementation.
13. Continuing phase by phase.

Do not stop after creating a plan.

Actually implement the system.

At the end of every major phase:

* run tests
* inspect errors
* fix issues
* update documentation
* update the implementation checklist
* verify the application manually where possible

The ultimate goal is:

A complete, production-grade ERP built specifically for software companies, where engineering activity, business operations, HR, recruitment, finance, clients, infrastructure, DevOps, automation, and AI operate as one connected system.
