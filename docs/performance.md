# Performance, measured

Section 77 of the brief asks for performance. The checklist said "indexed, paged
where it matters — untested at scale" for weeks, and that sentence was an
admission rather than a status: every index in this system was added because
somebody reasoned about a query, and reasoning about a query planner is how a
schema ends up with six indexes nothing uses and none on the column that matters.

This is what happened when PostgreSQL was asked instead.

## How to run it

`tools/JiranisokoTech.ScaleCheck` fills a throwaway database with more rows than
the firm will have for years, calls the same query methods the screens call with
the same arguments, and reports for each one how long it took, how many
statements it issued, and what the planner did.

The database in `docker/compose.yaml` publishes no port — deliberately, and that
is not worth changing for this. Reach it with a temporary sidecar on the compose
network:

```bash
docker run -d --rm --name pg-tunnel --network jiranisokotech_default -p 127.0.0.1:15432:5432 alpine/socat tcp-listen:5432,fork,reuseaddr tcp-connect:db:5432
```

Then make a database for it to ruin, and run it:

```bash
docker compose -f docker/compose.yaml --env-file docker/.env exec -T db psql -U jiranisokotech -d postgres -c "CREATE DATABASE jiranisokotech_scale OWNER jiranisokotech;"
```

```bash
SCALECHECK_CONNECTION="Host=127.0.0.1;Port=15432;Database=jiranisokotech_scale;Username=jiranisokotech;Password=<from docker/.env>;Command Timeout=1800" dotnet run --project tools/JiranisokoTech.ScaleCheck
```

It truncates thirteen tables before it starts, and it refuses a database that has
any account in it — the failure being guarded against is a production connection
string pasted into a tool whose first statement is `TRUNCATE`. Pass `--no-seed`
to measure again without refilling. Stop the sidecar with
`docker rm -f pg-tunnel` when finished; it is a route into the database and it
should not outlive the measurement.

It exits non-zero when there is a finding, so it can be a gate later. It is not a
gate now: it needs a database and several minutes, and a check that slow is one
people learn to skip.

## What it measures, and why those three things

**Statements, not only time.** The failure that kills an EF application at volume
is not a slow query, it is a fast one issued four hundred times, and that is
invisible in a timing taken on a laptop with twelve rows in the table. The
signature is the same statement repeated, so that is what it looks for — the
first version counted statements and called anything past four a query per row,
which flagged the dashboard for asking twelve different questions.

**The plan, not only the clock.** A sequential scan over sixty thousand rows is
quick on a warm cache and a machine with nothing else to do. The clock says the
page is fine; the plan says it will not be at four times the size, and four times
is a year or two of a system nobody deletes from.

**Where the time went.** The time inside the database is reported apart from the
time around the call, because the two have opposite fixes. A slow statement wants
an index; a fast statement whose call took a second and a half wants fewer rows,
because that second was spent turning them into objects.

## The volume

| Table | Rows |
| --- | --- |
| `audit_entries` | 600,000 |
| `time_entries` | 250,000 |
| `work_items` | 120,000 |
| `invoice_lines` | 120,000 |
| `invoices` | 40,000 |
| `opportunity_activities` | 32,000 |
| `invoice_payments` | 26,667 |
| `opportunities` | 8,000 |
| `projects` | 6,000 |
| `client_contacts` | 6,000 |
| `clients` | 2,000 |
| `employees` | 400 |

## What it found, and what was done about it

Five faults. Every one of them was invisible to the test suite, which runs
against SQLite with a few dozen rows and asserts about correctness.

**The invoices screen and the public API read every invoice the firm had ever
issued**, with its lines and its payments — 1.8 seconds and forty thousand
aggregates, to show fifty rows. The API's paging was applied *after* the rows had
been read, so the cap `Paging` documents as protecting the firm was protecting
the response and nothing else. The comment at the top of that file, about an
answer that takes twenty seconds and several megabytes, described the code
underneath it. Both now ask for the page in SQL. 1,918ms → **52ms**.

**The work item screen read every work item in the system** and picked its own
out of the list with `FirstOrDefault`. Invisible with forty rows; three quarters
of a second and eighty thousand objects per view of one item at a hundred and
twenty thousand. It now asks for the one. → **91ms**.

**The timesheet approval queue read every unapproved entry there had ever
been** — fifty thousand at this volume, each rendered into a table with a tick
box. It is now capped at two hundred, and says on the screen how many are behind
it. 265ms → **77ms**.

Capping it made the sort order a correctness question rather than a preference.
The query returned newest first, which is right for a timesheet and exactly wrong
for a queue: with a cap, newest-first hides the oldest entries — the ones
somebody is waiting on. The approval queue now reads oldest first.

**The approval queue had no index and was a sequential scan of a quarter of a
million rows.** Unapproved entries are a fifth of the table, so a plain index on
`ApprovedAt` would not have been worth the planner's while. It now has the
schema's only partial index, over `On` where `ApprovedAt IS NULL`: the size of
the queue rather than the size of the history, and it answers the filter and the
order together so nothing is left to sort.

**A property named `Events` on `Subscription` hid `Entity.Events`**, the domain
events waiting for the outbox. Found while reading the build output the scale
check's project forced, after an incremental build had been quietly skipping it.
Nothing was broken by it yet, and the next thing written would have been: this
codebase tests that something was announced with
`Assert.Single(thing.Events.OfType<SomethingHappened>())`, and on that type it
would have compiled against the wrong list and found nothing, for ever, without
failing. Renamed to `Wanted`.

## What is slow on purpose

Three reads take over a second at this volume and are left alone. The harness
names each one with its reason rather than skipping it, because a report nobody
can tell is complete is one where the next person adds the index anyway.

**What a client owes** — 1.2 seconds, of which 990ms is spent building objects.
An invoice's total is the sum of its lines and what is outstanding is that less
its payments; neither is a column, because both are computed by the aggregate,
which is where the rule about them lives. Pushing the arithmetic into SQL would
put a second, separate definition of "what is owed" in the database, and the day
the two disagree is the day somebody chases a client for the wrong amount. The
cost is reading every unpaid invoice with its lines and payments.

This is the one worth revisiting, and revisiting it means changing where that
rule lives rather than adding an index. The honest options are a stored balance
maintained by the aggregate itself in the same transaction, or a materialised
view that is openly a cache. Both are a day's work and a new way to be wrong, and
neither should be done in the same week as anything else.

**Margin per project** — 1.3 seconds. It is computed over every invoice, every
approved hour and every paid claim, which is what margin is. A scan is the right
plan for a figure over every row and no index changes that.

**The firm's state** — 1.7 seconds for twelve firm-wide figures, in twelve
statements. Twelve different questions, not one question asked twelve times.

**The work board's ordering** scans `work_items` and takes the top two hundred.
"Open" means "not one of the domain's finished states", read from the domain's
own list, and a partial index would put a second copy of that list in the
schema — which is the exact fault the query was written to avoid, and the one
that made every copy of a hand-written status pair go on reporting released work
as open when a seventh state arrived. It costs 120ms.

## What this does not measure

Writes, concurrency, and anything under load. One reader on an idle database is
the easy case, and the answer to "is anything unindexed" is the same either way —
which is the question this was built to answer and the only one it answers.
