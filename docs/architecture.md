# Architecture

Section 2 of the brief asks for one document covering the current architecture and where it
is going. This is it. It describes what is in this repository, not what is planned, except
under the headings that say *proposed*; where something is not built, it says so. For what
is done section by section, see [implementation-checklist.md](implementation-checklist.md);
for the brief itself, [master-prompt.md](master-prompt.md).

## Current architecture

One ASP.NET Core 10 application, one PostgreSQL database, one Redis cache, in containers.

```
src/
  JiranisokoTech.Domain          entities, value objects, domain events — no dependencies
  JiranisokoTech.Application     services and the interfaces they need
  JiranisokoTech.Infrastructure  EF Core, Identity, Git providers, mail, files, jobs
  JiranisokoTech.Web             Blazor pages, the HTTP API, webhook endpoints
tests/
  JiranisokoTech.Tests           xUnit, mostly driving the real application
tools/
  JiranisokoTech.ScaleCheck      fills a throwaway database and times every read
docker/
  Dockerfile  compose.yaml  Caddyfile
```

Dependencies point inward only: `Web → Infrastructure → Application → Domain`. The domain
knows nothing about databases, HTTP or the framework, which is what keeps its rules testable
without any of them running. This is the separation section 78 asks for.

**Two rules the rest depends on.** A service is the only way in: pages, the API and webhook
handlers call the same application services, and none of them holds a business rule. And
events are facts in the past tense, carrying identifiers rather than entities, published only
after the transaction that raised them has committed.

The pages are **statically server-rendered** Blazor, so that signing in can write a cookie
and every form is a real POST a test can send. A handful of components opt into an
interactive circuit where a page needs it; the rest need no JavaScript. `CLAUDE.md` lists the
consequences, several of which have cost real time.

## Proposed architecture

Nothing about the shape needs to change to finish the brief. What is missing is modules, not
layers:

- **Support and knowledge** (sections 25–26): ordinary modules on the existing pattern.
- **A CLI** (section 50): a client of the public API, holding no rules of its own — the API
  already goes through the same services the pages do, which is the whole of what makes that
  possible.

Multi-tenancy (section 3) is out of scope by agreement: this is built for one firm, which is
why the firm's settings are a single row with a fixed key.

## Database strategy

PostgreSQL 18 in production; SQLite in memory for the test suite. EF Core 10, code-first,
with 48 migrations applied by the application on start — there is no separate migration step
to forget. Core data is relational: no JSON columns hold anything a query needs to read.

- **Money** is an integer count of minor units beside a three-letter currency. Arithmetic
  across currencies is refused by the type, not by convention.
- **Indexes** are declared beside each entity's mapping, about 160 of them; the scale check
  in `tools/` measures every read path against a database holding a million rows and names
  any that scans.
- **Uniqueness** is enforced by the database wherever two requests could race — one pay run
  per period, one release per version per repository — and never through a nullable column,
  because nulls are distinct in a unique index.
- **Owned collections** hold an aggregate's parts (invoice lines, approval steps); ordinary
  one-to-many is used once, for payslips, which is why that mapping is marked.

## Module structure

The same folder names run through all four projects, so a feature can be followed from its
domain rules to its page: People, Recruitment, Work, Engineering, Platform, Assets, Clients
and Business, Contracts, Money and Accounting, Payroll, Procurement, Vendors, Incidents,
Documents, Notices, Approvals, Audit, Integrations, Privacy, Settings, Automation. Section
79 lists Support and Knowledge as well; they do not exist yet. AI exists as `Ai` in Application,
Infrastructure and the pages.

## Authentication strategy

ASP.NET Core Identity over the same database, with its own tables.

- **Accounts are invite-only.** There is no self-registration; an administrator invites, and
  the invited person sets their own password from a link, which also confirms the address.
  The first owner is created once from configuration on an empty database.
- **Passwords**: twelve characters minimum, no composition rules; Identity's PBKDF2 hashing.
- **Lockout**: five failures, fifteen minutes. Every attempt is recorded with its address.
- **Two-step sign-in**: authenticator codes and eight one-time recovery codes. Offered, not
  yet required of anybody. Both secrets are **encrypted with the key ring** before they reach
  the database (`ProtectedUserStore`) — Identity's default stores them as they are, which made
  a database dump a working second factor for everybody. An administrator can turn somebody's
  second factor off, for a phone lost with its recovery codes.
- **Sessions**: an HttpOnly, Secure, SameSite cookie, eight hours sliding. The security stamp
  is checked every minute, so withdrawing somebody's access ends the session they are in, and
  an administrator can end every session an account has without withdrawing it. There is no
  per-device list: the server keeps none, and the security page says so.
