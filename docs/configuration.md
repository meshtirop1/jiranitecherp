# Configuration

Every setting this application reads, where it comes from, what it defaults to, and what
happens when it is left blank. Section 86 of the brief asks for all environment variables to
be documented; `ConfigurationTests` fails the build when a variable in `docker/compose.yaml`
is missing from `.env.example` or from this page, so the three cannot drift apart again —
`CACHE_CONNECTION` was used by the compose file for weeks without being in either.

## How settings reach the application

ASP.NET reads, in order, each one overriding the last:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. environment variables, where `__` stands for `:` — `Mail__Host` is `Mail:Host`
4. user secrets, in Development only

In a container, `docker/compose.yaml` turns the short names in `.env` into the long ones
the application reads. **Nothing secret belongs in an `appsettings` file**: they are
committed. Use `.env` on a host and `dotnet user-secrets` on a developer's machine.

## Environments

`ASPNETCORE_ENVIRONMENT`, set from `APP_ENVIRONMENT` in `.env`.

| Environment | Used for | Differences |
|---|---|---|
| **Production** | the live system | JSON logs, the friendly `/error` page, HSTS. No banner. |
| **Staging** | trying a change on a copy before it goes live | Identical to production, plus a banner on every page saying it is not the live system. |
| **Testing** | the automated suite | The production pipeline, quieter logs, a banner. The test host sets it. |
| **Development** | a developer's machine | The developer error page, the HTTPS redirect, the API description at `/openapi/v1.json`, readable logs. Its settings file is local: `appsettings.Development.json` is ignored by git so that a developer's own connection string never leaves their machine. |

Staging is the production pipeline on purpose. A staging copy that behaves differently from
production proves nothing about production.

## Every variable

### Required

| `.env` | Read as | Meaning |
|---|---|---|
| `POSTGRES_PASSWORD` | part of `ConnectionStrings:Default` | The database password. Compose refuses to start without it rather than defaulting to something guessable. |

### The database

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `POSTGRES_DB` | part of `ConnectionStrings:Default` | `jiranisokotech` | Database name. |
| `POSTGRES_USER` | part of `ConnectionStrings:Default` | `jiranisokotech` | Database user. |

Outside a container, set `ConnectionStrings__Default` directly. A value containing
`Data Source=` and no `Host=` selects SQLite, which is what the tests use; anything else is
PostgreSQL.

### The host

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `APP_ENVIRONMENT` | `ASPNETCORE_ENVIRONMENT` | `Production` | Which copy this is. See above. |
| `ERP_DOMAIN` | used to build `MAIL_BASE_ADDRESS` | `erp.jiranisokotech.co.ke` | The name the application answers to. |
| `WEB_PORT` | the published port | `8080` | The port on the host, bound to localhost only; the reverse proxy is what the internet reaches. |
| `PROXY_NETWORK` | `Proxy:TrustedNetworks:0` | blank | The network the reverse proxy connects from. Only from there is `X-Forwarded-For` believed. Blank means the address of whatever connected, which behind a proxy is the proxy. With the bundled proxy, set it to `APP_SUBNET`. |
| `APP_SUBNET` | the compose network's range | `172.30.80.0/24` | The containers' own network. Fixed so that `PROXY_NETWORK` can be known in advance. |
| `ACME_EMAIL` | Caddy's account email | `delivery@jiranisokotech.co.ke` | Where Let's Encrypt warns about the certificate, when the bundled proxy is used. Never empty: Caddy will not start with an empty email. |

### The first account

| `.env` | Read as | Meaning |
|---|---|---|
| `OWNER_EMAIL` | `Bootstrap:OwnerEmail` | Creates the first owner on an empty database, and is ignored ever after. |
| `OWNER_PASSWORD` | `Bootstrap:OwnerPassword` | At least twelve characters. Change it after signing in, then clear these three lines. |
| `OWNER_NAME` | `Bootstrap:OwnerName` | How the owner is named on screens. |

### Mail

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `MAIL_TRANSPORT` | `Mail:Transport` | `File` | `File` writes each message to `/mail` and sends nothing; `None` discards; `Smtp` sends. Anything but Smtp is safe to leave on a copy that must not email real people. |
| `MAIL_HOST` | `Mail:Host` | blank | The SMTP server. |
| `MAIL_PORT` | `Mail:Port` | `587` | |
| `MAIL_USERNAME` | `Mail:Username` | blank | |
| `MAIL_PASSWORD` | `Mail:Password` | blank | |
| `MAIL_FROM_ADDRESS` | `Mail:FromAddress` | `delivery@jiranisokotech.co.ke` | |
| `MAIL_FROM_NAME` | `Mail:FromName` | `Jiranisoko Tech Solutions` | |
| `MAIL_BASE_ADDRESS` | `Mail:BaseAddress` | `https://` + `ERP_DOMAIN` | The address links in emails point at. Mail sent by the background dispatcher has no request to read it from. |

`Mail:Directory` is fixed at `/mail` in the compose file, on a volume, because a relative
default lands inside the image where the application cannot write.

### Code hosts

Each is the secret the host signs or authenticates its webhook deliveries with. Set the same
value in the host's webhook settings. Blank means deliveries from that host are answered with
503 — try again later — so setting it afterwards loses nothing that arrived in between.

