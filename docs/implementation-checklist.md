# Implementation checklist

The brief is a 99-section master prompt: a developer-native ERP for a software
company, where engineering activity, business operations, HR, recruitment,
finance, clients, infrastructure, DevOps, automation and AI are one connected
system. Section 96 of that brief asks for this file to exist and to be kept
live. It did not exist until now, which is why "is it done?" has been a matter
of opinion rather than a question anybody could answer.

**One agreed departure from the brief: section 3, multi-tenancy, is out of
scope.** This is built for Jiranisoko Tech Solutions and no other company. That
decision removes organisation switching, per-organisation isolation and
organisation-scoped permissions, and it is the reason `FirmSettings` is a single
row with a fixed key rather than a table of organisations.

`✅` done · `◐` partial, with the gap named · `☐` not started

Last updated: 23 September 2026 · 647 tests · verified against PostgreSQL in
Docker.

---

## The honest headline

The foundation and roughly half the core business modules are built and solid.
**The engineering half of the brief is not built at all** — no Git integration,
no CI/CD, no deployments, no infrastructure, no incidents, no assets.

That matters more than the count suggests, because the brief's central
philosophy is that *developers should spend as little time as possible entering
ERP information manually* and that the system should collect it automatically
from repositories, pull requests, CI and deployments. None of that collection
exists. What is built is an ERP that a software company could run its business
on; it is not yet the "software company operating system" the brief describes.

By section: **31 done, 22 partial, 45 not started, 1 excluded by agreement.**

---

## Foundation — phase 1

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 4 | Authentication | Sign-in, throttled, sign-in trail, two-step |
| ✅ | 4 | MFA / 2FA | TOTP, QR, recovery codes. Offered, not compulsory |
| ◐ | 4 | Session and device management | Sign out everywhere works; no per-device list, no suspicious-login detection |
| ☐ | 4 | Self-service password reset | An administrator must issue a link. There is no forgot-password page |
| ☐ | 4 | Email verification | |
| ◐ | 4 | Profile | Name, title, department, manager, capacity. No photo, phone, skills, certifications, location or time zone |
| ✅ | 5 | RBAC | 7 roles, one matrix, permission-as-claim, deny by default |
| ◐ | 5 | Permission granularity | Resource-level by ownership (own work, own file). No department, project or team scoping |
| ✅ | 29 | Audit log | Append-only, same transaction, readable and filterable |
| ✅ | 30 | Event system | Domain events, transactional outbox, handlers, backoff, dead-lettering |
| ✅ | 41 | Database | PostgreSQL, migrations applied on start, indexed |
| ✅ | 44 | Error handling | 403/404/500, no internals leaked |
| ✅ | 45 | Health endpoints | `/health` and `/ready`, liveness thinner than readiness |
| ◐ | 45 | Observability | Structured logging. No metrics, no tracing, no job monitoring |
| ✅ | 46 | Testing foundation | 647 tests, run inside the image build |
| ✅ | 54 | Security | Headers, CSRF, rate limiting, secure cookies, server-side authorization, hashing |
| ✅ | 78 | Architecture | Domain ← Application ← Infrastructure ← Web, enforced by project references |
| ✅ | 86 | Environment configuration | `.env.example`, no secrets committed |
| ✅ | 87 | Deployment | Docker and Compose, built and run, migrations verified against real PostgreSQL |
| ☐ | 85 | Documentation | This file is the first of the fourteen the brief asks for |
| ☐ | 83 | Seed data | No demo data. Everything is created by hand |
| ☐ | 3 | — | Multi-tenancy — **excluded by agreement** |

## Core business — phase 2

