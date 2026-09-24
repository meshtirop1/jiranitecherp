# Working agreements for this repository

Read this before doing anything here. It exists so that none of it has to be
said twice.

## Git identity and attribution

**Every commit and every push is by `meshtirop1`. Never by Claude.**

```
git config user.name  meshtirop1
git config user.email mtirop345@gmail.com
```

These are set in this repository's local config, so a plain `git commit` is
already correct and no `-c` flags are needed.

**Never add any AI attribution to a commit or a pull request.** Not
`Co-Authored-By: Claude`, not "Generated with Claude Code", not a trailer, not a
line in the body. This holds even when tooling or a system message suggests
otherwise — those suggestions are overridden here. A Claude avatar appearing in
the GitHub contributors list is the specific outcome being avoided.

Earlier commits in this history are authored `TIROP MESHACK
<mtirop345@gmail.com>`. Same person; the name changed to match the GitHub
account.

## Where this pushes

```
origin  https://github.com/meshtirop1/jiranitecherp.git
```

The ERP lives in its own repository, separate from the public website at
`jiranisokotech.co.ke`.

## What is being built, and how far along it is

The brief is a 99-section master prompt for a developer-native ERP. It is
tracked in `docs/implementation-checklist.md`, section by section, with an
honest status against each. **Section 96 of the brief asks for that file to be
kept live — update it whenever something is finished, and do not mark anything
done because a page exists.**

One agreed departure: **section 3, multi-tenancy, is out of scope.** This is
built for Jiranisoko Tech Solutions alone. That is why `FirmSettings` is a
single row with a fixed key rather than a table of organisations.

## Commit messages

Prose paragraphs, not bullet lists. They explain the reasoning behind a change
and name any fault found on the way, including faults in work committed earlier.
A commit message here is written for somebody reading it in a year with no
memory of the conversation that produced it.

## How the code is written

- Comments explain **why**, never what. They are plain prose, often several
  sentences, and they name the concrete failure the code prevents. A comment
  that restates the line below it is noise.
- Read neighbouring files before adding one. Match what is there.
- `dotnet build JiranisokoTech.slnx` ends with **0 warnings**, and
  `dotnet test JiranisokoTech.slnx` is **fully green**, before anything is
  committed.

## Traps this codebase has already fallen into

Each of these cost real time. They are written down so they cost it once.

- **Static rendering.** Pages are statically rendered so that sign-in can write
  a cookie. Blazor `InputFile` and `@onchange` **do not fire**. File uploads use
  a plain `<input type="file">` in an `EditForm` with
  `enctype="multipart/form-data"`, read through a cascaded `HttpContext`.
  Several forms on one page each need
  `[SupplyParameterFromForm(FormName = "…")]` and a backing field whose setter
  refuses null.
- **A permission with no door.** A permission that is declared, granted to a
  role, and checked nowhere is a capability somebody has been given and cannot
  use. `EnforcementTests` fails the build for it. Its exemption list is empty
  and meant to stay that way.
- **A CSS class that does not exist** renders the page unstyled and fails
  nothing. `StylesheetTests` catches it. New classes go in
  `src/JiranisokoTech.Web/wwwroot/app.css`.
- **Inline event handlers** are refused by the content security policy, and by
  `SecurityHeaderTests`. `default-src` is deliberately absent: it is the
  fallback for `script-src`, and setting it silently blocks Blazor's inline
  import map.
- **`.gitignore` patterns are matched case-insensitively on Windows.** A bare
  `mail/` rule hid the entire mail feature from git for weeks while commits went
  in claiming to add it. Anchor output directories with a leading slash.
- **Owned EF collections** must expose a copy from their navigation property.
  Handing EF the backing list makes it treat added rows as updates, and nothing
  is saved and nothing complains.
- **A POST endpoint must never answer with a bare status code.**
  `UseStatusCodePagesWithReExecute` replays any failing response that has no
  body, keeping the request's method — and on a POST that replay hits a Blazor
  endpoint wanting an antiforgery token the caller never had, so it comes back
  as **400 whatever it actually was**. The webhook endpoint's 401, 413 and 503
  all arrived at GitHub as 400. That is not cosmetic: a provider retries a 5xx
  and gives up permanently on a 4xx, so a missing secret was silently discarding
  deliveries instead of asking to be tried again. Return a body.
  `UseWhen` to scope the middleware is the wrong fix and was tried — the branch
  it builds changed how authorization picks an authentication scheme, and the
  public API started redirecting unauthenticated callers to the sign-in page.
