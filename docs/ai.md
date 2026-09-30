# AI

Sections 36 and 37 of the brief, and section 7's list of what AI may do for a recruiter. This
describes what is built, what it sends and to whom, who may use it, and what is not built. It is
one of the documents section 85 asks for.

Everything here is **off until an administrator sets an API key**. With no key every AI page says
it is not configured, and nothing is composed, sent, or made up in its place.

## What is built

| Where | What it does | Permission |
|---|---|---|
| `/assistant` ("Ask" in the menu) | Answers a question about the firm's records from lookups made as the person asking. Can propose a work item, which the person creates with a button — or does not. | `ai.ask` |
| `/projects/readings`, `/projects/{id}/reading` | A project's recorded facts and figures calculated from them, always; a model's reading of how it stands — schedule, engineering, deployment, budget, blockers, main risk — on request, labelled as inference. | `ai.analyse` |
| `/hiring/applications/{id}/assist` | A CV summary set against the advert's stated requirements, suggested interview questions, and drafts of letters to the candidate. | `ai.recruit` and `candidates.view` |
| `/ai/usage` | Who used which feature, when, about what, what it looked up, what came of it, and the tokens it cost. | `audit.view` |

## The provider

Anthropic's Messages API, over plain HTTP from `Infrastructure/Ai/AnthropicModel.cs` behind the
`IAiModel` interface in `Application/Ai`. The model is `claude-opus-5` unless `Ai:Model` says
otherwise. The request:

- sets effort explicitly (`Ai:Effort`, default `medium`). This model always reasons before it
  answers; there is no setting to switch that off, and none is sent;
- marks the instructions cacheable, and never puts anything that changes between calls — the date,
  the person's name — into them, so the repeated calls of a question that makes several lookups
  are billed at the cached rate;
- opts into Anthropic's server-side fallback (`fallbacks: "default"`). If a safety classifier
  declines a request, Anthropic re-runs it on another of its own models instead of returning
  nothing. **This means an answer may occasionally come from a different Anthropic model than the
  one configured**; the usage log records the configured one. If the fallback model also declines,
  the page says the provider declined;
- hands the model's previous reply back verbatim in a lookup loop, because the reasoning blocks in
  it are only valid unchanged;
- asks for structured output (a JSON schema) wherever a page lays the answer out in labelled
  fields — the project reading, the CV summary, the questions, the drafts — so the labels on the
  page are this code's, not whatever headings the model chose.

Failures are turned into a sentence on the page. A rate limit (429), an overload (529) or a failure
on the provider's side (5xx) is tried up to three times in all, honouring the provider's
`retry-after` up to ten seconds; a refused key, a missing model or an oversized request is not
retried, and says what to check. One deadline (`Ai:Timeout`, two minutes) covers every attempt of
one use, so a person is told it timed out rather than left waiting. Nothing that fails is shown as
if it had worked, and every failure is in the usage log.

## How the assistant is kept to what the person may see

Section 36 says a user must never receive information they are not authorised to access. The
assistant cannot read the database. It can only ask for **lookups**, listed in
`Infrastructure/Ai/AssistantTools.cs`, and each one is made **as the signed-in person**:

| Lookup | Checks, before anything is read | Same rule as |
|---|---|---|
| `search` | Each group — people, clients, projects, work, invoices, candidates, documents — against the person's permissions and reach, inside the query | the search page |
| `list_projects`, `project` | `projects.view_all`, or `projects.view_member` narrowed to the projects the person leads or has work on | the project reach (`Reaches.ProjectsAsync`) |
| — within `project` | other people's work only with `tasks.view_all` or `projects.view_all`; pull requests, builds and deployments only with `repos.view`; hours and money only with `projects.view_all` | the board, the repository pages, the project and money pages |
| `work_items` | `tasks.view_own` for the person's own; `tasks.view_all` for everybody's | the board |
| `invoices` | `invoices.view` | the invoices page |
| `client` | `clients.view`; its invoices only with `invoices.view` | the clients page |
| `deliveries` | `repos.view`; a project only within the person's reach | the repository and environment pages |
| `propose_task` | `tasks.create` — and it creates nothing | the raise page |

