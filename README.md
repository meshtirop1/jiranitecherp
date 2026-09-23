# Jiranisoko Tech — delivery system

A developer-native ERP for **Jiranisoko Tech Solutions**: engineering activity,
business operations, HR, recruitment, finance, clients, infrastructure and
automation as one connected system, rather than a dozen screens over a database.

Will answer at **erp.jiranisokotech.co.ke**.

**.NET 10 · ASP.NET Core 10 · Blazor Web App · EF Core 10 · PostgreSQL · Docker**

---

## State: foundation

Early. What is here is built and tested; nothing below is a placeholder, and
nothing claims to work that does not.

| | |
| --- | --- |
| ✅ | Solution layout, dependencies pointing inward |
| ✅ | `Money` — integer minor units, currency-safe, allocation that balances |
| ✅ | `Entity` and domain events, collected rather than dispatched mid-transaction |
| ✅ | `IClock`, so date rules are testable on a day that is not today |
| ✅ | Audit trail — append-only, written in the same transaction as the change |
| ✅ | Roles and permissions, declared once in code and synced on every start |
| ✅ | Sign in, sign out, lockout, deactivation, and a record of every attempt |
| ✅ | Outbox dispatcher — at-least-once, backoff, claims, dead-lettering, sweeping |
| ✅ | `/health` and `/ready`, answering different questions |
| ✅ | Dockerfile and Compose — built, run, and four faults fixed that only running found |
| ✅ | Migrations, applied on start, with a test that catches a model change without one |
| ✅ | People — departments, employees, reporting lines that cannot form a loop |
| ✅ | People screens — roster, person, departments, organisation chart |
| ✅ | Work — projects, work items, a state machine that will not be talked round |
| ✅ | Work screens — board, card, projects; an engineer sees their own work |
| ✅ | Modules joined by events: a departure releases the leaver open work |
| ✅ | A home page that is what is in front of you, not a wall of tiles |
| ✅ | Approvals — chains that cannot reach the stuck state the old one did |
| ✅ | Accounts — invitations, roles, withdrawal, linking to a staff record |
| ✅ | Email — invitations and approval notices; File by default, so nothing surprises anybody |
| ✅ | Two-step sign-in — authenticator codes, recovery codes, a way back in |
| ✅ | Recruitment — requisitions that must be approved before anything is advertised |
| ✅ | Careers pages — public adverts, applications, CV upload, rate limited |
| ✅ | Interviews — panels, scorecards, and one strong no that carries the panel |
| ✅ | Clients — one code per client, and no archiving one with work still running |
| ✅ | Timesheets — a day that cannot hold more hours than it has; nobody approves their own |
| ✅ | Leave — overlaps refused, weekends not counted, only sickness datable in the past |
| ✅ | Expenses — claims, approval, payment as a separate state from approval |
| ✅ | Invoices — numbered in sequence, totals summed not stored, approved hours billed once |
| ✅ | Screens for all six — logging a day, asking for leave, claiming, approving, billing |
| ✅ | Every class a page uses is a class some stylesheet defines, and a test says so |
| ✅ | Where things stand — the figures the firm is run on, each with somewhere to go |
| ✅ | Settings — the firm's own details, on its invoices, where a tax invoice needs them |
| ✅ | Search — one box over six tables, each group behind the permission its page checks |
| ✅ | Documents — contracts, orders and receipts, behind the permission of what they hang off |
| ✅ | A read API at /api/v1, on keys with scopes, rate limited per caller |
| ✅ | Applications and interviews — the screens the domain had been waiting for |
| ✅ | Every permission is enforced by something, and a test that keeps it that way |
| ✅ | The trail, readable — it had been recorded since week one and shown nowhere |
| ✅ | Clients and projects open onto their own page, where their documents live |
| ✅ | 522 tests |
| ☐ | Requiring two-step sign-in of anybody. It is offered, not compulsory |
| ☐ | Writing through the API, and the integrations that would need it |

**Docker has been run against this**, and the first person was right to expect
to fix something. Four faults were found by running it that nothing on a
developer's machine could have shown:

