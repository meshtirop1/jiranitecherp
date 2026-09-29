# Testing

How the suite is run, what it covers, and the handful of rules that decide whether a test is
worth having. The short version is in [architecture](architecture.md#testing-strategy).

## Running it

```bash
dotnet build JiranisokoTech.slnx --no-incremental   # must end with 0 warnings
dotnet test JiranisokoTech.slnx                     # must be fully green
```

Nothing else is needed: the suite runs on SQLite in memory. `--no-incremental` matters — an
incremental build reports only what it recompiled, and several commits here once claimed "0
warnings" over a clean build that had four.

### Against PostgreSQL

Production runs PostgreSQL, and SQLite has hidden two faults that PostgreSQL does not forgive
(below). The tests marked `[PostgresFact]` run against a real server when `TEST_POSTGRES` is
set to a connection string for a user that may create databases:

```bash
TEST_POSTGRES="Host=localhost;Username=erp;Password=erp;Database=postgres" dotnet test JiranisokoTech.slnx
```

Each such test creates a database of its own, applies the migrations exactly as production
startup does, and drops it afterwards. Without `TEST_POSTGRES` they are **skipped and say
so** — the count at the end of a run shows how many did not run, where a test that returned
early would have reported green having checked nothing.

### Where it runs

| Where | What runs |
|---|---|
| A developer's machine | Everything on SQLite; the PostgreSQL tests too if `TEST_POSTGRES` is set. |
| The image build | Everything except the PostgreSQL tests and the tests of repository files — see below. An image whose tests fail is never produced. |
| CI (`.github/workflows/tests.yml`) | Everything, with a PostgreSQL service, on every push; and a second job that builds the image. |

The image build is given the source without `docker/` and `docs/` (`.dockerignore` keeps the
build cache from being thrown away by a document edit). A test that reads compose, the
Caddyfile or a document is `[RepositoryFact]`, which skips with its reason where those files
are absent. Before that attribute existed, nine such tests broke the image build while every
checkout run stayed green — and `compose up --build` went on serving the previous image.

## What is tested, and how

**Rules and services** over an in-memory SQLite database: the domain's invariants, each
service's refusals, the outbox and the webhook inbox with their retries and dead letters.

**Pages** through `WebApplicationFactory`, running the real application in the Testing
environment — production's pipeline, not Development's. Tests sign in and **post the real
forms**, then assert on the stored rows rather than on what the page said, because several
faults here reported success on screen while saving nothing. `HtmlForm.Fill` reads a page's
inputs and antiforgery token the way a browser would; it does not read selects or text areas,
so a test sets those itself, as the browser would post them.

**End-to-end workflows** (`tests/.../Workflows`), the three the brief names in section 46, each
walked in one test as the people who would do each step:

- *Delivery* — the owner names the firm, HR invites a developer who sets a password from the
  link, a delivery manager starts a project and raises the work, an engineering manager
  connects the repository, and GitHub reports the push, the pull request, the merge and the
  deployment at the signed webhook endpoint. The merge reaches the board through the outbox.
- *Recruitment* — a head raises a requisition, it is approved up the reporting line on the
  approvals page, HR advertises it, a candidate with no account applies and later accepts on
  the offer link, another is rejected with a reason, and the hire ends in a staff record and
  onboarding.
- *Finance* — sales takes a client on, the accountant drafts and records payments, the finance
  manager sends; each is shown not to be offered the other's button.

Four hiring steps sit on interactive screens that a posted form cannot press — sending a
requisition for approval, marking an interview held and scoring it, writing and sending the
offer, and making the staff record — and those call the service the screen calls. The two
background loops are slowed to an hour in these tests and drained by hand, because a test
that races a hosted worker for the same rows passes or fails on timing.

**Every page** with a fixed address is opened by `EveryPageOpensTests`, as the owner, on both
databases, and every visible form control on it must have a name a screen reader can
announce.

**Scale** is measured separately: `tools/JiranisokoTech.ScaleCheck` times every read path
against PostgreSQL holding a million rows. See [performance](performance.md).

## Guard tests

Tests that read the source or the compiled assemblies and fail the build for a shape that has
gone wrong here before. Each exists because the fault it names reached this codebase at least
once; the test's own remarks say how.

| Test | Fails the build for |
|---|---|
| `EnforcementTests` | a permission granted to a role and checked nowhere |
| `PermissionTests` | a role holding both halves of a separated act, an auditor holding a write, a role missing from the brief's list |
| `ReachabilityTests` | a service method nothing calls |
| `StylesheetTests` | a CSS class used and not defined |
| `SecurityHeaderTests` | an inline event handler, or a header missing |
| `PerRowFormTests` | a per-row form with fields whose model is bound by name |
| `FirstLookTests` | a static page reseeding its form on a post |
| `RenderedBeforeLoadedTests` | a page property promised never-null that a lifecycle method loads |
| `ComponentsInScopeTests` | a component tag Razor cannot resolve |
| `EmailAttributeTests` | `[EmailAddress]`, which makes an optional field required |
| `LayoutTests` | the layout sharing the page's database context |
| `SlugFallbackTests` | an optional code falling back to the name only when null |
| `LayeringTests` | a layer depending outwards |
| `RelationalPatternTests` | the Razor pattern that broke the SDK's build |
| `RetentionTests` | a bulk delete from the audit trail outside the retention sweep |
| `ConfigurationTests`, `DeploymentTests`, `DocumentationTests` | a variable compose reads that is undocumented, a proxy misconfigured, a broken link |
| `DeploymentTests` | and a project in the solution with no COPY line in the image, which fails `restore` inside the image only while every local build stays green |
| `SelfServiceTests` | a role that works here and cannot do something everybody does |
| `AttachmentKindTests` | a kind of attachment one of the three switches over it has no answer for |
| `NoticeWordingTests`, `SearchHeadingTests` | a kind of notice or search result the page has no words for |
| `ChatDestinationTests` | a kind of outbound destination nothing is registered to send to |
| `DemoDataTests` | demonstration data that has stopped being marked, or a history written by a clock that did not travel |

## What SQLite hides

Two faults passed every SQLite test and failed on PostgreSQL:

- **A page that renders before its data.** SQLite's queries finish before an `await` yields, so
  the component never draws with its data still missing. On PostgreSQL the security centre did,
  and was an error screen. `RenderedBeforeLoadedTests` now refuses the pattern, and the
  PostgreSQL page sweep fails on the original fault when it is put back while the SQLite sweep
  passes.
- **A timestamp that is not UTC.** GitHub sends a commit's time in its author's offset,
  "+03:00" in Nairobi. SQLite's mapping converted it to UTC on the way to text; Npgsql refuses
  it. The context now writes every timestamp in UTC on PostgreSQL too.

## The rule every regression test follows

When a fault is one a test could have caught, the test is written, and then **the fix is
broken on purpose to watch the test fail**. A regression test that has never been seen to fail
is a guess. Every test added in the work described in the checklist was checked that way, and
where the first break turned the whole class red rather than the one test, a narrower break
was made until only the test for that fault failed.