Who is asking is built from the signed-in principal's permission claims and staff record
(`Web/Authorization/Askers.cs`) — the same claims every page's authorization reads — and never from
anything the model writes. The model chooses which lookups to ask for; it cannot choose who they
are made as, and it names records by the name or code a person would use, resolved only among what
that person may see. A project out of reach and a project that does not exist get the same answer,
so a question cannot discover what exists.

A refused lookup returns a refusal, not the records, so **the records never reach the model** and
no instruction has to be trusted to keep them out of the answer. The model is told to say plainly
that the person does not have access and not to guess at what the lookup would have shown. When a
project is read for somebody who may see only part of it, the model is also told what was withheld
and why, so an absence reads as "not shown to you" rather than as "none".

The tests prove it on what was sent, not on what the page said: `AssistantTests` scripts the fake
model to ask for invoices on behalf of a tech lead, who holds `ai.ask` and not `invoices.view`, and
asserts the client's name appears in none of the requests; the same lookup for a finance manager
sends it. A developer-shaped asker who is not on a project gets "no project matches"; one who is on
it gets their own work and not their colleagues'.

## What is sent to Anthropic

Personal data leaves the firm when these features are used. This is the list, and
[privacy.md](privacy.md) records it too.

| Feature | Sent | Not sent |
|---|---|---|
| Assistant | The question, today's date, and the results of the lookups the model asks for — which can include names of staff, clients and — for somebody who may see them — candidates, project and work item titles, invoice numbers and amounts, pull request titles and authors, build and deployment names — only ever what the person asking could open themselves. Lists are capped: fifty rows, eight per group for a search, and a project's 150 most pressing work items. | Anything behind a permission the person does not hold. Staff contact details, pay, identity numbers, leave, reviews: no lookup reads them. |
| Project reading | The project's name, code, status, due date and lead; its work items, the 150 most pressing (the person's own only, if that is all they may see); open pull requests, builds and deployments of the last 14 days with their authors and links; hours and money if the person holds `projects.view_all`; the calculated figures; the list of what was withheld. | The same exclusions. |
| CV summary | The CV — as the PDF itself, or the text of a Word, OpenDocument or plain-text file — the advert's title, summary and description, and the years of experience, education and skills the candidate wrote on the form. | The candidate's name, email, phone, links to their profiles, salary expectation and consent. Old `.doc` and `.rtf` files are not read, and nothing is sent for them. |
| Interview questions | The advert, the interview type, and the years of experience, education and skills from the form. | The CV, and everything the summary does not send. |
| Letter drafts | The candidate's full name, the role, the kind of letter, and the points the recruiter types. | Everything else about the candidate. |

Sending a CV is recorded on the audit trail as `job_application.cv_sent_to_ai` against the
application, beside the record of anybody opening it, because "who has read my CV" is a question an
applicant is entitled to ask and a third party reading it is the most consequential answer.

What Anthropic does with what it receives is governed by the firm's agreement with Anthropic, not
by this application. Read it — in particular how long inputs are kept and whether they may be used
for anything else — before setting a key, and tell staff and applicants what it says. The
applicants' consent sentence on the careers page does not currently mention a third party reading
the CV; see *Not built* below.

## Fact, calculation, inference

Section 37 asks that actual data, calculated metrics and AI inference be clearly distinguished.
Here that is done by construction rather than by wording:

- **Recorded** facts come from `ProjectFacts`, read from the database as the person. They are shown
  on every visit and need no model.
- **Calculated** figures — open work, overdue, blocked, unassigned, estimated work left, days to the
  due date, open pull requests and the oldest, failed builds and deployments in the window, hours in
  the window, budget used — are computed by code in `ProjectPicture.Calculations`, each shown with
  how it was counted. The model is handed them rather than asked to count, because a model that
  miscounts states the wrong number with complete confidence.
