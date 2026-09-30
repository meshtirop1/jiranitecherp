# How far the system meets the brief's standing rules

Three sections of the brief are not features: section 1's twenty principles, section 79's
module structure, and section 81's definition of done. They cannot be built on their own —
they are met or missed by everything else — so this is the assessment, rule by rule, with
the evidence for each and the gaps named. The section-by-section status is in
[the checklist](implementation-checklist.md); this is the cross-cutting view of it.

`✅` met · `◐` met in part, with the gap said · `☐` not met

## Section 1 — principles

| | # | Principle | Where it stands |
|---|---|---|---|
| ◐ | 1 | Everything is connected | Work links to projects, commits, pull requests, builds, deployments, time and invoices; a hire becomes a staff record, onboarding and payroll; an incident names the service. The end-to-end tests walk three chains across modules. The knowledge base and help desk (§25, §26) exist now and are connected — an article is reviewed and owned, a ticket names its client. What is still not connected is any relationship a report could walk generically: every link is a typed key some query knows about, which is §70's and §72's remaining gap. |
| ✅ | 2 | Avoid duplicate data entry | A branch named for a task links its commits without anybody typing the link; a hire is made from the accepted offer; invoice lines come from approved hours. |
| ◐ | 3 | Automate repetitive administrative work | Git activity, reminders, renewals, retention and outbound webhooks run on their own, and WHEN / IF / THEN rules on /automation can raise work, notify and add checklist lines (section 31; [automation](automation.md)). Rules cannot yet deploy, approve or pay, or combine conditions with OR. |
| ✅ | 4 | Developers work in their tools and the ERP follows | Pushes, pull requests, reviews, builds and deployments arrive by signed webhook from four hosts; a merge moves the work to review. |
| ✅ | 5 | Every important action generates an event | Domain events are written through a transactional outbox and dispatched with retry and dead-lettering; subscribers can receive them by webhook. |
| ✅ | 6 | Every important change is auditable | Every change is written in the same transaction, including role grants and complex-type members; sensitive reads are recorded too. See section 29 in the checklist. |
| ✅ | 7 | Permissions are granular | 92 permissions of the form `area.action`, eighteen roles, reach for department, project, team and own-record scope, separations held by tests. See [architecture](architecture.md#authorization-strategy). |
| ☐ | 8 | Multi-company | **Out of scope by agreement.** One firm; `FirmSettings` is one row. |
| ✅ | 9 | Future modules without rewrites | Layered, with the layering now checked by `LayeringTests`; a module is a domain folder, a service and pages, and the outbox, audit and permission machinery apply to it without change. |
| ◐ | 10 | AI throughout | An assistant that looks things up as the signed-in person, project readings labelled as inference, and recruitment drafts (sections 36, 37; [ai](ai.md)). Off until an API key is set. Not yet in most modules, and not yet exercised against the live API with a working key. |
| ◐ | 11 | Every module exposes an API | `/api/v1` covers clients, invoices and payments, projects, work, job openings and feature flags, with scoped keys. HR, payroll, recruitment's internal side, assets, incidents, procurement and contracts have none. |
| ◐ | 12 | Every module supports automation | Every module raises events, and the rules engine can act on 27 of them; rules can raise work, notify, email staff, add checklist lines and call a webhook subscription. What a rule can do is limited to those actions. |
| ✅ | 13 | Responsive | Fluid layout; every table carries its own horizontal scroll so the page never widens. Checked at 375 pixels on the pages changed in this work. |
| ◐ | 14 | Desktop, tablet and mobile | As above. `PhoneWidthTests` now holds the stylesheet's side of it — that width rules exist, that a touchscreen gets its own, and that no layout file still switches at the Blazor template's 641 pixels. What is still checked only by hand is a rendered page: proving one looks right at 375 pixels needs a browser comparing `scrollWidth` against `clientWidth`. |
| ✅ | 15 | Fast and professional | Every read path measured against a million rows — see [performance](performance.md). |
| ✅ | 16 | Errors are understandable and actionable | A refusal is a sentence on the page saying what to do; an unexpected error shows a reference that matches a trace in the logs. The end-to-end tests fail on the page's own sentence. |
| ✅ | 17 | Production-ready | Docker, a proxy with automatic HTTPS, migrations at start, health and readiness, JSON logs, retention, CI that builds the image, and nightly backups whose restore the suite runs against PostgreSQL ([backups.md](backups.md)). |
| ✅ | 18 | No fake functionality | Where something does not exist the screen says so — the sessions paragraph on the security page is the example. |
| ✅ | 19 | No TODO placeholders | None in the source. |
| ✅ | 20 | No frontend pretending to a backend | Pages call the services that enforce the rules; `EnforcementTests` fails the build for a permission nothing checks, and the workflow tests post the real forms. |

## Section 79 — module structure

Four projects, depending inwards only — `Domain` ← `Application` ← `Infrastructure` ← `Web`
— which `LayeringTests` now checks against the compiled assemblies. Inside each, one folder
per domain, with the same name in each layer.

| Brief's module | Here | |
|---|---|---|
| Identity | `Infrastructure/Identity`, `Web/Identity` | ✅ |
| Organizations | `Settings` — one firm | ✅ by agreement |
| HR | `People`, `Performance`, `Time` (leave and hours) | ✅ |
| Recruitment | `Recruitment` | ✅ |
| Projects, Tasks | `Work` | ✅ |
| Engineering, Git, CI/CD | `Engineering` — repositories, commits, pull requests, builds, deployments, releases | ✅ |
| DevOps | `Platform` — services, environments, resources, feature flags | ✅ |
| Assets | `Assets` | ✅ |
| CRM | `Clients` — clients and opportunities | ✅ |
| Contracts | `Contracts`, `Renewals` | ✅ |
| Finance | `Money`, `Accounting`, `Currencies` | ✅ |
| Payroll | `Payroll` | ✅ |
| Expenses | `Money` — claims, approved and paid with the rest of what leaves the firm | ✅ |
| Procurement | `Procurement`, `Vendors` | ✅ |
| Support | `Support` — tickets, the promise clock, escalation | ✅ |
| Incidents | `Incidents` | ✅ |
| Documents | `Documents` | ✅ |
| Knowledge | `Knowledge` — articles and their review dates | ✅ |
| Notifications | `Notices`, `Mail` | ✅ |
| Automation | `Automation`, `Scheduling`, `Messaging`, `Integrations` (outbound webhooks) | ◐ rules engine built; no deploy, approve or pay actions |
| AI | `Ai` in Application and Infrastructure | ◐ assistant, project reading, recruitment drafts; see [ai](ai.md) |
| Reporting | `Reporting` | ✅ |
| Audit | `Audit` | ✅ |
| Integrations | `Integrations`, `Api` | ◐ four Git hosts and two outbound destination kinds; no cloud, payment or calendar provider — see §51 |

## Section 81 — definition of done

What each item means here, and whether the system holds to it everywhere or only mostly.

| | Item | How it is met |
|---|---|---|
| ✅ | Database model, migration | Every entity is mapped in a configuration class; migrations are generated, and the PostgreSQL tests apply them as production does. `has-pending-model-changes` is clean. |
| ✅ | Backend logic, validation | Rules live on the entities and in the services, not in pages; a page's validation is for the person, and the domain refuses regardless — the work-item kind is the recent example. |
| ◐ | API | For the modules listed under principle 11, not all. |
| ✅ | Authorization | Every page and endpoint names a permission; `EnforcementTests` and the role tests fail the build otherwise. |
| ✅ | UI | Every module has pages; `EveryPageOpensTests` opens every one with a fixed address, on both databases. |
| ✅ | Loading states | Pages render on the server and arrive complete, so there is no state between a request and its answer to show. The one failure mode this creates — a page drawn before its data — is now a build failure (`RenderedBeforeLoadedTests`). |
| ✅ | Empty states | Lists say what is not there in words, not a blank table. |
| ✅ | Error states | See principle 16. |
| ✅ | Notifications | In-app notices and mail, through the outbox so a failed send is retried. |
| ✅ | Audit logging | See principle 6. |
| ✅ | Tests | 1,400 and more, including three end-to-end workflows and a PostgreSQL run in CI. |
| ◐ | Documentation | Architecture, configuration, deployment, git integration, testing, security, automation, AI, backups, disaster recovery, performance, privacy and this file. Of section 85's list, `development`, `database` and `api` are not written; `setup` is configuration and the README, and `authentication` and `authorization` are in security. |
| ◐ | Accessibility | Every visible form control on every page with a fixed address has a name a screen reader can announce, checked by the page sweep; the first run of that check found the tax-band table, the chart of accounts and the navigation toggle unnamed. Pages behind an identifier, colour contrast and keyboard order are not checked by anything. |
| ✅ | Mobile responsiveness | See principle 13. |

For integrations:

| | Item | How it is met |
|---|---|---|
| ✅ | Authentication | Each host's own signature or token, verified in constant time; API keys hashed and scoped. |
| ✅ | Webhooks | Inbound from four code hosts; outbound to subscribers, signed. |
| ✅ | Sync | Deliveries are upserts keyed by the host's own identifier. |
| ✅ | Retry | The inbox and the outbox both retry with backoff and dead-letter what keeps failing, with a screen to put it back. |
| ✅ | Error handling | A refusal answers with a body and the right status, so a host retries a 5xx and gives up on a 4xx — see CLAUDE.md on why that needed fixing. |
| ✅ | Logging | Structured, with the trace identifier on every record. |
| ✅ | Idempotency | A delivery identifier seen before is acknowledged and not processed twice. |