| `.env` | Read as |
|---|---|
| `GITHUB_WEBHOOK_SECRET` | `Git:Providers:GitHub:Secret` |
| `GITLAB_WEBHOOK_SECRET` | `Git:Providers:GitLab:Secret` |
| `BITBUCKET_WEBHOOK_SECRET` | `Git:Providers:Bitbucket:Secret` |
| `AZURE_DEVOPS_WEBHOOK_SECRET` | `Git:Providers:AzureDevOps:Secret` |

### Cache and metrics

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `CACHE_CONNECTION` | `Cache:Connection` | `cache:6379` | Redis. Blank runs without a cache: every answer is computed, which is slower and otherwise the same. |
| `METRICS_TOKEN` | `Metrics:Token` | blank | The token a collector presents at `/metrics`. Blank means the endpoint does not exist at all. |

### The AI features

The assistant, project readings and the recruitment aids. **Off until a key is set**: with
`AI_API_KEY` blank every AI page says it is not configured and nothing is sent anywhere. With it
set, the records each use looks up are sent to Anthropic to be read — see [ai.md](ai.md) for
exactly what, and [privacy.md](privacy.md). The key is listed, set or not, on the security centre.

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `AI_API_KEY` | `Ai:ApiKey` | blank | The Anthropic API key. Blank means off. |
| `AI_MODEL` | `Ai:Model` | `claude-opus-5-5` | Which model answers. Change it when the provider retires this one; a retired name is refused on every question. |
| `AI_DAILY_LIMIT` | `Ai:DailyLimit` | `40` | How many times one person may use any AI feature in a UTC day. Each use is billed by the token. |

Also read if present, and not set by the compose file:

| Key | Default | Meaning |
|---|---|---|
| `Ai:Effort` | `medium` | How hard the model thinks: `low`, `medium`, `high`, `xhigh`, `max`. Higher costs more per question. |
| `Ai:MaxTokens` | 16000 | The most one reply may use, thinking included. |
| `Ai:Timeout` | 2 minutes | How long one use may take, retries included, before the page says it timed out. |
| `Ai:MaxRounds` | 6 | How many rounds of lookups the assistant may make before it must answer. |
| `Ai:BaseAddress` | `https://api.anthropic.com/` | Changed only to point at a proxy the firm runs. |

### Retention

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `LOG_MAX_SIZE` | each container's log rotation size | `20m` | Application logs are Docker's, not the database's. A log is rotated at this size. |
| `LOG_MAX_FILES` | how many rotated logs are kept | `10` | With the default size, about 200 MB of history per container. |

Everything else that is removed with age — unsuccessful applicants, documents on leavers'
staff records, withdrawn accounts and the audit trail — is set by an administrator under
*How long records are kept* on the settings page, and applied nightly by the
`retention.sweep` job. Each is kept for ever until somebody sets a period, and each has a
floor below which it cannot be set.

### Backups

Read by the `backup` and `restore` services, which run only when started by hand or by the
host's scheduler. [backups.md](backups.md) has the rest.

| `.env` | Read as | Default | Meaning |
|---|---|---|---|
| `BACKUP_DIRECTORY` | the host directory mounted at `/backups/data` | `/var/backups/jiranisokotech/data` | Where the database dump and the archives of attachments and CVs are written, one timestamped directory per run. |
| `KEYS_BACKUP_DIRECTORY` | the host directory mounted at `/backups/keys` | `/var/backups/jiranisokotech-keys` | Where the key ring's backup is written. Must be a different place from `BACKUP_DIRECTORY`, and not inside it; the backup refuses to run otherwise, because the two together undo the encryption of every second factor. |
| `BACKUP_RETENTION_DAYS` | `RETENTION_DAYS` in `backup.sh` | `30` | Backups older than this are removed after each successful run, and never after a failed one. `0` keeps everything. |

### Fixed in the compose file

These are paths on volumes, not choices, and are listed so that whoever backs the host up
knows what they are.

| Read as | Value | Holds |
|---|---|---|
| `DataProtection:KeyRingPath` | `/keys` | The key ring that signs cookies and links and encrypts every account's second factor and every webhook subscription's secret. Losing it signs everybody out, voids every link already sent, and leaves no second factor that can be checked — back it up, apart from the database. See [backups](backups.md). |
| `Documents:Directory` | `/documents` | Attachments. |
| `Cvs:Directory` | `/cvs` | Applicants' CVs. |
| `Mail:Directory` | `/mail` | What the File transport "sent". |

### Tuning, with defaults that need no setting

Read from configuration if present; nothing in the compose file sets them.

| Key | Default | Meaning |
|---|---|---|
| `Outbox:PollInterval` | 5 seconds | How often the event dispatcher looks for work. |
| `Outbox:MaxAttempts` | 8 | Tries before an event is set aside as dead. |
| `Outbox:Retention` | 30 days | How long dispatched events are kept. |
| `Git:PollInterval` | 10 seconds | How often webhook deliveries are processed. |
| `Cache:OpeningsLife` | 60 seconds | How long the public careers list is cached. |
| `Mail:Timeout` | 20 seconds | How long an SMTP send may take. |