- **Machines** authenticate to `/api/v1` with API keys, stored hashed and shown once.
- **Git hosts** authenticate each webhook delivery with the host's own signature or token.

## Authorization strategy

Permissions, not roles, are what the code checks. About ninety permissions are declared in
`Application/Authorization/Permissions.cs`, each named `area.action`. A role is a bundle of
them, written in the same file and synced into the database on every start, so the code and
the database cannot disagree about what a role grants.

- A page or endpoint names the permission it needs: `permission:invoices.manage` is a policy
  the policy provider builds on demand. Nothing checks a role name.
- **Reach** narrows what a permission shows. Somebody without `projects.view_all` sees the
  projects they lead or work on; a department head sees their department's absences. See
  `Infrastructure/Authorization/Reaches.cs`.
- Everything is enforced on the server. The navigation hides what somebody cannot open, and
  the page refuses them anyway.
- `EnforcementTests` fails the build for a permission that is granted and checked nowhere,
  because that is a capability somebody has been given and cannot use.

Eighteen roles are declared: sixteen of the brief's list, plus department head and
interviewer. The two missing are argued on `Roles` — "super administrator" is the owner,
since with one firm there is nothing above it to administer, and "client" waits for a client
portal, because a role that opens nothing is an account that cannot be used. Tests hold the
separations the roles were built around: approving and paying a claim, drafting and sending
an invoice, agreeing a contract and invoicing against it, ordering and receiving goods,
running and paying the payroll, deciding and carrying out an erasure — no role below the top
holds both halves of any of them.

**Grants below the firm are relationships, not rows.** The brief lists organisation,
department, project, team and record scope. Here the permission says what kind of act it is
and reach says which records: the projects somebody leads or is on, the people who report
to them, their own claims. That covers the department, project, team and record cases
without a table of per-record grants, which is what makes "who can see this invoice" a
question somebody can still answer.

**Roles are not edited on screen, on purpose.** The matrix is in code so the seeder, the
policies, the tests and the roles page all read the same thing; a matrix editable at run
time would be one the tests could no longer vouch for, and the separations above would hold
only until somebody ticked a box. A firm that needs a new role asks for one in the code, and
the tests check it.

## Integration strategy

Every external system sits behind an interface in Application, implemented in
Infrastructure, so a new provider does not touch business logic.

- **Git hosts**: one `IGitProvider` per host (GitHub, GitLab, Bitbucket, Azure DevOps) turns
  that host's payloads into the same `GitEvent`s. Deliveries are written to an inbox table
  first and processed after, so a slow handler never makes a host time out and retry.
- **Outgoing webhooks**: subscriptions receive signed events, retried with backoff and
  dead-lettered, and can be replayed from a screen.
- **Mail**: a transport chosen by configuration — SMTP, a directory, or nowhere — so a copy
  that must not email real people cannot by default.
- **Files**: attachments and CVs on disk through store interfaces, on volumes in a container.

Slack, Teams, calendars, cloud providers and payment providers (section 51) are not built.

## Event architecture

Entities raise domain events as they change — about 150 kinds. They are collected when the
transaction saves and written to an **outbox** table in the same transaction, so an event
exists if and only if the change it describes does. A background dispatcher claims batches
with a lease, hands each event to its handlers, retries failures with backoff, and sets aside
after eight attempts an event it cannot deliver, where a screen can put it back. Delivery is
at least once; handlers are written to be safe run twice.

Handlers are how modules react to each other without depending on each other: a departure
releases the leaver's open work; a merged pull request moves the work it names to review.
Those are code. A rule a person writes on the rules page is data, matched and carried out
by two handlers on the same outbox, so it is retried and set aside exactly as they are; see
[automation.md](automation.md).

## Background jobs

Inside the web process, as hosted services — there is no separate worker:

- the **outbox dispatcher**, the **webhook processor** and the **outbound sender**, each a
  queue in the database claimed with a lease;
- the **scheduler**, running ten recurring jobs: lapsing qualifications, expiring
  contracts, expiring agreements, expiring certificates and domains, recurring expenses,
  applying the retention periods, pruning old job runs and failed sign-ins, and for the
  automation rules, starting delayed runs and announcing overdue invoices. It schedules from each job's last recorded run,
  so a deploy does not rerun everything.

Every run is recorded and shown on `/settings/machinery`, where a job can be run now. The
scheduler takes no lock, so one web container should run; see
[deployment.md](deployment.md).