| | § | Item | Note |
| --- | --- | --- | --- |
| ◐ | 6 | Employee records | No personal details, emergency contacts, contract, salary, benefits, work location, skills or certifications |
| ✅ | 6 | Departments | With heads |
| ☐ | 6 | Teams | Distinct from departments |
| ✅ | 6 | Reporting lines | Org chart, cycle-safe |
| ✅ | 6 | Employee lifecycle states | Hired, started, suspended, reinstated, left |
| ✅ | 23 | Leave | Statutory kinds, approval, overlap refusal, weekends and public holidays excluded |
| ✅ | 23 | Public holidays | Managed as data, recount in the same transaction |
| ◐ | 21 | Time tracking | Manual entry, approval, billing. No automatic detection from Git, calendar or deployments |
| ☐ | 6 | Performance, goals | |
| ☐ | 6 | Employee announcements | |
| ✅ | 24 | Employee documents | Narrowed to employees.manage plus the person themselves |
| ◐ | 24 | Document management | Attachments on six kinds. No versioning, tags, search or relationships |
| ☐ | 22 | Payroll | |
| ◐ | 16 | Clients | Record, state, terms, documents, invoices. No leads, contacts, opportunities, pipeline or activities |
| ✅ | 17 | Client contracts | Draft/active/terminated, expiry by date, signed copy attached |
| ☐ | 17 | Employee, vendor contracts, NDAs, renewal reminders | |
| ✅ | 10 | Projects | Lead, dates, state, documents, hours |
| ◐ | 10 | Project fields | No budget, revenue, costs or profitability |
| ✅ | 11 | Tasks | State machine, per-transition permissions, release gate |
| ☐ | 11 | Epics, features, stories, subtasks, sprints, backlog | |
| ☐ | 11 | Task labels, dependencies, comments, checklists, acceptance criteria | |
| ☐ | 25 | Knowledge base | |
| ☐ | 26 | Support / help desk | |

## Engineering — phase 3 — **not started**

| | § | Item |
| --- | --- | --- |
| ☐ | 12 | Git integration — GitHub, GitLab, Bitbucket, Azure DevOps |
| ☐ | 12 | Repositories, branches, commits, pull requests, reviews |
| ☐ | 12 | Task ↔ branch ↔ commit ↔ PR ↔ build ↔ deployment mapping |
| ☐ | 13 | CI/CD — builds, pipelines, tests, artifacts |
| ☐ | 13 | Environments — development, staging, production |
| ☐ | 67 | Releases, version numbers, changelog, rollback |
| ☐ | 68 | Feature flags |
| ☐ | 40 | Webhooks, incoming and outgoing, signed and idempotent |
| ☐ | 75 | Webhook security — signature verification, replay protection, dead-letter |
| ☐ | 76 | IDE integration readiness |

This block is the brief's stated centre of gravity and none of it exists.

## Recruitment — phase 4

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 7 | Job requisitions | With approval chain, headcount, repair paths |
| ◐ | 7 | Job postings | Backend complete; **no screen can draft, publish or close one**, so the chain is broken |
| ✅ | 7 | Public job board | Adverts, applications, CV upload, rate limited |
| ✅ | 7 | Applications | Readable, movable, CV downloadable |
| ✅ | 7 | Interviews and scorecards | Panels, four-point scale, one strong no carries |
| ✅ | 7 | Candidate correspondence | Acknowledgement, rejection, invitation, offer |
| ◐ | 7 | Candidate profile | Name, email, phone, CV. No portfolio, GitHub, LinkedIn, skills, experience, education or salary expectation |
| ☐ | 7 | Technical assessments | |
| ☐ | 8 | Offer documents, electronic acceptance | A letter is sent; there is no offer object to approve or accept |
| ☐ | 8 | Onboarding checklist, equipment, account provisioning | |
| ◐ | 9 | Offboarding | A leaver's work is released. No exit interview, asset return or access removal |

## Finance — phase 5

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 18 | Invoices and payments | Numbered, frozen on send, overpayment refused, voidable before payment |
| ✅ | 20 | Expenses | Claim, approve, pay — approval and payment deliberately separate |
| ◐ | 57 | Multi-currency | Money refuses cross-currency arithmetic and totals say so. No exchange rates or conversion |
| ☐ | 18 | Chart of accounts, recurring expenses, financial reports | |
| ☐ | 19 | Project profitability | |
| ☐ | 61 | Procurement — purchase requests, orders, receiving | |
| ☐ | 62 | Vendor management | |

## Infrastructure — phase 6 — **not started**