- **Inference** is the model's reading, returned in a fixed shape and drawn in its own box with a
  dashed edge and the words "Inference, not fact" above it. A reading that does not match the shape
  is not shown at all.

The assistant's answers are labelled "Written by a language model — check it against the
records", and list every lookup it made. The recruitment aids are headed "Everything below is
written by a language model and is not an assessment".

## Hiring decisions stay with people

Section 7 forbids a system that decides who is hired. None of the recruitment schemas has a field
for a recommendation, a score or a rank, so a model inclined to write "strong hire" has nowhere to
put it; the instructions forbid it too, and tell the model to ignore anything a CV says about age,
sex, family, religion, ethnicity, nationality, health or disability. Requirements are taken only
from what the advert states. A draft letter is text in a box: nothing here sends anything to a
candidate or changes an application.

## Limits and cost

- **Per person, per day**: `Ai:DailyLimit` uses of any AI feature (default 40), counted from the
  usage log by UTC day. The next use is refused before anything is sent.
- **Per question**: at most `Ai:MaxRounds` rounds of lookups (default 6), capped lists in each
  lookup, and `Ai:MaxTokens` per reply.
- **Time**: `Ai:Timeout` across all attempts.

The usage log records the input and output tokens of every use, which is what the bill is made of.

## Who holds what

| Role | `ai.ask` | `ai.analyse` | `ai.recruit` |
|---|:-:|:-:|:-:|
| Owner, administrator | ✓ | ✓ | ✓ |
| HR | ✓ | | ✓ |
| Department head, project manager, engineering manager, tech lead | ✓ | ✓ | |
| Finance manager | ✓ | | |
| Recruiter | | | ✓ |
| Everybody else | | | |

Granted to the people whose questions these are, not to everybody who can sign in, because each use
sends records to a third party and is billed. Widening it is a change to `Roles.Matrix`, where the
reasoning sits beside each grant.

## The usage log

`ai_exchanges`, shown at `/ai/usage` to holders of `audit.view`: when, who, which feature, what it
was about, the question in the asker's words (assistant only), each lookup with "(refused)" beside
those the person could not make, the outcome and any problem, the model, the tokens and the time.

**The answers are not kept.** An answer is built from records the person could see at that moment;
storing it would be a second, unaudited copy of those records that outlives the permission that
allowed them to be read.

## Configuration

`AI_API_KEY`, `AI_MODEL` and `AI_DAILY_LIMIT` in `.env`; `Ai:Effort`, `Ai:MaxTokens`, `Ai:Timeout`,
`Ai:MaxRounds` and `Ai:BaseAddress` if they need changing. All in
[configuration.md](configuration.md). The security centre shows whether the key is set and where it
came from.

## Not built

- **Conversations.** Each question is answered on its own; nothing is carried to the next one. That
  is simpler to reason about when permissions change, and it is also a limitation.
- **Most of section 36's list beyond looking things up.** Generating reports as documents,
  explaining a failed deployment from its log (the logs are on the code host and not held here),
  drafting an invoice, triggering automations, and assisting with documentation are not built.
  Creating a task is built only as a proposal the person confirms.
- **AI throughout the system** (section 1, principle 10). It is in three places, not everywhere.
- **Summarising interview notes** (section 7) is not built; nor is comparing candidates with each
  other, which section 7 permits only against explicitly defined requirements and this does not do.
- **Streaming.** A page waits for the whole answer.
- **Semantic search or embeddings.** The assistant's search is the keyword search box's.
- **A retention period for the usage log.** It is not yet in the retention sweep, so it is kept
  until somebody deletes it. It holds assistant questions in the asker's words.
- **The applicants' consent sentence** on the careers form does not mention that a CV may be read
  by an AI provider. Until it does, `ai.recruit` should not be used on applications received under
  the current wording without the firm deciding that its lawful basis covers it.