## AI architecture

Sections 36 and 37, and section 7's recruiter aids. Full detail — what is sent, who may use it,
what is not built — in [ai.md](ai.md).

```
Application/Ai     IAiModel, Asker           the interface and who is asking; no HTTP, no storage
Infrastructure/Ai  AnthropicModel            the Messages API over IHttpClientFactory
                   AssistantTools            the lookups, each made as the asker
                   Assistant                 the ask / look up / answer loop
                   ProjectFacts              a project's records and calculated figures, as the asker
                   ProjectReading            a model's reading laid over those facts
                   RecruitingAid, CvReader   CV summary, interview questions, letter drafts
                   AiLedger, AiExchange      the usage log and the daily limit counted from it
Web/Components/Pages/Ai                      four statically rendered pages
```

The three constraints written here before anything was built held, and each is now a mechanism:

1. **It answers through the same permission checks as the pages** (section 36). The model cannot
   read the database; it asks for lookups, and each lookup checks the permission and reach the page
   for the same records checks, then calls the same query. A refused lookup returns a refusal, so
   the records never reach the model. Who is asking comes from the signed-in principal, never from
   the model. Tests assert on what was *sent*, through a fake `IAiModel`: no test calls the provider.
2. **It separates fact, calculation and inference** (section 37) by construction. Facts come from
   `ProjectFacts`; figures from code, each with how it was counted; the model's reading comes back
   in a fixed JSON shape and is drawn in its own labelled box. The first two need no model at all.
3. **It proposes and a person decides.** The assistant changes nothing: a task it suggests is
   created only when the person presses the button. The recruitment aids have no field for a
   recommendation, score or rank, and send nothing to a candidate (section 7).

Off by default: with no `Ai:ApiKey` every AI page says it is not configured and nothing is sent.
A per-person daily limit, a round limit on lookups, one deadline across retries, and retries for
rate limits and overloads bound the cost and the wait. Each use is logged, without its answer.

Not built: conversations, streaming, embeddings or semantic search, and most of section 36's
actions beyond looking things up — see [ai.md](ai.md).

## Deployment architecture

Docker Compose on one host: the application, PostgreSQL, Redis, and optionally Caddy in front
terminating TLS with a certificate it obtains and renews itself. The image build runs the test
suite, so an image whose tests fail is never produced, and CI builds that image on every push.
Production and staging write JSON logs;
`/health` and `/ready` answer different questions. Full detail in
[deployment.md](deployment.md) and every setting in [configuration.md](configuration.md).

Backups (section 88) are a `backup` service in the same compose file, on the database's own
image so `pg_dump` always matches the server: a nightly dump and archives of the attachments
and CVs, with the key ring written to a separate destination, verified by checksum before
they count, and restored by the suite against PostgreSQL on every push. There is no replica
and no write-ahead-log archive, so recovery is to the last nightly backup — up to a day of
work, which [disaster-recovery.md](disaster-recovery.md) states along with the procedure for
each kind of loss. [backups.md](backups.md) has the schedule and the restore.

## Testing strategy

About 1,400 tests, run by `dotnet test` with nothing else installed, inside the image build,
and in CI on every push. The full account — what runs where, and the guard tests by name —
is [testing.md](testing.md).

- **Domain and service tests** over an in-memory SQLite database, for rules and workflows.
- **Page tests** through `WebApplicationFactory`, running the real application in the Testing
  environment — the production pipeline, not Development's — signing in and **posting the
  real forms**, then asserting on the stored rows rather than on what the page said. Several
  faults here reported success on screen while saving nothing, so the screen is exactly what
  cannot be trusted.
- **End-to-end workflows**: the brief's delivery, recruitment and finance chains, each walked
  in one test as the people who do each step, through the pages and the signed webhook
  endpoint.
- **PostgreSQL**: those workflows and a sweep that opens every page run again against a real
  server when `TEST_POSTGRES` is set, with the migrations applied as production applies them.
  CI provides the server. SQLite hid two production faults that this found.
- **Guard tests** that read the source or the compiled assemblies and fail the build for a
  shape that has gone wrong before: a permission checked nowhere, a CSS class that does not
  exist, a per-row form that cannot bind, seeding that overwrites a post, a component out of
  scope, a variable missing from the documentation, a layer depending outwards, a form control
  with no name.
- **Scale**, measured separately with `tools/JiranisokoTech.ScaleCheck` against PostgreSQL.

Gaps: no test drives a real browser, so what only a browser can show — layout at a given
width, colour contrast, keyboard order — is still checked by hand.