- The Postgres 18 images refuse to start when the volume is mounted at
  `/var/lib/postgresql/data`; 18 and later want `/var/lib/postgresql`.
- Restoring with only the `.csproj` files present — the usual layer-caching
  trick — happens before any `.razor` file exists, so the SDK never adds the
  implicit package carrying `blazor.web.js`, and `--no-restore` locks that in
  for the rest of the build. Every page then asked for a script that answered
  with a redirect to the sign-in page. Nothing is interactive yet, so nothing
  visibly broke; the first component to ask for `@rendermode` would simply not
  have worked, in the container only.
- The keys that sign the authentication cookie were written inside the
  container, so every deploy would have signed the whole firm out and
  invalidated any password-reset link already sent.
- And the fix for that one was itself wrong until it was run: a named volume
  takes its ownership from the image, so `/keys` has to exist and belong to the
  non-root user before that user takes over. Until it did, the sign-in page
  returned 500 from a directory permission.

What is verified: the image builds, the tests run and pass inside it, all
three containers report healthy, every migration applies to a real Postgres,
the health and readiness endpoints answer, the public pages render with their
stylesheets, and the signing keys survive a restart.

Not yet done: nothing is deployed to a host, so `erp.jiranisokotech.co.ke` does
not answer. The `cache` container runs Redis and the application does not use
it — it is provisioned ahead of the background-job work rather than because
anything reads from it today.

---

## Running it

Needs the .NET 10 SDK. Nothing else — the test suite does not require Docker.

```bash
dotnet test                              # 522 tests
dotnet run --project src/JiranisokoTech.Web
```

An empty database has no accounts, and there is no self-registration — so set
these before the first run or the sign-in page has nothing to let you past:

```bash
export Bootstrap__OwnerEmail=you@jiranisokotech.co.ke
export Bootstrap__OwnerPassword=at-least-twelve-characters
```

They are read once, on a database with no owner, and ignored ever after. Change
the password from inside the application and clear them.

To see it as it will actually be served, publish first: a debug run cannot serve
the framework assets from the project folder, and the pages come out unstyled.

```bash
dotnet publish src/JiranisokoTech.Web -c Release -o /tmp/erp
cd /tmp/erp && dotnet JiranisokoTech.Web.dll --urls http://localhost:5189
```

With Docker, once you have one:

```bash
cp .env.example .env                     # then set POSTGRES_PASSWORD
docker compose -f docker/compose.yaml up --build
```

Compose refuses to start without a database password rather than defaulting to
something guessable.

---

## Layout

```
src/
  JiranisokoTech.Domain          entities, value objects, events — no dependencies
  JiranisokoTech.Application     services, use cases, the interfaces they need
  JiranisokoTech.Infrastructure  EF Core, providers, the outside world
  JiranisokoTech.Web             Blazor UI and the HTTP API
tests/
  JiranisokoTech.Tests           xUnit, driving the real application
docker/
  Dockerfile  compose.yaml
```

Dependencies point inward only: `Web → Infrastructure → Application → Domain`.
The domain knows nothing about databases, HTTP or the framework, which is what
keeps the rules testable without any of them running.

---

## Two rules worth knowing before reading the code

**A service is the only way in.** HTTP, the API, the CLI and webhook handlers
all call the same service, and none of them contains a rule. That is what makes
an API and a CLI cheap rather than a second implementation of the business.

**Events are facts, in the past tense, carrying ids and not entities.** They are
collected on the entity and published after the transaction commits — a rule
that fires mid-transaction eventually fires for something that then fails to
save, and an email about a hire that never happened cannot be unsent.

---

## Where this came from

There is a working Laravel implementation of much of this at `../jiranisoko-tech`,
in production and in daily use. This is a separate, deliberate rebuild on .NET;
the older system keeps running until this one earns the traffic.

Its architecture notes — the phase plan, what the approval engine already does,
and the hosting constraint — are worth reading first:
`../jiranisoko-tech/docs/erp-architecture.md`.
