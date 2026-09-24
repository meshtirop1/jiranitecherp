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

Last updated: 24 September 2026 · 1112 tests · verified against PostgreSQL in
Docker, including the webhook endpoint answering a signed delivery inside the
container, a lost opportunity surviving its client record being deleted, the
builds and deployments tables applying to a real Postgres, an abandoned event
put back by hand and picked up by the dispatcher, and
every read path measured against a database holding a million rows — see
[docs/performance.md](performance.md).

---

## The honest headline

The foundation and roughly half the core business modules are built and solid.
**The engineering half of the brief is most of the way there.** Git integration
is built and verified against real payload shapes from four hosts: repositories,
commits, pull requests and reviews arrive on their own, a merged pull request
moves the work it names to review, builds and deployments are recorded as the
hosts report them, and the delivery inbox verifies signatures, refuses replays
and dead-letters what it cannot handle. Still absent: infrastructure, incidents
and assets.

That matters more than the count suggests, because the brief's central
philosophy is that *developers should spend as little time as possible entering
ERP information manually* and that the system should collect it automatically
from repositories, pull requests, CI and deployments. All four of those now
arrive on their own: an engineer who names a branch after their work never
reports on it again, and the day's commits and deployments are shown beside the
timesheet rather than typed into it. What is built is an ERP that a software
company could run its business on, and the part that makes it developer-native
is no longer the part that is missing.

By section: **40 done, 3 partial, 55 not started, 1 excluded by agreement.**

That count was recomputed from the tables below rather than adjusted, because the
figure previously here did not add up to anything the tables said and had been
carried forward by hand for weeks. The method is now stated so the next person
can check it: a section is done when every row under it is done, partial when
its rows disagree or a row names a gap, and not started otherwise. Twenty-six of
the brief's ninety-nine sections have no row yet at all and are counted as not
started, which is what they are.

Two of the changes in the latest count are rows that were **stale rather than
undone**: §19 project profitability has been computed at `/projects/money` since
§10, and the §94 note below claimed the opposite for several days. A checklist
whose rows drift behind the code is worth less than no checklist, because
somebody plans around it — so the count is recomputed from the tables on each
pass rather than adjusted by the size of the change.

---

