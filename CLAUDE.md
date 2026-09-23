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
- **Page tests in one class share one database** through the class fixture, so
  "nothing has been recorded yet" is true only for whichever test runs first.
  Assert against a state no other test in the class produces.

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
