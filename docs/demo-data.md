# A database worth looking at

Section 83 of the brief asks for development seed data: a sample organisation,
departments, employees, projects, clients, tasks, repositories, invoices,
candidates, job openings, assets, servers and incidents. Then it adds one
sentence that is harder than all of it.

> Seed data must be clearly identifiable as development/demo data.

That sentence is hard here for a reason particular to this system. This is one
firm's real ERP, not a product with a demo tier, and the same binary runs in
production — so there is no separate place to put demonstration data and no
environment variable that helps, because a variable is a fact about where the
tool ran and the row outlives the run. A demonstration client restored or copied
into the live database would afterwards be indistinguishable from a real one.

So the marking is in the values, and the guard is in the tool.

## How to run it

`tools/JiranisokoTech.DemoData` writes eighteen months of a small software firm
through the application's own services. It is a console tool rather than a page
or a start-up flag: a button is a button somebody presses on the wrong copy, and
a flag is a flag somebody leaves set. It is not published into the runtime image
— the Dockerfile publishes only the web project — so it is not present on a
deployed host at all.

Against a local SQLite file, which is the quickest way to get something to look
at:

```bash
DEMODATA_CONNECTION="Data Source=demo.db" dotnet run --project tools/JiranisokoTech.DemoData
```

Against PostgreSQL in Docker, the database in `docker/compose.yaml` publishes no
port. Reach it with a temporary sidecar on the compose network, the same way
[docs/performance.md](performance.md) does:

```bash
docker run -d --rm --name pg-tunnel --network jiranisokotech_default -p 127.0.0.1:15432:5432 alpine/socat tcp-listen:5432,fork,reuseaddr tcp-connect:db:5432
```

Make a database for it to fill:

```bash
docker compose -f docker/compose.yaml --env-file docker/.env exec -T db psql -U jiranisokotech -d postgres -c "CREATE DATABASE jiranisokotech_demo OWNER jiranisokotech;"
```

Then fill it:

```bash
DEMODATA_CONNECTION="Host=127.0.0.1;Port=15432;Database=jiranisokotech_demo;Username=jiranisokotech;Password=<from docker/.env>" dotnet run --project tools/JiranisokoTech.DemoData
```

Point the application at it by changing `POSTGRES_DB` in `docker/.env` and
restarting, or by pointing `ConnectionStrings__Default` at it directly. Stop the
sidecar with `docker rm -f pg-tunnel` when you are finished; it is a route into
the database and it should not be left open.

The tool creates **no sign-ins at all**. An employee and an account are separate
things in this application, and a demonstration database where ten invented
people can all log in is one where eventually somebody does. Sign in as the owner
account the application seeds from `OWNER_EMAIL` and `OWNER_PASSWORD`.

## The five markers

Each one covers a different way somebody meets the data, which is why there are
five rather than one. Any single marker leaves a route by which a demonstration
row looks real.

| Marker | Where it is | What it prevents |
|---|---|---|
| Trading name — `Jiranisoko Tech Solutions (DEMONSTRATION DATA)` | `FirmSettings`, and therefore every invoice and every offer letter | A document that does not say what it is |
| Invoice prefix — `DEMO` | Every invoice number, so they read `DEMO-0001` | The one marker that travels outside this application, into a PDF or an export |
| Code prefix — `demo-` | Every client code, project code, department slug, advert slug and asset tag (upper-cased for tags) | A row that looks real to somebody querying the database |
| E-mail domain — `demo.invalid` | Every contact, candidate and client address | A letter reaching somebody. `.invalid` is reserved by RFC 2606 and cannot resolve, so it fails even if the mail transport is switched on by accident |
| Audit actor — `the demonstration seed` | `ActorName` on every audit entry | The question somebody asks a year later, which is not "who was this" but "why is there a year of history nobody remembers making" |

There is also an announcement on the notice board saying in words what the data
is. That is the marker a person actually reads: the other five are in values you
have to be looking at the right column to notice, and this one is at the top of
the first screen anybody opens.

`DemoDataTests` walks the written database and insists on all five. A marker that
has quietly stopped being applied is worse than none, because the data then looks
real and somebody has been told it is marked.

## The three refusals

The tool has no confirmation prompt. A prompt in a tool nobody runs twice a year
is one somebody answers without reading; these are the check a prompt pretends to
be. Each prints one sentence and exits 2 without writing anything.

