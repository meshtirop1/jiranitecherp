# What this system holds about people, and why

Section 55 of the brief asks for data minimisation. Minimisation is a decision about each
thing held, and a decision nobody wrote down is one nobody can check, so this is the list.
It describes what the code stores today; when a field is added to a record about a person,
add it here.

The law in view is Kenya's Data Protection Act, 2019. The firm is the controller.

## Staff

| What | Why it is held | Who sees it |
|---|---|---|
| Name, job title, department, manager, start and leaving dates | The employment contract: work is assigned to people and approved by them. | Anybody who can see staff. |
| Personal phone and email | Reaching somebody who is away from work. | Anybody who can see staff — unless the person has chosen, on their own profile, to keep them to HR. Then only HR and the person. |
| National identity number, KRA PIN | Statutory: payroll deductions and returns are filed against them. | Holders of `employees.pay`, and only masked — enough to check against the paper copy, not enough to be worth taking from a screenshot. Every view is recorded. |
| Next of kin — name, relationship, phone | Somebody to call in an emergency. Nothing else. | Anybody who can see staff, because the person who needs it is whoever is there when something happens. |
| Salary, pay frequency, contract type, notice, probation | The contract, and paying people. | Holders of `employees.pay` only, and every view is recorded. |
| Leave, time, expenses, payslips, goals, reviews, equipment | The work itself. | The person, their manager, and the function that approves it. |
| Documents and a photograph on the staff record | Contract and identity paperwork. | HR; every opening is recorded. |

Not held, deliberately: bank details (payroll exports the amounts; the bank file is prepared
outside), health information, religion, ethnicity, marital status.

**Held in the schema and filled in by nothing:** the staff record has columns for date of
birth and home address, and no screen writes either. They are empty for everybody. Under
minimisation the right move is to remove them unless a statutory return turns out to need
them; until that is decided they are listed here so they are not mistaken for data that is
being collected.

## Applicants

| What | Why | Who sees it |
|---|---|---|
| Name, email, phone | Replying to the application. | Recruitment. |
| Portfolio, GitHub, LinkedIn, years of experience, education, skills, expected salary | Assessing the application — each is something the careers form asks because a decision uses it. | Recruitment. |
| The CV they uploaded | The same. | Recruitment; every opening is recorded. |
| The consent sentence they agreed to, and when | Evidence of what they were told. The exact wording is stored, not a tick, because the wording changes and a tick says nothing about which version was agreed. | Recruitment. |
| Why they were not taken forward | Rejecting somebody requires a reason, because "why did we not take them?" is asked — by the candidate, a colleague, occasionally a tribunal. Internal; the candidate is not shown it. | Recruitment. |
| Interview scores and the interviewer's notes | The hiring decision, and the evidence for it. | The panel and recruitment. |

## Accounts

| What | Why | Who sees it |
|---|---|---|
| Email, display name, phone, whether two-factor is on | Signing in. | Administrators; the person on their profile. |
| Authenticator key and recovery codes | The second factor. | Nobody — encrypted with the key ring before they are stored, and never shown after enrolment. |
| Sign-in attempts — address tried, outcome, time, IP address, browser | Security. A failed attempt against an address with no account is kept, because a list of such attempts is the only evidence that somebody was working through addresses. | The person, on their own security page; administrators and auditors, on the security centre. |

## The audit trail

Every change to a record is written with who made it and when. Values that identify a
person are **not** copied into it: each entity lists its own `AuditExcludes`, and those
fields are left out of the entry. The trail therefore does not become a second copy of the
personnel file. The cost is that it cannot say which of those fields changed, only that the
record was saved — when a record is erased, the entry does name the fields, without their
values.

Reads of what is most sensitive are recorded too — a staff record showing pay, an
employee's or an agreement's document, an applicant's CV, and an access-request export.

## What leaves the firm to be read by an AI provider

Nothing, unless an administrator sets `AI_API_KEY`. Once it is set, the assistant, the project
readings and the recruitment aids send records to **Anthropic**, a processor outside Kenya, to be
read by a language model. What goes is kept to what each use needs, and is listed in full in
[ai.md](ai.md); in short:

| Use | Personal data sent | Who can cause it |
|---|---|---|
| A question to the assistant | Whatever the lookups it makes return — names of staff, clients and (for recruiters' roles) candidates, work item and pull request titles, authors, invoice numbers and amounts — and never more than the person asking could open on a screen. The question itself. | Holders of `ai.ask` |
| A project reading | The project's work, with who holds each item; pull request and deployment authors; hours and money only for holders of `projects.view_all`. | Holders of `ai.analyse` |
| A CV summary | **The applicant's CV**, and the experience, education and skills they wrote on the form. Not their name, contact details, links or salary expectation. | Holders of `ai.recruit` and `candidates.view` |
| Interview questions | The experience, education and skills from the form. | The same |
| A draft letter | The applicant's name and whatever the recruiter types. | The same |

Staff contact details, pay, identity numbers, next of kin, leave and reviews are read by no AI
lookup at all.

Every use is written to the usage log (`ai_exchanges`, shown to holders of `audit.view`): who, when,
which feature, about what, the question in the asker's words, and what was looked up. The answers
are not kept. Every CV sent is also on the audit trail, as `job_application.cv_sent_to_ai`, beside
every opening of it.

Two things to settle before switching it on. **The firm's agreement with Anthropic** decides how
long Anthropic keeps what it is sent and what it may do with it; that is a contract question this
application cannot answer. And **the careers form's consent sentence** does not yet tell applicants
that their CV may be read by a third party's model, so the lawful basis for summarising CVs received
under the current wording is the firm's to decide.

## How long

Every period below is **off until an administrator sets it** on the settings page, and off
means keep. A retention job that arrived with defaults would delete records on the first
night after an upgrade. Each period has a floor, because a short enough period is a way of
deleting something without being seen to.

| Kind | Setting | Floor | What happens |
|---|---|---|---|
| Applicants never hired | months after their last application closed | 1 month | Name, contact details, links, application notes and the reason for rejection are erased and the CV file deleted. The row stays, empty, so counts and the history of the opening remain true. **Interview scorecard notes are not erased** — they no longer name anybody once the name is gone, but an interviewer's words can still describe a person, and they should go too. |
| Leavers' documents and photographs | years after leaving | 5 years | The files are deleted. Agreements are kept: they are the firm's record of what it signed. |
| Withdrawn accounts | months after withdrawal | 1 month | Address, name and phone are replaced. The row stays so the trail's actors still resolve, as "Former user". |
| The audit trail | years | 7 years | Entries older than the period are deleted, after an entry saying how many and why. |

The sweep runs nightly as `retention.sweep` and its result is on the machinery screen.
Application logs are Docker's, capped by `LOG_MAX_SIZE` and `LOG_MAX_FILES` — see
[configuration](configuration.md).

**Not yet covered by a period:** sign-in records, the AI usage log, and the staff record itself. A leaver's
name stays on their work for as long as the work is kept, which is argued on
`Employee.Leave`; erasing somebody who has left is a decision taken on an erasure request,
not by a timer.

## Rights

A request is logged on the privacy screen, and the deadline is computed from when it
arrived. Deciding and carrying out an erasure are two permissions, `privacy.respond` and
`privacy.erase`, so one person cannot do both in the same minute.

- **Access.** The request page offers one JSON file of every record that refers to the
  person, whole rows rather than chosen fields, plus what the trail records about them.
  Documents are listed, not attached. The download is itself recorded.
- **Erasure.** Decided per class of data — erased, anonymised, kept until a date on a stated
  basis, or refused with a reason — and a request cannot be closed with nothing recorded. The
  audit trail is never erased for anybody: it is the evidence of what was done, including the
  erasure.
- **Rectification.** Staff correct their own contact details on their profile; anything else
  is corrected by HR on the staff record, and the trail records the change.
- **Objection to being contacted directly.** The profile's privacy setting, above.
