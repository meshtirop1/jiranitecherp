# Automation

Sections 31, 64, 65 and 66 of the brief. This describes the rules engine as it is built:
what a rule can start from, what it can test, what it can do, the rules the application ships
with, and the limits that keep a rule from doing more than somebody meant. For how events
reach it, see *Event architecture* in [architecture.md](architecture.md).

## What a rule is

**WHEN** an event happens, **IF** every condition holds, **THEN** do these things, in order.

A rule is a row in the database, written and changed on the rules page at `/automation`, not
a class. The fixed handlers in the codebase are how modules react to each other and are
changed by a release; a rule is how the firm reacts to its own events in ways it decides for
itself.

A rule **acts as the firm**, not as whoever wrote it. The work it raises is raised by "the
firm" (the same way the public API raises work for a key), and every row its actions write is
recorded in the audit trail with the actor `Automation rule: <name>`. Who wrote and changed
the rule is in the trail against the rule itself.

A new rule is **off**. Switching it on is a separate act, refused until the rule has at least
one action, and taking away its last action switches it off again. A rule acts only on events
that happen **after** it was switched on: the outbox can be holding older events (a backlog
after an outage, or the minute between somebody hiring three people and somebody else
switching the joiners' rule on), and a rule that reached back for those would raise work for
things already dealt with by hand.

## How it runs

```
something changes ──► domain event ──► outbox row (stamped with the chain of rules behind it)
                                          │
                              dispatcher  ▼
                         MatchRules<Event>: for each rule that is on for this event,
                         check it was switched on before the event, test the conditions,
                         check the loop and fan-out limits, write an AutomationRun
                                          │   (queued at once, or waiting out a delay)
                                          ▼
                              AutomationRunDue ──► outbox row
                                          │
                              dispatcher  ▼
                         CarryOutAutomation: each action through its service,
                         each in its own scope, each step recorded as it finishes
```

Both halves are outbox handlers, so **a run is retried and set aside by the outbox**, with its
backoff and its number of attempts (eight by default, `Outbox:MaxAttempts`), and never by a
retry policy of its own. Matching and carrying out are separate messages so that one rule's
failing email is retried on its own, rather than making the outbox deliver the original event
again to every other handler that had already succeeded.

The matcher is idempotent: a run is keyed on the rule and the outbox message, checked before
inserting and enforced by a unique index, so the second delivery of one event does nothing.

## Triggers — WHEN

The events a rule may start from are listed in `Application/Automation/Triggers.cs`, and
nothing else fires a rule. Adding one is a line in that file; the matcher is registered for
every entry.

| Area | Trigger | Who it can reach |
|---|---|---|
| Work | A piece of work is raised · moves on the board · is given to somebody · is finished | who raised it, who it was given to, its assignee; the lead of its project |
| Work | A project is started · is held, delivered, cancelled or resumed | the project's lead |
| Engineering | A pull request is opened · merged · a build finishes · a deployment finishes · a release is declared | roles and named people |
| Incidents | An incident is raised · resolved | roles and named people |
| People | Somebody is hired · starts · their leaving is begun · leaves · leave is approved | the person, their manager; for a hire, the head of their department |
| Hiring | An offer is accepted | roles and named people |
| Clients | A client is taken on · a client's standing changes · an opportunity is won or lost · a contract comes into force | roles and named people |
| Money | An invoice is sent · is 7, 21 or 45 days overdue · is paid in full · an expense claim is submitted | the claimant; roles and named people |

Deliberately **not** offered: events that are plumbing (a webhook delivery received, an API
key used), and events that carry what should not flow into a work item title, a notice or
somebody else's webhook — pay terms, pay runs, where somebody signed in from.

**The one scheduled trigger.** "An invoice is overdue" is not something anybody does, so
nothing raised it. A daily job (`automation.invoices_overdue`) raises `InvoiceOverdue` at
seven, twenty-one and forty-five days late — the ladder `ReminderKind.InvoiceOverdue` already
declared — once per rung per invoice, recorded in the reminders ledger so a restart does not
announce twice. It looks only when a rule for overdue invoices is switched on, so switching
one on later picks up every invoice at the rung it has reached.

## Conditions — IF

A condition is a field of the event, a comparison, and a value. Every condition must hold
(AND); a rule that needs OR is two rules, and the history then says which of the two fired.

| Field kind | Comparisons | The value is written as |
|---|---|---|
| Text | is, is not, contains, does not contain, is filled in, is empty | text; case does not matter |
| Number | is, is not, is at least, is at most, is filled in, is empty | a number, `7` or `2500.50` |
| Date | the same as numbers, "at least" meaning on or after | `2026-10-01` |
| Choice (a status, a severity) | is, is not | one of the names the page lists, such as `Done` |
| Yes or no | is | yes or no |
| Identifier | is, is not, is filled in, is empty | the identifier at the end of a record's address |

Moments in time ("when it happened") are not offered for conditions: every event has one, and
a condition on it could only compare it with a date somebody typed long ago.

There is no expression language and nothing is evaluated. A condition is checked against its
trigger when it is written — the field must exist, the comparison must suit it, the value must
read as the field's kind — so a condition that could never hold is refused on the screen
instead of being silently false for every event for a month. Amounts are in cents, because
that is how money travels in events, and the field's label says so.

**What a condition cannot test** is anything that is not in the event. The brief's first
example — "WHEN a pull request is merged, IF CI passed" — needs the build's outcome, which the
merge event does not carry; it can be written the other way round, on "a build finishes" with
`Outcome is Passed` and `Branch is main`.

## Actions — THEN

| Action | Done through | Safe to run twice because |
|---|---|---|
| Raise a piece of work: title, detail, for whom, on which project, due in how many days | `WorkService` | the work item's id is saved on the run the moment it exists; a retry finds it and does not raise a second |
| Tell people: a line in their notice centre, emailed as their notice preferences say | `NoticeService`, kind *Rule* | — a retry after a failure in the middle of the list tells the first ones again |
| Email people who work here, whatever their preferences | `IMailer`, from the outbox | — as above; a duplicate email is the trade the rest of the application's mail already makes |
| Add lines to the joining checklist of the person the event names, starting one if there is none | `OnboardingService` | a line already on the list is not added again |
| Notify an outgoing webhook subscription | a queued `OutboundDelivery`, signed and retried by the outbound sender | — the receiver sees a second notification |

Text an action writes can carry the event's fields in braces — `Create accounts for
{FullName}, who starts on {StartsOn}` — and `{Rule}` for the rule's name. A name the trigger
does not carry is refused when the rule is saved. Substitution is all it is: the values are
escaped where they land, on the pages and in the emails, like every other value.

**Who.** An action names people as: the person in a field of the event; that person's manager
(or, when nobody is named as their manager yet, the head of their department — which is the
case at the moment somebody is hired); the lead of the project the event names; the head of
the department it names; everybody signed in with a role; or one named person. Work is given
to one person, never to a role. Only people still on the staff list are reached.

**There is no way to name an email address or a URL.** Every message goes to somebody who
works here, at the address they sign in with, and every webhook goes to a subscription set up
and signed on the webhooks page. A rule is written once and fires for months; one that could
write to any address would be a standing way to send the firm's records out of it that no
other permission governs.

**A refusal is final; a failure is retried.** When a service refuses — a project that has
closed, somebody who has left, nobody holding the role — the refusal is recorded against that
step in words, the rule's other actions go ahead, and the run ends *Done, with refusals*. The
same request would be refused the same way on every retry, so it is not retried. Anything
else — a mail server that does not answer, a database that is busy — fails the run, the outbox
retries it, finished steps are skipped, and after the outbox's last attempt the run says *Gave
up* at the same moment the machinery screen counts the message as abandoned. Putting the
message back from the machinery screen carries on from the first unfinished step.

## Delays

A rule can wait up to thirty days after its event before acting. The run is written at once
and waits in its own table; a job (`automation.delayed`) starts waiting runs every five
minutes, so "an hour later" means between sixty and sixty-five minutes. A rule switched off
while a run waits cancels it, and the history says so.

## Loops and fan-out

Every outbox message carries the chain of rules whose actions led to it
(`outbox_messages.Causation`). The dispatcher makes the chain ambient while a message's
handlers run, so anything a fixed handler saves in reaction carries it onward, and a run adds
its own rule before carrying out its actions.

- **A rule never fires on its own work**, directly or through other rules: if the rule is in
  the event's chain, the run is recorded as *Held back* and nothing is done.
- **A chain stops at three rules.** A client taken on starting a project whose own rule sets up
  its tasks is two; two rules feeding each other stop on the third lap.
- **A rule fires at most sixty times an hour.** An import of four hundred clients would
  otherwise answer with four hundred emails inside a minute. Past the limit, runs are recorded
  as held back, so the history says exactly how many.
- **At most ten conditions and ten actions a rule, twenty checklist lines an action, and
  twenty-five people an action reaches**, however large the role.

## The rules the application ships with

On every start, `AutomationService.SeedTemplatesAsync` adds any shipped rule not already in
the database, switched off. Once a rule is in the database it belongs to the firm: a later
start never overwrites it, and a shipped rule can be edited and switched on and off but not
deleted (it would only come back).

**Set up a new project** (section 64). When a project is started, five pieces of work on the
project: create the repository and connect it here; write the README and first architecture
notes; agree the team and name its lead; set up development, staging and production; break it
into epics and plan the first sprint.

**Get a new joiner ready** (section 65). When somebody is hired: add *email account*, *Git
host account* and *chat account* lines to their joining checklist; raise work to create their
accounts and prepare their equipment; raise work for their manager to put them on a team and
confirm who they report to; tell HR, IT (the DevOps engineers) and their manager.

**Welcome a new client** (section 66). When a client is taken on: raise work to confirm the
billing details, attach the signed agreement to their record, and name the main contact and
support channel; tell sales and the finance manager.

**Chase an overdue invoice** (section 31's own example). When an invoice is seven or more days
overdue: tell the finance manager and sales, and raise work to chase it.

**What those sections list that no rule does, and why.** Nothing here creates an account, a
Git repository, an environment or a folder, or picks a team or a manager. Accounts and teams
are decisions — the onboarding service's own remarks explain why access is never granted from
a date — so the rules hand the decision to a person as a piece of work. A repository cannot be
created because no Git provider here holds credentials that can write. Environments live at
the hosting provider. Documents are attached to their record, so a client's or a project's
"folder" is its own page, which exists the moment the record does; the same is true of a
client's "CRM record", "workspace" and "billing profile" — the client record is all of them.
There is no support module (section 26), so "support profile" is a piece of work naming the
contact and channel.

## Permissions

| Permission | Held by | Why |
|---|---|---|
| `automation.view` — read the rules and their history | owner, administrator, HR, department head, project manager, engineering manager, finance manager, sales, auditor | the people whose processes the shipped rules run can see what is done in their name and why a work item appeared; the auditor reads it as evidence of what the system did on its own |
| `automation.manage` — write, change, switch on and off, delete | owner, administrator | a rule acts as the firm: it raises work on any project, tells any role and notifies the webhook subscriptions, so writing one is an administrative act whatever it looks like |

The forms on the rules page are drawn only for `automation.manage`, and a form that is not
drawn cannot be posted to.

## What is not built

- **Deploying, approving, paying, granting access or deleting.** There is no action for any of
  them. The brief's "THEN deploy staging" needs a deployment integration this application does
  not have, and the others are decisions a rule should put in front of a person rather than
  take. "Approvals" in section 31's list is met only in that sense.
- **OR, and conditions on anything but the event.** See *Conditions*.
- **Schedules as triggers.** Beyond overdue invoices there is no "every Monday" trigger; the
  recurring jobs are registered in code for the reason `IRecurringJob` gives.
- **API calls to arbitrary addresses.** Only the signed outgoing webhooks.
- **Trying a rule out.** There is no dry run; a rule is tested by switching it on and reading
  its history, or by writing it with a condition that only a test record will meet.
