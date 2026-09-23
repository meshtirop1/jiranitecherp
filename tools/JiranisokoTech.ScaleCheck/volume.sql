-- Volume for the scale check.
--
-- Written as SQL rather than driven through the services on purpose. The services
-- enforce rules — an invoice needs a live contract, leave cannot overlap — and
-- satisfying all of them to reach a hundred thousand rows would take an hour of
-- machine time and would be measuring the writes, which is not the question. This
-- fills the tables the read paths scan, and nothing here should be mistaken for a
-- fixture anything else can rely on.
--
-- Deliberately not idempotent in the "safe to leave running" sense: it truncates
-- first. It is meant for a throwaway database and refuses to run against one that
-- looks like it holds real work, which the harness checks before calling it.
--
-- Row counts are chosen to be past the point where a sequential scan stops being
-- free and well short of where the check takes longer to run than anybody will
-- wait. A firm of this size will not reach these numbers for years; that is the
-- point of measuring them now.

TRUNCATE audit_entries, invoice_payments, invoice_lines, invoices, time_entries,
         work_items, projects, opportunity_activities, opportunities,
         client_contacts, clients, employees, departments
      RESTART IDENTITY CASCADE;

-- Departments: 12.
INSERT INTO departments ("Id", "Name", "Slug", "IsActive")
SELECT gen_random_uuid(), 'Department ' || n, 'dept-' || n, true
FROM generate_series(1, 12) AS n;

-- Employees: 400. Roughly a tenth have left, because "who is still here" is a
-- filter on almost every people query and a table where everybody is active never
-- exercises it.
INSERT INTO employees ("Id", "FullName", "JobTitle", "DepartmentId", "Status",
                       "StartsOn", "LeftOn", "WeeklyCapacityHours")
SELECT
    gen_random_uuid(),
    'Employee ' || n,
    CASE n % 5 WHEN 0 THEN 'Engineer' WHEN 1 THEN 'Analyst' WHEN 2 THEN 'Designer'
               WHEN 3 THEN 'Manager' ELSE 'Technician' END,
    (SELECT "Id" FROM departments ORDER BY md5("Id"::text || n::text) LIMIT 1),
    CASE WHEN n % 10 = 0 THEN 5 ELSE 2 END,
    DATE '2020-01-01' + (n % 1800),
    CASE WHEN n % 10 = 0 THEN DATE '2025-06-01' + (n % 300) ELSE NULL END,
    40
FROM generate_series(1, 400) AS n;

-- Clients: 2,000.
INSERT INTO clients ("Id", "Name", "Code", "Status", "PaymentTermDays",
                     "ContactName", "ContactEmail")
SELECT
    gen_random_uuid(),
    'Client ' || n,
    'client-' || n,
    CASE WHEN n % 8 = 0 THEN 4 ELSE 2 END,
    30,
    'Contact ' || n,
    'contact' || n || '@example.test'
FROM generate_series(1, 2000) AS n;

-- Contacts: about three per client.
INSERT INTO client_contacts ("Id", "ClientId", "Name", "JobTitle", "Email",
                             "IsMain", "AddedAt", "GoneAt")
SELECT
    gen_random_uuid(),
    c."Id",
    'Person ' || c."Code" || '-' || k,
    CASE k WHEN 1 THEN 'Operations' WHEN 2 THEN 'Finance' ELSE 'Procurement' END,
    'person' || k || '.' || c."Code" || '@example.test',
    k = 1,
    now() - (k || ' months')::interval,
    CASE WHEN k = 3 AND right(c."Code", 1) = '7' THEN now() - INTERVAL '2 months' END
FROM clients c CROSS JOIN generate_series(1, 3) AS k;

-- Opportunities: 8,000, most of them decided. The pipeline screen reads the open
-- ones, so a table that was all open would never exercise the filter.
INSERT INTO opportunities ("Id", "Title", "About", "ClientId", "OwnerId", "Stage",
                           "ValueMinorUnits", "ValueCurrency", "ExpectedOn",
                           "OpenedAt", "MovedAt", "ClosedAt", "Outcome")
SELECT
    gen_random_uuid(),
    'Opportunity ' || n,
    'Client ' || (1 + n % 2000),
    (SELECT "Id" FROM clients WHERE "Code" = 'client-' || (1 + n % 2000)),
    CASE WHEN n % 7 = 0 THEN NULL
         ELSE (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + n % 400)) END,
    CASE WHEN n % 10 < 4 THEN 1 + (n % 4) WHEN n % 10 < 7 THEN 5 ELSE 6 END,
    (n % 900 + 1) * 100000,
    'KES',
    DATE '2026-01-01' + (n % 365),
    now() - ((n % 700) || ' days')::interval,
    now() - ((n % 700) || ' days')::interval,
    CASE WHEN n % 10 >= 4 THEN now() - ((n % 600) || ' days')::interval END,
    CASE WHEN n % 10 >= 7 THEN 'Lost on price to the incumbent.' END
FROM generate_series(1, 8000) AS n;

-- Activities: about four per opportunity.
INSERT INTO opportunity_activities ("Id", "Kind", "What", "At", "OpportunityId")
SELECT
    gen_random_uuid(),
    1 + (k % 5),
    'Something happened, entry ' || k,
    o."MovedAt" - (k || ' days')::interval,
    o."Id"
FROM opportunities o CROSS JOIN generate_series(1, 4) AS k;

-- Projects: 6,000.
INSERT INTO projects ("Id", "Name", "Code", "DepartmentId", "LeadId", "ClientId",
                      "Status", "DueOn", "DeliveredAt", "BudgetCurrency",
                      "BudgetMinorUnits")
