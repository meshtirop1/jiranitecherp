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

## What is verified before saying something works

Running the application, not only testing it. Four faults reached this codebase
that every test passed over: an unstyled sign-in page, a rate limit on the wrong
verb, a mobile layout overflowing, and a content security policy that blocked
the framework's own script. Each was found by opening a page and looking.