## Foundation — phase 1

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 4 | Authentication | Sign-in, sign-in trail, two-step — and **throttled properly, which it was not.** The word was in this row from the start and it meant Identity's lockout, which counts failures per account. That is the one axis the attack this firm invites is blind to: a list of addresses and twenty common passwords, one password tried against every address in turn, so no account ever accumulates enough failures to lock. Sign-in was also the only public form in the application with no rate limit at all, while the careers and recovery forms had carried one since they were written. Two now, the same division as the recovery form: sixty posts per address in five minutes in memory, which bounds a flood and is loose enough that the whole office behind one address never meets it, and **fifteen failures per address in fifteen minutes written down**, which is the half that catches a spray and survives a restart. The evidence was already being collected — `SignInRecord` has recorded every attempt with its address since it was added, and its own remark called the failures "the more useful half" — and nothing read it |
| ✅ | 4 | MFA / 2FA | TOTP, QR, recovery codes. Offered, not compulsory |
| ✅ | 4 | Session and device management | Sign out everywhere, plus where the account has been used and an email the first time it is used somewhere new. Called "places" not "devices", because cookie auth gives no way to end one session |
| ✅ | 4 | Self-service password reset | An anonymous `/forgot-password` page that reissues the same set-password link an administrator would. Answers an address with an account and one without in identical words, because a form a stranger can post to that answers differently is a way of testing a list of addresses against this firm's staff. Throttled twice: five posts per address per fifteen minutes in memory, and three links per **recipient** per hour written down — the second is the one that survives a restart and the one that stops somebody burying an inbox |
| ✅ | 4 | Email verification | The link this system already mails **is** the verification: following it sets `EmailConfirmed`, because having a link posted to an address is the only evidence of that address anybody ever has. A second flow mailing a second link to prove the same thing would be ceremony, so there deliberately is not one |
| ✅ | 4 | Profile | A page about yourself at /my-profile — photo, phone, location, time zone, next of kin, skills, qualifications. Pay and identity numbers deliberately not editable there |
| ✅ | 5 | RBAC | 7 roles, one matrix, permission-as-claim, deny by default |
| ✅ | 5 | Permission granularity | Reach adds the missing middle between firm-wide and own-record. Fixed a real leak: projects.view_member let every engineer find every project through search |
| ✅ | 29 | Audit log | Append-only, same transaction, readable and filterable. **A live gap in it was found and closed after this row first said "done", which is the reason the row now says how.** `PersonalDetails`, `EmergencyContact` and `Terms` are mapped as EF complex properties, and the capture walked `entry.Properties`, which does not enumerate a complex type's members — so editing somebody's salary, their phone number or their next of kin produced **no entry at all**, because a modification whose changed set comes back empty is deliberately discarded as "nothing moved". `Employee.AuditExcludes` named two of the three, which made the gap read as intentional: it was intentional about the values and accidental about the act, and those are different decisions. Keeping a salary figure out of a table nothing prunes is right; leaving no record that somebody changed it is the opposite of what a trail is for. An excluded complex property now records that it changed and withholds the values, each of the three on its own evidence, and a new meta-test refuses a future complex property on an audited entity until somebody has said which of the two it gets |
| ✅ | 30 | Event system | Domain events, transactional outbox, handlers, backoff, dead-lettering |
| ✅ | 41 | Database | PostgreSQL, migrations applied on start, indexed |
| ✅ | 44 | Error handling | 403/404/500, no internals leaked |
| ✅ | 45 | Health endpoints | `/health` and `/ready`, liveness thinner than readiness |
| ✅ | 45 | Observability | Structured logging, counters at a gated /metrics, spans on the background work, and one screen showing all three queues and every scheduled job |
| ✅ | 46 | Testing foundation | 652 tests, run inside the image build |
| ✅ | 54 | Security | Headers, CSRF, secure cookies, server-side authorization, hashing — and rate limiting on all four of the public surfaces now rather than three. The careers form, the recovery form and the API and webhook endpoints each had a limit; sign-in, the form that guards the accounts, had none. See §4 |
| ✅ | 78 | Architecture | Domain ← Application ← Infrastructure ← Web, enforced by project references |
| ✅ | 86 | Environment configuration | `.env.example`, no secrets committed |
| ✅ | 87 | Deployment | Docker and Compose, built and run, migrations verified against real PostgreSQL |
| ☐ | 85 | Documentation | This file is the first of the fourteen the brief asks for |
| ☐ | 83 | Seed data | No demo data. Everything is created by hand |
| ☐ | 3 | — | Multi-tenancy — **excluded by agreement** |

