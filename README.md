# Jiranisoko Tech — delivery system

A developer-native ERP for **Jiranisoko Tech Solutions**: engineering activity,
business operations, HR, recruitment, finance, clients, infrastructure and
automation as one connected system, rather than a dozen screens over a database.

Will answer at **erp.jiranisokotech.co.ke**.

**.NET 10 · ASP.NET Core 10 · Blazor Web App · EF Core 10 · PostgreSQL · Docker**

---

## Where it stands

The brief is a 99-section master prompt, kept verbatim in
[docs/master-prompt.md](docs/master-prompt.md). What is done against each section,
what is partly done and what is not started is in
[docs/implementation-checklist.md](docs/implementation-checklist.md) — that file,
not this one, is the answer to "is it done?", and it is kept honest rather than
kept flattering.

| Document | What it covers |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | how the system is built, and why |
| [docs/deployment.md](docs/deployment.md) | putting it on a host, HTTPS, updating, health |
| [docs/backups.md](docs/backups.md) | what is backed up, scheduling it, verifying it, restoring it |
| [docs/disaster-recovery.md](docs/disaster-recovery.md) | what to do when the host, the database or the keys are lost |
| [docs/configuration.md](docs/configuration.md) | every setting and environment variable |
| [docs/git-integration.md](docs/git-integration.md) | connecting code hosts |
| [docs/security.md](docs/security.md) | signing in, roles and permissions, the security centre, secrets |
| [docs/testing.md](docs/testing.md) | running the suite, the PostgreSQL run, the workflow and guard tests |
| [docs/automation.md](docs/automation.md) | the WHEN / IF / THEN rules, the ones shipped, and their limits |
| [docs/performance.md](docs/performance.md) | what was measured at volume |
| [docs/conformance.md](docs/conformance.md) | the brief's principles, module structure and definition of done, rule by rule |
| [docs/privacy.md](docs/privacy.md) | what is held about people, why, for how long, and their rights |
| [docs/ai.md](docs/ai.md) | the assistant, project readings and recruitment aids: what is sent, who may use them, and what is not built |

---

## Running it

Needs the .NET 10 SDK. Nothing else — the test suite does not require Docker.

```bash
dotnet test
dotnet run --project src/JiranisokoTech.Web
```

The tests marked `[PostgresFact]` also run against a real PostgreSQL when `TEST_POSTGRES`
names one, and CI runs them on every push — see [docs/testing.md](docs/testing.md).

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

With Docker, the `.env` file goes beside the compose file, because that is
where Compose looks for it:

```bash
cp .env.example docker/.env              # then set POSTGRES_PASSWORD
cd docker && docker compose up --build
```

Compose refuses to start without a database password rather than defaulting to
something guessable. For a real host, with HTTPS, see
[docs/deployment.md](docs/deployment.md).

---

## Layout

```
src/
  JiranisokoTech.Domain          entities, value objects, events — no dependencies
  JiranisokoTech.Application     services, use cases, the interfaces they need
  JiranisokoTech.Infrastructure  EF Core, providers, the outside world
  JiranisokoTech.Web             Blazor UI and the HTTP API
tests/
  JiranisokoTech.Tests           xUnit, driving the real application; Workflows/ walks
                                 the brief's three end-to-end chains
tools/
  JiranisokoTech.ScaleCheck      times every read against a filled PostgreSQL
docker/
  Dockerfile  compose.yaml  Caddyfile
.github/workflows/
  tests.yml                      the suite on SQLite and PostgreSQL, and the image build
```

Dependencies point inward only: `Web → Infrastructure → Application → Domain`, which
`LayeringTests` checks against the compiled assemblies.
The domain knows nothing about databases, HTTP or the framework, which is what
keeps the rules testable without any of them running.

---

## Two rules worth knowing before reading the code

**A service is the only way in.** The pages, the API and the webhook handlers
all call the same service, and none of them contains a rule. That is what will
make a CLI cheap rather than a second implementation of the business.

**Events are facts, in the past tense, carrying ids and not entities.** They are
collected on the entity and published after the transaction commits — a rule
that fires mid-transaction eventually fires for something that then fails to
save, and an email about a hire that never happened cannot be unsent.

---

## Where this came from

There is a working Laravel implementation of much of this at `../jiranisoko-tech`,
in production and in daily use. This is a separate, deliberate rebuild on .NET;
the older system keeps running until this one earns the traffic.

Its architecture notes are at `../jiranisoko-tech/docs/erp-architecture.md`.
This system's own are in [docs/architecture.md](docs/architecture.md).