SELECT
    gen_random_uuid(),
    'Project ' || n,
    'proj-' || n,
    (SELECT "Id" FROM departments ORDER BY md5("Id"::text || n::text) LIMIT 1),
    (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + n % 400)),
    (SELECT "Id" FROM clients WHERE "Code" = 'client-' || (1 + n % 2000)),
    CASE WHEN n % 5 = 0 THEN 4 ELSE 2 END,
    DATE '2026-01-01' + (n % 500),
    CASE WHEN n % 5 = 0 THEN now() - ((n % 400) || ' days')::interval END,
    'KES',
    (n % 500 + 1) * 100000
FROM generate_series(1, 6000) AS n;

-- Work items: 120,000. The largest table a screen reads directly, and the one
-- whose board is filtered six ways.
INSERT INTO work_items ("Id", "Title", "ProjectId", "RaisedById", "AssigneeId",
                        "Status", "Priority", "EstimateMinutes", "DueOn",
                        "StartedAt", "CompletedAt", "Number")
SELECT
    gen_random_uuid(),
    'Work item ' || n,
    (SELECT "Id" FROM projects WHERE "Code" = 'proj-' || (1 + n % 6000)),
    (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + n % 400)),
    CASE WHEN n % 11 = 0 THEN NULL
         ELSE (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + (n * 7) % 400)) END,
    1 + (n % 6),
    1 + (n % 4),
    30 * (1 + n % 16),
    DATE '2026-01-01' + (n % 400),
    CASE WHEN n % 3 <> 0 THEN now() - ((n % 500) || ' days')::interval END,
    CASE WHEN n % 6 = 0 THEN now() - ((n % 400) || ' days')::interval END,
    n
FROM generate_series(1, 120000) AS n;

-- Time entries: 250,000. Read by the timesheet, the approval queue, the billing
-- run and three reports.
INSERT INTO time_entries ("Id", "EmployeeId", "On", "Minutes", "WorkItemId",
                          "ProjectId", "IsBillable", "ApprovedAt", "ApprovedById")
SELECT
    gen_random_uuid(),
    (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + n % 400)),
    DATE '2025-01-01' + (n % 600),
    30 * (1 + n % 12),
    NULL,
    (SELECT "Id" FROM projects WHERE "Code" = 'proj-' || (1 + n % 6000)),
    n % 4 <> 0,
    CASE WHEN n % 5 <> 0 THEN now() - ((n % 300) || ' days')::interval END,
    CASE WHEN n % 5 <> 0
         THEN (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + (n * 3) % 400)) END
FROM generate_series(1, 250000) AS n;

-- Invoices: 40,000, with lines.
INSERT INTO invoices ("Id", "ClientId", "Number", "Currency", "IssuedOn", "DueOn",
                      "Status", "SentAt", "ProjectId")
SELECT
    gen_random_uuid(),
    (SELECT "Id" FROM clients WHERE "Code" = 'client-' || (1 + n % 2000)),
    'INV-2026-' || lpad(n::text, 6, '0'),
    'KES',
    DATE '2025-01-01' + (n % 600),
    DATE '2025-01-31' + (n % 600),
    CASE WHEN n % 6 = 0 THEN 1 WHEN n % 6 = 1 THEN 2 WHEN n % 6 < 5 THEN 3 ELSE 4 END,
    CASE WHEN n % 6 <> 0 THEN now() - ((n % 500) || ' days')::interval END,
    (SELECT "Id" FROM projects WHERE "Code" = 'proj-' || (1 + n % 6000))
FROM generate_series(1, 40000) AS n;

INSERT INTO invoice_lines ("Id", "Description", "Quantity", "UnitMinorUnits",
                           "Currency", "InvoiceId")
SELECT
    gen_random_uuid(),
    'Line ' || k,
    1 + (k % 4),
    (k % 40 + 1) * 250000,
    'KES',
    i."Id"
FROM invoices i CROSS JOIN generate_series(1, 3) AS k;

-- Payments against about half of them, so "what is owed" has real arithmetic to do.
-- No currency on a payment: the invoice's currency is the authoritative one, and a
-- payment carrying its own would be a second answer to a question with one.
INSERT INTO invoice_payments ("Id", "On", "MinorUnits", "Reference", "InvoiceId")
SELECT
    gen_random_uuid(),
    i."DueOn" - 3,
    500000,
    'Ref ' || i."Number",
    i."Id"
FROM invoices i WHERE i."Status" >= 3;

-- Audit entries: 600,000. The append-only table nothing ever deletes from, which
-- makes it the one certain to be the largest in any installation that runs for a
-- year — and it is read by a screen with filters on four columns.
INSERT INTO audit_entries ("Id", "Action", "SubjectType", "SubjectId", "ActorId",
                           "ActorName", "OccurredAt")
SELECT
    gen_random_uuid(),
    CASE n % 4 WHEN 0 THEN 'created' WHEN 1 THEN 'updated' WHEN 2 THEN 'removed'
               ELSE 'viewed' END,
    CASE n % 6 WHEN 0 THEN 'WorkItem' WHEN 1 THEN 'Invoice' WHEN 2 THEN 'Client'
               WHEN 3 THEN 'Employee' WHEN 4 THEN 'Project' ELSE 'Opportunity' END,
    gen_random_uuid(),
    (SELECT "Id" FROM employees WHERE "FullName" = 'Employee ' || (1 + n % 400)),
    'Employee ' || (1 + n % 400),
    now() - ((n % 500) || ' days')::interval - ((n % 86400) || ' seconds')::interval
FROM generate_series(1, 600000) AS n;

ANALYZE;