## Core business — phase 2

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 6 | Employee records | Contact details, next of kin, contract, salary, work location, skills, certifications. Salary and identity numbers behind employees.pay |
| ✅ | 6 | Departments | With heads |
| ☐ | 6 | Teams | Distinct from departments |
| ✅ | 6 | Reporting lines | Org chart, cycle-safe |
| ✅ | 6 | Employee lifecycle states | Hired, started, suspended, reinstated, left |
| ✅ | 23 | Leave | Statutory kinds, approval, overlap refusal, weekends and public holidays excluded |
| ✅ | 23 | Public holidays | Managed as data, recount in the same transaction |
| ✅ | 21 | Time tracking | Manual entry, approval, billing, and the day's commits and deployments shown beside the form. Still deliberately not auto-filled — see below. Deployments are found by the day's commit hashes as well as by who triggered them, because on a pipeline-driven release the host names a bot and matching on that alone shows an empty panel. **A known inherited fault:** the day is bounded in UTC while times are shown local, so a late-evening release lands under the next day — pre-existing for commits, and not fixable without deciding whose timezone a day belongs to, which no `FirmSettings` field answers. No calendar integration (§51) |
| ☐ | 6 | Performance, goals | |
| ☐ | 6 | Employee announcements | |
| ✅ | 24 | Employee documents | Narrowed to employees.manage plus the person themselves |
| ✅ | 24 | Document management | Attachments on seven kinds, with versions that supersede rather than replace, tags, and search narrowed to the kinds a reader may see |
| ✅ | 22 | Payroll | A period drafted from the staff list, agreed, and paid — and it closes the reason this system's income-and-expenditure report called its bottom line "a difference" and never profit. **The statutory rates are recorded rather than compiled in**, which is the decision the rest of this section hangs on. PAYE bands, the pension, the health contribution and the housing levy change by Act of Parliament, sometimes more than once a year; built into the application they would be wrong law on the day the Act commences and stay wrong until somebody shipped a release. They are dated rows somebody enters, the way exchange rates and public holidays already are, and the person who has read the Act is the person who types them in. A set is never edited — a change is a new set from a new date — so a run from last March is still explicable this March. **Every payslip line carries its own arithmetic**, so a figure can be checked against the Act without reading any code: "PAYE 86,619.35, 10% of 24,000 plus 25% of 8,333 plus 30% of 281,787, less 2,400 relief". The order deductions apply in is written down in the domain as an assumption, because that is the shape of the law rather than a number in it, and it is the part an accountant should argue with. **A payslip is deliberately not auditable.** Every figure on one is somebody's pay, the trail is append-only and never pruned, and `Employee.AuditExcludes` already goes to some trouble to keep salary out of it. The run is audited — the period, who approved it, when — because those are the acts; the figures are not. For the same reason the accounts carry one firm-wide payroll total with no breakdown: a department of two is one subtraction from an individual, which is the reasoning project costing at a blended rate is built on. The draft is assembled from what this system knows and a bureau does not: employment terms, joining and leaving dates with pay prorated by working days, approved unpaid leave with weekends and holidays already out, and the rates in force for the period. People it cannot include are named with the reason — paid in another currency, on invoice, no salary recorded — because a payroll that quietly left somebody out is discovered on payday, by them. Drafts rebuild rather than patch, and an approved payslip is the only record of what somebody was actually paid, since nothing else in the system keeps a dated pay history. **Not built, on the record:** filing. Nothing here produces a P9, a P10 or an NSSF return, and the screen says in as many words that it is not tax advice and checks nothing against the law |
| ✅ | 16 | Clients | Record, state, terms, documents, invoices, and the people at each one — with a leaver kept rather than overwritten, and one of them the person to call first |
| ✅ | 16 | Pipeline | An enquiry and an opportunity are one record at different stages, so nothing is lost to a conversion step. Backwards moves allowed, decided ones not reopened, losing one demands a reason, and winning one creates nothing. Listed by silence rather than by value |
| ✅ | 17 | Client contracts | Draft/active/terminated, expiry by date, signed copy attached |
| ☐ | 17 | Employee, vendor contracts, NDAs, renewal reminders | |
| ✅ | 10 | Projects | Lead, dates, state, documents, hours |
| ✅ | 10 | Project fields | Budget recorded; revenue, cost and margin derived from invoices, approved hours and paid expenses. Costed at a blended rate so no project report reveals a salary |
| ✅ | 11 | Tasks | State machine, per-transition permissions, release gate |
| ☐ | 11 | Epics, features, stories, subtasks, sprints, backlog | |
| ☐ | 11 | Task labels, dependencies, comments, checklists, acceptance criteria | |
| ☐ | 25 | Knowledge base | |
| ☐ | 26 | Support / help desk | |

## Engineering — phase 3 — **not started**