1. **Business records.** Any row in clients, employees, projects or invoices and
   it refuses, naming the counts. Four tables rather than one, because a
   half-restored copy may have clients and no invoices.
2. **A firm that can invoice.** `FirmSettings.IsReadyToInvoice` is the question
   the application itself asks before letting anybody invoice. A firm that has
   ever sent one has its real trading name on paper somebody outside it is
   holding — and overwriting that name with a marker is the very first thing the
   seed does.
3. **A copy somebody has already named.** An empty database whose trading name is
   already set and does not contain the marker. Nothing has been billed, so the
   second refusal passes; but somebody set this copy up deliberately, and
   overwriting it would be doing what the second refusal exists to prevent, one
   day earlier.

It also takes its connection string from `DEMODATA_CONNECTION` or an argument and
**never** from `ConnectionStrings:Default`. A tool that read the same key the
application reads is one that fills whichever database is configured on the
machine it is run on, which on a server is the live one.

## Two things that look wrong and are not

The repositories page shows both seeded repositories as **"Secret differs"**. That
is correct and is the page telling the truth: the seed connects them with its own
webhook secret, and the copy of the application you are looking at is configured
with a different one, so a delivery signed with the seed's secret would be
refused. Nothing will ever send one, because nothing outside your machine knows
those repositories exist.

The **sign-in page and the careers site** show the marked trading name like every
other page — but only since the name stopped being typed into seven places as a
literal. If you are looking at an older build and they show the plain name, that
is what you are seeing.

## Why it goes through the services

Every row is written by calling the service a person would use. The alternative
is raw SQL, which is what `tools/JiranisokoTech.ScaleCheck` does and is right to:
that tool needs a million rows and is measuring reads, so the domain rules would
cost an hour of machine time for no benefit.

Here the rules are the point. A demonstration database exists to be looked at,
and a page shows what a record *means* rather than what is in its columns. An
invoice has to have been drafted, had lines added, been sent and then part-paid
before the ageing report has anything to age. A piece of work has to have moved
through its transitions before a burndown has a shape. A requisition has to have
been approved before an advert can be published, because the service refuses
otherwise. Writing rows directly would produce a database that satisfies the
schema and contradicts the domain, and every screen reading it would be showing
something that could not have happened.

It has a second effect worth naming: the tool is the widest compile-time check in
the repository that the application services still fit together, and the only
thing anywhere that exercises eighteen months of them in order.

## Why the clock travels

`IClock` is a singleton in the running application, so a seed that simply called
the services would produce a firm where every client was taken on, every hour
logged, every invoice issued and every incident resolved in the same three
seconds. The pages would fill up and look fine. The ageing report would have one
bucket, the burndown one point, the oldest overdue would be today, and every
trend on every screen would be a flat line — which is a worse demonstration than
an empty database, because an empty database is honestly empty.

`TravellingClock` replaces it, and it reaches the audit trail too, because
`AppDbContext` takes the same clock. So a change made in March is stamped March in
the trail. `DemoDataTests` asserts the spread on both, and asserts that the
timeline ends at today rather than whenever the tool was written — a seed of
eighteen months ending a year ago satisfies every other check and shows a
dashboard where nothing has happened lately.

## What it deliberately does not invent

Two things, and both are the kind of thing a seed is tempted by.

**It does not walk the approval chains.** A requisition's decision is recorded
directly rather than approved step by step. Submitting one opens a chain up the
reporting line, and the line ends at the managing director — so a seed answering
every step would be answering as somebody it also invented, which is the one thing
in this data that would be a lie rather than a simplification. The record is still
honest about the state, because publishing an advert refuses unless the
requisition is approved.

**It creates no sign-ins.** See above.

## What is on the screens afterwards

The seed aims at states rather than counts, because a screen with rows on it can
still have nothing to say. Three clients and a prospect; four projects, one of
them the firm's own with no client; twelve pieces of work spread across Todo, in
progress, blocked and done, over four sprints of which one is running; hours
logged against them, some approved and some not; five invoices — settled,
part-paid, overdue and one still a draft; an advert with six applications; eight
assets of which six are issued; three services and six resources, one certificate
expiring in three weeks; three incidents, one still open; four help desk tickets,
one already past its promise; two articles, one overdue a check.

`DemoDataTests.The_seeded_firm_has_something_on_every_screen_worth_looking_at`
asserts each of those states by name, with the screen it is for in the failure
message.