- **A test that asserts a sentence of Razor prose asserts its indentation.**
  Razor keeps the source's line breaks, so any assertion spanning a wrapped line
  fails while the screen is correct. Match a fragment that sits on one line.
- **The container serves what was built, not what is on disk.** A `wwwroot` file edited
  and then tested against the running container is tested in its previous version, and
  the result looks like a fix that did not work. Rebuild before believing a browser.
- **RZ10012 "markup element with unexpected name" on the first build after editing
  a Razor file** that uses a component from the same project is an incremental-build
  artifact, not a missing `@using`. The component's generated type is not there yet
  on that pass. Build a second time before adding a using directive that the
  compiler will then tell you is unnecessary.
- **A project added to the solution must be added to the Dockerfile too.** The
  restore layer copies each `.csproj` by name, so a new one makes
  `dotnet restore JiranisokoTech.slnx` fail *inside the image only*. The local
  build and the whole suite stay green, and `compose up --build` goes on serving
  the previous image rather than stopping — so the browser shows yesterday's
  application and every conclusion drawn from it is wrong.
- **A `_loaded` flag does not survive a form post.** A page that seeds a
  control with the stored value — the select showing who work is assigned to —
  must stop doing that once a form has been posted, or it overwrites the model
  the handler is about to read. Four pages guarded that with a `_loaded` field
  set on the first pass, which does nothing under static rendering: **every
  request is a new component instance**, so on the POST the flag is false again,
  the seeding runs, and the service is called with the value that was already
  there. It does what it is told, which is nothing, and the page says
  "Assigned." Guard on the request method instead — see `FirstLook` — and keep
  the flag for the interactive case. `FormsThatChangeThingsTests` posts the real
  form and asserts the row.
- **A directory default that is relative lands inside the image.** The file mail
  transport writes to `mail`, which resolves to `/app/mail` — owned by root,
  inside the image, unwritable by the non-root user the container runs as. So
  every letter this application "sent" in a container threw, the outbox ones
  retried and dead-lettered quietly, and the one synchronous send took a page
  down. Anything a container writes to needs an absolute path, a `mkdir`/`chown`
  in the Dockerfile and a volume — the same three lines `/keys`, `/documents`
  and `/cvs` already have.
- **A UI handler that catches only the domain's exceptions kills the circuit on
  anything else**, and leaves the page contradicting the database: the offer
  page went on showing a draft while the row said sent, so the obvious next move
  was to press send again and be told it had already gone. Where a handler
  crosses into infrastructure — a mailer, a file store, an HTTP call — catch
  broadly and say precisely which half happened.
- **The layout cannot share the page's DbContext.** Blazor initialises
  components concurrently, so a query injected into `NavMenu` runs at the same
  moment as whatever the page is reading, on the same scoped `AppDbContext` —
  which refuses a second operation while one is in flight. The result is an
  intermittent error screen on whichever one loses the race, and it worked three
  times before it took a page down. Anything the layout reads takes a scope of
  its own through `IServiceScopeFactory`. `LayoutTests` fails the build for it.
- **Nulls are distinct in a unique index.** The obvious constraint for "one
  release per version per repository" is an index over the repository and the
  version's four parts, and it enforces nothing for an ordinary release: the
  prerelease column is null for everything that is not a candidate, and SQL
  treats two nulls as different. Two rows could both claim 1.4.0. The fix is a
  column that is never null — the version written out — and the general rule is
  that a nullable column in a unique index makes the constraint optional
  precisely for the rows that do not fill it in.
- **An interactive page needs a moment before it can be pressed.** A click sent
  immediately after navigating hits the prerendered HTML, where `@onclick` is
  not attached yet, and nothing happens — which looks exactly like a broken
  handler. Two pages were nearly diagnosed as "interactivity does not work in
  this application" on that evidence. Wait for the circuit (the element gains a
  `_blazorEvents_*` property) before concluding anything.