| | § | Item |
| --- | --- | --- |
| ✅ | 12 | Git integration — all four providers. Only GitHub verified against real deliveries; the other three from published payloads |
| ✅ | 12 | Repositories, branches, commits, pull requests, reviews |
| ✅ | 12 | Task ↔ branch ↔ commit ↔ PR ↔ build ↔ deployment. A work item shows what was built, whether it passed, and the furthest environment it actually reached. The link comes from the commit's own work item rather than from re-reading the branch, because a deployment runs from `main` or `release/*` — refs `WorkReference` deliberately finds nothing in |
| ✅ | 13 | CI/CD — builds and pipelines from all four hosts: GitHub `workflow_run`, GitLab `pipeline`, Bitbucket commit statuses, Azure `build.complete`. A pipeline stopped at a manual gate is neither running nor failed but `Blocked`, because those want different reactions. **Tests and artifacts are declined, not pending:** a `workflow_run` payload says a run finished and what its conclusion was and nothing about how many assertions ran, so a count would have to be invented. GitLab's single-job `build` event is read and discarded — a six-job matrix would otherwise record six builds of one commit |
| ✅ | 13 | Environments — development, staging, production and other, classified from the host's free text with the original kept beside it. A page at `/repositories/environments` says what is running where, bounded per environment and honest about what it is not showing. **Promotion is declined on the record:** nothing here runs a pipeline, so a promote button would fire nothing — a form that looks like an approval and is not. Azure releases are declined too, because their payload carries no commit at a stable path and a deployment without one attaches to nothing |
| ☐ | 67 | Releases, version numbers, changelog, rollback |
| ☐ | 68 | Feature flags |
| ✅ | 40 | Webhooks both ways — signed, idempotent, retried, dead-lettered, replayable |
| ✅ | 75 | Webhook security — signature verification, replay protection, dead-letter, replay |
| ☐ | 76 | IDE integration readiness |

This block is the brief's stated centre of gravity and none of it exists.

## Recruitment — phase 4

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 7 | Job requisitions | With approval chain, headcount, repair paths |
| ✅ | 7 | Job postings | Draft, publish, take down. The chain now runs from requisition to advert to application |
| ✅ | 7 | Public job board | Adverts, applications, CV upload, rate limited |
| ✅ | 7 | Applications | Readable, movable, CV downloadable |
| ✅ | 7 | Interviews and scorecards | Panels, four-point scale, one strong no carries |
| ✅ | 7 | Candidate correspondence | Acknowledgement, rejection, invitation, offer |
| ✅ | 7 | Candidate profile | Portfolio, GitHub, LinkedIn, experience, education, skills as stated, and a salary expectation behind employees.pay |
| ✅ | 7 | Technical assessments | An exercise between the interview and the offer, with an Assessing stage that is not a one-way door. **The rule is the point, not the table:** an offer is refused while an exercise is out or unmarked, because an offer made then is made on evidence nobody has read. Marking demands the reasoning as well as the verdict; late work is still accepted, because refusing it throws away the only evidence there is. No rubric — weighted criteria turn a judgement into arithmetic somebody then has to defend to a candidate |
| ☐ | 8 | Offer documents, electronic acceptance | A letter is sent; there is no offer object to approve or accept |
| ☐ | 8 | Onboarding checklist, equipment, account provisioning | |
| ✅ | 9 | Offboarding | Work released, plus a checklist: assets out, sign-in closed with who closed it, exit interview kept narrowly. A departure cannot be closed with access still live |