| | § | Item |
| --- | --- | --- |
| ☐ | 14 | Servers, cloud, containers, databases, DNS, domains, SSL |
| ☐ | 15 | Asset management and lifecycle |
| ☐ | 27 | Incidents, severity, timeline, root cause |
| ☐ | 69 | Postmortems and corrective tasks |
| ☐ | 88 | Backups with tested restore |
| ☐ | 89 | Disaster recovery documentation |

## Automation — phase 7

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 60 | Approval engine | Chains, steps, resolvers, escalation, outcomes |
| ✅ | 30 | Event-driven reactions | A leaver releases their work; a requisition opens a chain; letters are sent |
| ☐ | 31 | Automation engine | No WHEN / IF / THEN rules anybody can configure |
| ☐ | 32 | Notification centre | Email only. No in-app centre, grouping or rules |
| ☐ | 59 | Notification rules | |
| ☐ | 33 | Calendar | |
| ◐ | 42 | Background jobs | Outbox dispatcher runs as a hosted service. No general queue or scheduler |
| ☐ | 42 | Scheduled jobs | Nothing runs on a timetable — no renewal reminders, no SSL expiry, no overdue chasing |
| ☐ | 43 | Caching | Redis runs and nothing uses it |

## AI — phase 8 — **not started**

| | § | Item |
| --- | --- | --- |
| ☐ | 36 | AI assistant, permission-aware |
| ☐ | 37 | AI project analysis, labelled as inference not fact |
| ☐ | 34 | Semantic search |
| ☐ | 36 | AI-assisted CV summary, interview questions, drafts |

## Advanced — phase 9

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 73 | API tokens | Scoped, hashed, shown once, revocable, rate limited |
| ✅ | 74 | Service accounts | The same keys; machine identity with scoped permissions |
| ◐ | 39 | API-first | Read-only `/api/v1`, versioned, OpenAPI. No writes, no pagination |
| ✅ | 34 | Global search | Six kinds, each behind its own permission. No filter syntax |
| ◐ | 35 | Dashboards | One home page and one standing report. No role-specific dashboards |
| ◐ | 38 | Reporting | Money, hours, absence, delivery, with CSV export. No engineering metrics |
| ◐ | 52 | Import / export | CSV export. No import |
| ◐ | 47 | UI and UX | Responsive, consistent. No command palette, keyboard shortcuts, dark mode or bulk actions |
| ☐ | 48 | Developer experience shortcuts | |
| ☐ | 50 | CLI | |
| ☐ | 51 | Integrations — Slack, Teams, cloud, calendar | |
| ☐ | 53 | Data retention policies | |
| ☐ | 55 | Data privacy — export, deletion workflow | |
| ☐ | 56 | Internationalisation | Strings are in the markup |
| ◐ | 77 | Performance | Indexed, paged where it matters. Untested at scale |
| ☐ | 84 | Admin tools — job monitoring, failed jobs, integration health | |

---

## The four critical workflows

The brief names four and asks that each work end to end.

**§91 Engineering** — Client → contract → project → task → branch → commit → PR
→ review → CI → staging → production → release → invoice → payment →
profitability.
**Broken.** Everything from branch to release is absent, and profitability is
not calculated. The business ends work end to end; the engineering middle does
not exist.

**§92 Hiring** — Requisition → approval → opening → published → applies →
screening → interview → assessment → offer → accepted → employee → onboarding.
**Broken at one link.** Everything exists except a screen to publish the advert,
so an approved requisition can never become a job anybody can apply to. Offers
and onboarding are also absent.

**§93 Incident** — **Not started.**

**§94 Finance** — Client → contract → project → work → invoice → payment →
revenue → cost → profitability.
**Works as far as payment.** Cost and profitability are not calculated.

---

## What to do next, in order

1. **Publish an advert.** One screen closes the hiring chain and makes four
   existing modules usable together. It is the cheapest large win here.
2. **The other thirteen unreachable service methods.** Project hold/cancel/lead,
   timesheet correction, department rename, approval reassign and skip,
   attachment removal. All built, none callable.
3. **Git integration.** The brief's centre. Nothing else changes what this
   system *is* as much as repositories, pull requests and deployments arriving
   on their own.
4. **Scheduled jobs.** Several features already written are inert without them —
   contract renewals, overdue invoices, certificate expiry.
5. **The remaining documentation** the brief asks for in §85.