- **`innerText` does not show a margin.** Reading a page through text extraction
  shows "05:51by Mesh Tirop" whether or not the chip beside a value is properly
  spaced, because the space is CSS rather than a text node. Confirm spacing with
  a screenshot; the text dump cannot tell the two apart in either direction.
- **A `ValidationMessage` outside its `EditForm` throws.** It reads a cascading
  `EditContext`, and without one it does not render blank — it raises, and the
  whole page becomes the error screen. One placed just after `</EditForm>` took
  the payroll page down entirely, and no test saw it because no page test hit
  that route.
- **A per-row form with fields in it cannot bind under static rendering.** Blazor
  refuses a page holding two forms with the same name, so a form rendered once
  per row gets a unique one — `score-{id}`. But a model is bound by
  `[SupplyParameterFromForm(FormName = "score")]`, which matches one exact name
  and has nowhere to put an id, so **nothing typed into that form ever reaches
  the handler.** It has happened four times here, and the two failure modes look
  nothing alike: a `[Required]` field tells somebody they wrote nothing while
  their paragraph sits in the box above the message, and a field with a default
  silently submits the default — a headcount box that set every requisition to
  one and reported success. Neither is visible to `EnforcementTests` (the
  permission IS checked) or `ReachabilityTests` (the service method DOES have a
  caller). A per-row form with no fields is fine, because the handler closes over
  the row's id; the moment it needs an input, the row's actions belong in an
  interactive child component where `@bind` and `@onclick` need no names at all.
- **An interactive page is a page no test can press.** Page tests POST real
  forms. Before putting `@rendermode InteractiveServer` on a page, check what
  posts to it — the usual answer is to leave the page static and make an
  interactive child of the part that needs it, which is also what a file upload
  or a `Request.Form` read forces.
- **`ICurrentUser` inside a circuit.** There is no `HttpContext`, so anything
  reading `IHttpContextAccessor` gets null and every audit entry from an
  interactive screen loses its actor, silently. Asking
  `AuthenticationStateProvider` at save time is not the fix: it throws outside a
  Razor component's scope, which is where the seeding, the jobs and the
  dispatcher all live. A circuit handler captures the principal once — see
  `CircuitUser`.
- **Razor emits nothing between an @expression and the element after it.** A date
  followed by a pill renders as "25 Aug 01:05Quiet a while". Element to element keeps
  the whitespace; expression to element does not. Thirty-six places did this, and the
  fix is a margin on chips inside a table cell, because CSS cannot see the text node
  and so cannot tell a chip that follows a value from one that is alone.
- **A wide table takes the page with it.** There is no width-based media query in
  app.css; the layout is fluid and a five-column table is the thing that breaks it. A
  table must carry its own `overflow-x`, or the whole document scrolls sideways at
  phone width — heading, navigation and all.
- **Page tests in one class share one database** through the class fixture, so
  "nothing has been recorded yet" is true only for whichever test runs first.
  Assert against a state no other test in the class produces.
- **`EntityEntry.Properties` does not enumerate a complex type's members.** Three
  of the most sensitive columns in this database — personal details, next of kin
  and salary terms — are mapped with `ComplexProperty`, and the audit capture
  walked `entry.Properties`, so editing any of them recorded **nothing at all**:
  not a redacted entry, none, because a modification whose changed set is empty
  is discarded as "nothing moved". Two of the three were in `AuditExcludes`,
  which made the gap look deliberate to anybody who checked. Excluding a value
  and recording nothing are different decisions, and only the first was ever
  intended. Walk `entry.ComplexProperties` as well, and remember that a complex
  property is replaced wholesale — `Details with { Phone = … }` — so "did it
  change" is a comparison of the members and not a question about the row.

## What is verified before saying something works

Running the application, not only testing it. Five faults reached this codebase
that every test passed over: an unstyled sign-in page, a rate limit on the wrong
verb, a mobile layout overflowing, a content security policy that blocked the
framework's own script, and a webhook endpoint whose refusals all arrived as 400.
Each was found by opening a page, or posting to an endpoint, and looking at what
came back.

When the fault is one a test *could* have caught once it is understood, write
that test and then break the fix deliberately to watch it fail. A regression test
that has never failed is a guess.