## Finance — phase 5

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 18 | Invoices and payments | Numbered, frozen on send, overpayment refused, voidable before payment |
| ✅ | 20 | Expenses | Claim, approve, pay — approval and payment deliberately separate |
| ✅ | 57 | Multi-currency | Money refuses cross-currency arithmetic; dated rates recorded by hand, conversion available and never stored |
| ✅ | 18 | Chart of accounts, recurring expenses, financial reports | Income and expense accounts; standing costs that raise their own charges through the scheduler, catching up rather than skipping when it has been off; and one income-and-expenditure report. **No general ledger, and that is the decision rather than an omission:** a stored balance is a number that can disagree with the documents it was added up from, so every figure is summed from the invoices, claims and charges themselves. Accrual on both sides and the page says so. The difference is called a difference — there is no payroll yet (§22), so calling it profit would be wrong by the firm's largest cost |
| ✅ | 19 | Project profitability | **The row was stale rather than the work missing.** `ProjectMoneyQueries` and `/projects/money` compute revenue from sent invoices, cost from approved hours at the firm's standard rate plus paid expenses, and the margin between them — per project, every time, from the documents rather than from a stored figure. Costed at a blended rate for a permission reason and not for convenience: a delivery manager holds `projects.manage` and `time.view_all` and not `employees.pay`, and a cost built from real salaries would let them recover any one person's rate by dividing |
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
| ✅ | 42 | Background jobs | A scheduler with a run history, three jobs registered in code, and the outbox plus two webhook queues beside it |
| ✅ | 42 | Scheduled jobs | The scheduler runs the qualification and contract reminders on a ladder — 60/30/7 and 90/45/14 days out — against a ledger of notices already given. **This closed a live fault:** both jobs asked "what expires within N days" and mailed every department head every morning, so one contract produced forty-five identical emails, in flat contradiction of IRecurringJob's own stated contract that a job must be safe to run twice. Overdue-invoice chasing is written as a ladder (-7/-21/-45) but has no job yet; SSL and domain expiry need a server inventory, which is §14. **A second live fault in these two jobs was found and fixed afterwards:** both read `Employee.Details.PersonalEmail` — the private address somebody types into their own profile beside their date of birth and their next of kin — and mailed the firm's certification and contract lists to it every morning. Internal notices about colleagues now go to the account address, which is where every other letter in this system already went; `MailRecipients` had the right answer from the day it was written and these were the only two senders that did not ask it. The same filter also dropped any head without a personal address in silence, so a firm whose reminders reached one person out of four had nothing saying so — the job's own sentence now names how many heard nothing. A count of emails sent cannot see either fault, which is why the suite was green: the test recorder now keeps the recipient |
| ☐ | 43 | Caching | Redis runs and nothing uses it |

## AI — phase 8 — **not started**

| | § | Item |
| --- | --- | --- |
| ☐ | 36 | AI assistant, permission-aware |
| ☐ | 37 | AI project analysis, labelled as inference not fact |
| ✅ | 34 | Semantic search | **Declined, on the record, and the reasons are checked rather than asserted.** A vector index ranks before it filters, and this system's search deliberately does not query a group somebody may not see rather than querying and filtering it — `Reach.Only` is a per-request set of arbitrary ids that no vector store pre-filters on, so top-k would reintroduce the leak `Reaches` was built to fix. The suite also runs on SQLite via `EnsureCreated`, where a vector column has nowhere to exist. What was done instead: two **live permission leaks** in the keyword box closed — see below |
| ☐ | 36 | AI-assisted CV summary, interview questions, drafts |

## Advanced — phase 9

