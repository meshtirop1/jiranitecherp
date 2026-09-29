# Security

Who can get in, what they can do once they are in, what is recorded, and where the secrets
are. Each rule names where it is enforced, so the claim can be checked against the code.
Section 28 of the brief (the security centre), sections 4 and 5 (authentication and roles)
and section 54 (security) are covered here; what personal data is held and for how long is
in [privacy](privacy.md).

## Getting in

**There is no self-registration.** An administrator or HR invites somebody from `/accounts`;
the invitation opens an account with no password and a link, emailed and also shown on the
page, from which the person sets their own password. Following the link confirms the address.
The first owner is created once from `Bootstrap__Owner*` on an empty database, and those
settings should be cleared afterwards — the security centre flags them while they are set.

| Rule | Value | Where |
|---|---|---|
| Password length | twelve characters, no composition rules | `IdentityConfiguration` |
| Lockout | five failures, fifteen minutes, per account | `IdentityConfiguration` |
| Failures from one address | fifteen in fifteen minutes, whatever accounts they were against | `SignInThrottle` |
| Sign-in posts | sixty per five minutes per address | `SignInLimits` |
| Recovery requests | five per fifteen minutes per address | `RecoveryLimits` |
| Session | HttpOnly, Secure, SameSite=Lax cookie, eight hours sliding | `IdentityConfiguration` |
| Revocation reaches an open session | within one minute | `SecurityStampValidatorOptions` |

**Second factor.** Authenticator codes, and eight one-time recovery codes. Offered to everybody
and not yet required of anybody. The authenticator key and the unused recovery codes are
**encrypted with the application's key ring** before they are written (`ProtectedUserStore`):
Identity's default stores them as they are, which made any copy of the database a working
second factor for every enrolled account. Rows written before that change are converted at
startup.

**Losing the key ring turns every second factor off in effect** — nothing can be checked — and
signs everybody out. Back up the `keys` volume, and keep that backup apart from the
database's; see [deployment](deployment.md). An administrator can turn off somebody's second
factor from their account page, which is also the way back for a phone lost together with its
recovery codes.

**Machines** use API keys, scoped to named permissions, stored as a hash and shown once. **Code
hosts** sign each webhook delivery; the signature is checked in constant time against the
secret in configuration, and a delivery already seen is acknowledged and not processed again.

## What people can do

Permissions, not roles, are what the code checks: 87 of them, each named `area.action`. A role
is a bundle of permissions in `Application/Authorization/Permissions.cs`, synced into the
database on every start. There are eighteen roles — see [architecture](architecture.md#authorization-strategy)
for the list and why two of the brief's names are absent.

- **Deny by default.** A page with no authorization attribute requires signing in; nothing is
  public unless it says so.
- **Server-side.** The navigation hides what somebody cannot open, and the page refuses them
  anyway.
- **Reach** narrows a permission to the records somebody is related to — the projects they are
  on, the people who report to them, their own claims.
- **Separation of duties.** No role below the owner and administrators holds both halves of:
  approving and paying a claim, drafting and sending an invoice, agreeing a contract and
  invoicing against it, ordering goods and receiving them, running the payroll and paying it,
  deciding an erasure and carrying it out. `PermissionTests` fails the build if one does.
- **No permission without a door.** `EnforcementTests` fails the build for a permission that is
  granted and checked nowhere.

Roles are not editable on screen, deliberately: the tests vouch for the matrix in code, and the
separations above would hold only until somebody ticked a box.

## The security centre

`/security`, for holders of `security.view` — the owner and administrators, and the auditor
role. It is kept apart from the audit trail because it shows every address anybody has typed
into the sign-in form, which is more than somebody reading the trail for an invoice needs.

- **Needs a look**: refused sign-ins in the last day, accounts locked out, active accounts with
  no second factor, accounts unused for ninety days, live API keys and any never used, and
  whether an access review is due.
- **Sign-ins**, everybody's, including attempts against addresses that match no account — a
  run of those is somebody working through a list, and this is the only place it shows.
- **Permission changes**: every role granted or taken away, and by whom.
- **Other security events**: accounts withdrawn, restored, locked, unlocked or signed out
  everywhere, second factors switched on or off, API keys issued or revoked.
- **Sessions**: said plainly — sessions are signed cookies and the server keeps no list, so
  there is no row per device. Every session an account has can be ended at once.
- **Secrets**: each secret the application reads, whether it is set, and which configuration
  source supplied it. Never the value, nor any part of it.

**Access reviews** are at `/security/review`, for holders of `users.manage`. One form lists every
account that can sign in, with its roles, its second factor and when it last signed in; the
reviewer ticks the accounts to withdraw and says what they checked against. The review is kept
with a copy of every account as it stood, so it can answer "was this person's administrator
role in front of you when you signed this off". A review is due every ninety days.

## Secrets

| Secret | Kept |
|---|---|
| Database password, mail password, webhook secrets, metrics token | In `.env` on the host, passed to the container as environment variables. Never in a settings file — the security centre flags one that arrives from a file, because a settings file is committed. |
| Passwords | Hashed (PBKDF2). |
| API keys, offer links, repository webhook secrets | Hashed. |
| Second factors, outbound webhook subscription secrets | Encrypted with the key ring, under separate purpose strings. |
| The key ring | On the `keys` volume, outside the image and outside the database. |

## The web surface

- **Headers**: a content security policy with `object-src 'none'`, `base-uri 'self'`,
  `form-action 'self'` and `frame-ancestors 'none'`; `X-Frame-Options: DENY`;
  `X-Content-Type-Options: nosniff`; a strict referrer policy; HSTS outside Development.
  `script-src` and `default-src` are absent on purpose — the reasoning, and the fault that
  setting them caused, are in `SecurityHeaders`.
- **No inline event handlers**, which the policy would refuse; `SecurityHeaderTests` checks.
- **Antiforgery** on every form.
- **Rate limits** on every public surface: sign-in, recovery, the careers form and offer links
  (five posts per ten minutes per address), the public API (thirty a minute anonymous, a
  bucket of 120 per key) and the webhook endpoints.
- **A refusal always has a body**, so a code host retries a 5xx and gives up only on a real
  4xx — see CLAUDE.md for the fault that taught this.

## What is recorded

Every change is written to the audit trail in the same transaction that made it, with who and
when; identifying values are left out of the entry. Role grants are captured although Identity's
join table is not ours. Reads of what is most sensitive are recorded too: a staff record with
pay on it, an employee's or agreement's document, an applicant's CV, a subject access export.
Ending somebody's sessions is written by hand, because the only column that moves is the
security stamp, which the trail excludes. Retention of the trail is set by an administrator,
with a seven-year floor.
