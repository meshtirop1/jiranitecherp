# Watching the code repositories

The brief's first principle is that developers should spend as little time as
possible entering information into an ERP, and that the system should collect it
for itself. This is the part that does that: an engineer names a branch after the
work they are doing, pushes it, opens a pull request and merges it, and the board
fills itself in. Nobody writes a status update.

It covers GitHub. GitLab, Bitbucket and Azure DevOps each need one adapter —
`IGitProvider` — and nothing else.

## Setting it up

**1. Generate a secret and give it to the application.**

```bash
openssl rand -hex 32
```

In `docker/.env`:

```
GITHUB_WEBHOOK_SECRET=the-value-you-just-generated
```

In development, the secret manager rather than `appsettings.json`:

```bash
dotnet user-secrets set "Git:Providers:GitHub:Secret" "the-value-you-just-generated"
```

Left unset, the application still starts. Every delivery is answered with 503,
which tells GitHub to try again later rather than to give up — so setting the
secret afterwards does not cost the deliveries that arrived in between.

**2. Add the webhook at GitHub.** In the repository, Settings → Webhooks → Add
webhook:

| Field | Value |
| --- | --- |
| Payload URL | `https://erp.jiranisokotech.co.ke/webhooks/github` |
| Content type | `application/json` |
| Secret | the same value |
| Events | Pushes, Pull requests, Pull request reviews |

Sending every event instead of those three is harmless. Anything this system has
no use for is recorded, marked *Nothing for us*, and left alone.

**3. Connect the repository here.** On **Repositories**, enter the owner, the
name, the project it belongs to, and the same secret again.

That third entry of the secret is checked against the application's own
configuration and then thrown away — only a hash of it is kept. The check exists
because a repository connected with a secret the application does not hold would
be connected and permanently deaf, and that failure looks exactly like GitHub
having stopped sending.

## Reading the repositories screen

| What it says | What it means |
| --- | --- |
| **Watched** | Connected, and the stored secret matches configuration. |
| **Secret differs** | Somebody rotated the secret at one end and not the other. Every delivery is being refused. Disconnect and connect again. |
| **Nothing yet** | Connected, and nothing has ever arrived. Check the webhook at GitHub — its own Recent Deliveries tab says what happened. |
| **Last heard from** | The single most useful field. "Connected" and "last heard from three weeks ago" are different states, and only the second one tells anybody to go and look. |
| **n failed** | Deliveries that gave up. Until they are replayed, what the board says was built is incomplete. |

## How a commit finds its work

Work items are numbered, and the number is shown on the work item page as, for
example, `#412`. Any of these attaches a commit or a pull request to it:

```
feature/412-payment-api      the common convention
412-payment-api              the same without a prefix
bugfix/#412                  being explicit
Fixes #412                   in a commit message
#412 add the retry           in a pull request title
```

And none of these do, deliberately, because each would attach work to whatever
task happened to hold that number and would be believed:

```
v1.412.0                     a version
release/2026-412             a date or a build
fix-412-errors               ambiguous: 412 could be an error code
PR-412                       GitHub's number, not ours
```

The bias is towards finding nothing. A commit with no work attached is visible
and somebody can attach it; a commit attached to the wrong work is invisible and
stays wrong, and it puts evidence of effort onto something nobody did.

## What happens on a merge

Work that is **in progress** and whose pull request merges moves to **in
review**, with the reason recorded. It stops there. Merging means the code is in;
it does not mean anybody has agreed the work was right, and it certainly does not
mean it has been released. Accepting it is still `tasks.review` and releasing it
is still `tasks.deploy`, and both are still a person's decision.

Work that is already in review, done, released or cancelled is left alone, so a
late or replayed delivery cannot drag accepted work backwards.

## When a delivery fails

Every delivery is written down **before** it is understood, so nothing is ever
lost to a bug in reading it. A delivery that cannot be handled is retried up to
five times and then marked *Gave up*, and the **Deliveries** screen has a button
to try it again once the cause is fixed. The usual sequence is: deliveries fail
on a payload shape nobody anticipated, somebody corrects the adapter, deploys,
and replays what failed — and the history fills itself in rather than keeping a
hole for however long the fault lasted.

A review that arrives before its own pull request fails once and lands on a later
pass. That is ordinary: GitHub does not promise order.

## What the endpoint answers, and why it matters

| Code | When |
| --- | --- |
| 200 | Written down. Not necessarily understood yet — that is deliberate, because GitHub allows ten seconds and then records a failure. |
| 401 | The signature did not match. Nothing was recorded. |
| 400 | Signed, but missing the delivery identifier or the event name. |
| 413 | Larger than 4 MB. |
| 503 | No secret is configured here. |

The distinction between 4xx and 5xx is the one that matters: GitHub retries a 5xx
and gives up permanently on a 4xx. That is why a missing secret answers 503, and
why every refusal from this endpoint carries a body — an empty one gets replayed
through the status-code-pages middleware and arrives as 400. See `CLAUDE.md`.

## Why only a hash of the secret is stored

For the same reason an API key stores only a hash: a copy of the database should
not be a set of working credentials. The consequence is deliberate — a signature
cannot be verified from a hash, so the secret lives in configuration and the
stored hash exists only to prove the configured value is the one the repository
was connected with. That is the tripwire behind **Secret differs**.

The secrets are per provider, not per repository. Per-repository secrets would
mean parsing the payload to discover which secret to verify it with, and parsing
an unverified payload to decide how to verify it is the exact inversion this
endpoint exists to avoid.

## Who may do what

| Permission | What it allows |
| --- | --- |
| `repos.view` | See which repositories are watched, and what came out of them. Held by engineers, delivery managers and heads of department. |
| `repos.manage` | Connect a repository, move it to a project, disconnect it. |
| `repos.deliveries` | Read the delivery log and replay what failed. The narrowest of the three, because a delivery body carries branch names, commit messages and the shape of a private codebase's history. |

## Deleting old deliveries reopens a hole

The unique index on GitHub's delivery identifier is what refuses a second copy of
a delivery — and GitHub sends no timestamp to check freshness against, so that
index is the **only** replay protection there is. It protects exactly the rows
still in the table. A retention sweep over `webhook_deliveries` would make a
captured request postable again, and should not be added without replacing the
protection with something else first.