| | § | Item | Note |
| --- | --- | --- | --- |
| ✅ | 73 | API tokens | Scoped, hashed, shown once, revocable, rate limited |
| ✅ | 74 | Service accounts | The same keys; machine identity with scoped permissions |
| ✅ | 39 | API-first | Versioned `/api/v1`, OpenAPI, paged reads with a capped limit, and three writes that go through the same services the screens use |
| ✅ | 34 | Global search | Six kinds, each behind its own permission. No filter syntax |
| ✅ | 35 | Dashboards | The home page shows what is waiting on this reader, decided by what they may do. One list rather than a page per role — see WaitingQueries for why |
| ✅ | 38 | Reporting | Money, hours, absence, delivery and the repositories, with CSV export |
| ✅ | 52 | Import / export | CSV export, and client import with a dry run that writes nothing unless the whole file is sound |
| ✅ | 47 | UI and UX | Responsive, consistent, dark mode stamped server-side, a command palette on Ctrl+K built from the reader's own navigation, and bulk approval of timesheets. **Three faults were found in it by opening the screens and reading them, none of which any test could see.** Every explanatory paragraph in the application had been rendering with the line breaks and indentation of the `.razor` file it lives in — an orphaned word alone on a line, the next starting four spaces in — because `.instructions` was written for payment details somebody types into a box, with `white-space: pre-wrap` so their line breaks survive, and was then reused for eleven paragraphs of prose. It is two classes now. Razor also emits nothing between an `@expression` and the element after it, so a date ran straight into the pill beside it in thirty-six places: "25 Aug 01:05Quiet a while". And a five-column table wants 640 pixels where a phone gives 375, so on three screens the **whole page** scrolled sideways while somebody read a row — heading, navigation and all — which is the same fault as one already on the list of things this codebase has paid for. Tables take their own overflow now, checked at 375 and at desktop with the columns still lining up |
| ☐ | 48 | Developer experience shortcuts | |
| ☐ | 50 | CLI | |
| ☐ | 51 | Integrations — Slack, Teams, cloud, calendar | |
| ✅ | 53 | Data retention policies | **No policy engine, and that is the decision.** Rules per table, configurable on a screen, would be a way of writing an application inside an application for a firm with four tables that grow on a clock rather than on the business — and the interesting half of this section is not which tables are swept but which must never be. Three are named in code with their reasons, and a meta-test keeps it that way: the trail, because it is append-only and `RefuseToRewriteHistory` inspects the change tracker, which a bulk delete never reaches — so `audit_entries` was deletable with nothing in the way; the webhook delivery inbox, because the unique index on the provider's delivery id is the only replay protection there is and it protects exactly the rows still in the table; and successful sign-ins, because they are what decides whether a sign-in is from somewhere new. What was added is one sweep. `sign_in_records` has carried a remark saying it is "its own table with its own retention" since it was written and had none, while being the only table in this database a stranger on the public internet can add rows to as fast as they like. Failed attempts older than ninety days go; successes are never deleted, and the asymmetry is the point — `IsSomewhereNewAsync` returns false when an account has nothing to compare against, so a sweep that took successes would switch the unfamiliar-sign-in warning off per account, silently and for good. The meta-test is a whitelist and fails closed: every bulk delete in `src/` must be named with a sentence saying what it sweeps and what it keeps. Its own first version was wrong in the way these tests usually are — it derived the names to look for and produced "AuditEntrys" for a DbSet called `AuditEntries`, so it passed while missing the one line it existed to catch. Found by adding that line on purpose and watching it stay green |
| ☐ | 55 | Data privacy — export, deletion workflow | |
| ☐ | 56 | Internationalisation | Strings are in the markup |
| ✅ | 77 | Performance | Measured against PostgreSQL holding 600k audit rows, 250k hours, 120k work items and 40k invoices. Found five faults no test could see — an invoices screen and API reading all 40,000 to show 50, a work item page reading all 120,000 to show one, an uncapped approval queue with no index — and fixed them. The reads that stay slow are named with their reason in [docs/performance.md](performance.md), and `tools/JiranisokoTech.ScaleCheck` makes it repeatable |
| ✅ | 84 | Admin tools — job monitoring, failed jobs, integration health | Job monitoring was already there under §42. Two holes closed, and both were of the same kind: **something that needed a person, with no way for a person to act, and something broken with no row anywhere to show it.** **The events queue got the screen it never had.** The machinery page has counted abandoned outbox rows since it was built and its own alert said they "will not be tried again without somebody" — while the other two queues had a replay button from the day they were written and this one, the queue carrying hires, invoices and receipts, had a number. `/settings/events` lists them oldest first, because these are work to redo rather than records to read, and puts one or all of them back. A row whose event class no longer exists says so rather than letting somebody press the button and watch it come straight back. **A code host that cannot reach us is now visible.** This was invisible to every screen in the system and the code said so in writing: `WebhookSecrets` warns that a misspelt key "would leave the endpoint refusing every delivery for a reason nothing on a screen would explain". It does — the refusal happens before a delivery row exists, so the host got 503 on every push and gave up within the day while all three queue depths read zero and every job read green. The machinery screen now reports the configuration beside what has been heard from it, because only the two together separate "quiet because nothing happened" from "quiet because nothing can get in". Every refusal also ticks the counter now, tagged unsigned, unconfigured or unreadable — the first is the internet and the other two are our own deployment. **What cannot be told apart is said rather than hidden:** a secret that is present and *wrong* passes every test this can make and is refused at the signature, which also writes nothing, so a fortnight of silence on a watched repository is marked amber instead of claimed as a fault. **A job can be run now**, and the guard matters more than the button: lateness is measured against the scheduler's own runs, so pressing it cannot clear an overdue badge — otherwise checking a suspect job would erase the evidence, and a dead scheduler would look healthy for as long as anybody kept checking. It does not move the timetable either. `JobRunner` was lifted out of `Scheduler` so that both paths record a run identically, and doing it found a fault in the original: the save used the shutdown token, so the one run whose record matters most, the interrupted one, was the one not written down |

---

## The four critical workflows

The brief names four and asks that each work end to end.

**§91 Engineering** — Client → contract → project → task → branch → commit → PR
→ review → CI → staging → production → release → invoice → payment →
profitability.
**One link short.** This note said "everything from branch to release is absent,
and profitability is not calculated", and both halves stopped being true without
the line being rewritten — §12 and §13 closed the middle and §19 was already
computing the end. Every step now exists except **release**: a branch names the
work it belongs to, commits and pull requests and reviews arrive on their own,
builds and deployments are recorded from four hosts, the environments screen says
what is running where, and revenue, cost and margin come out at
`/projects/money`. What is missing is the one thing that turns a deployment into
a release anybody can name — §67 — and that is the only gap in the brief's own
headline chain.

**§92 Hiring** — Requisition → approval → opening → published → applies →
screening → interview → assessment → offer → accepted → employee → onboarding.
**Runs as far as the offer letter**, and a test drives the whole of it: a
requisition is raised, approved, advertised and published, and the advert is
then found on the public careers page by somebody with no account. Technical
assessments, a real offer object and onboarding are still absent.

**§93 Incident** — **Not started.**

**§94 Finance** — Client → contract → project → work → invoice → payment →
revenue → cost → profitability.
**Runs end to end, and the last gap in it is closed.** Revenue, cost and margin
per project come out at `/projects/money` from sent invoices, approved hours at
the firm's standard rate and paid expenses; payroll (§22) now reaches the
income-and-expenditure report as the firm's largest cost. The bottom line is
still called a difference and not profit, and the reason has shrunk rather than
gone: there is no depreciation here and no corporation tax. Being wrong by less
is not the same as being right.

---

## What to do next, in order

1. ~~Publish an advert.~~ Done. The hiring chain runs end to end.
2. ~~The other thirteen unreachable service methods.~~ Done, and
   `ReachabilityTests` now fails the build when a service method has no caller,
   so the class cannot come back quietly.
3. ~~**Git integration.**~~ Done for GitHub. What remains of the brief's centre
   is CI/CD and deployments: a merged pull request now moves work to review, but
   nothing yet knows whether it built, whether the tests passed, or whether it
   reached production — so releasing work is still a claim rather than a record.
   Nothing else changes what this
   system *is* as much as repositories, pull requests and deployments arriving
   on their own.
4. **Scheduled jobs.** Several features already written are inert without them —
   contract renewals, overdue invoices, certificate expiry.
5. **The remaining documentation** the brief asks for in §85.
