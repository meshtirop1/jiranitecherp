# Deployment

How this application goes onto a host, how it is updated, and how to tell whether it is
running. Section 87 of the brief. Every setting mentioned here is described in
[configuration.md](configuration.md).

## What runs

`docker/compose.yaml` starts:

| Service | What it is | Reachable from |
|---|---|---|
| `web` | the application, including its background workers and scheduled jobs | `127.0.0.1:${WEB_PORT}` on the host only |
| `db` | PostgreSQL 18 | the containers' network only |
| `cache` | Redis 8 | the containers' network only |
| `proxy` | Caddy, terminating TLS — **only with `--profile proxy`** | ports 80 and 443 |

There is no separate worker container. The event dispatcher, the webhook processor, the
outbound webhook sender and the scheduled jobs run inside `web` as hosted services.

**Run one `web` container.** The queues claim their work with a lease in the database and
would share it correctly between two, but the scheduler takes no lock: a second instance runs
every daily sweep a second time. The jobs are written to survive that, because a process dying
between the work and its record produces the same thing, but two instances racing can still
send one reminder twice. The reasoning for not adding a lock is in `Scheduler.cs`.

## A first deployment

On a host with Docker and the Compose plugin:

1. **DNS.** Point `ERP_DOMAIN` at the host. The proxy asks Let's Encrypt for a certificate
   the first time it starts, and that fails if the name does not resolve here yet.
2. **The repository.** `git clone https://github.com/meshtirop1/jiranitecherp.git` and
   `cd jiranitecherp`.
3. **Settings.** `cp .env.example docker/.env`, then fill in at least `POSTGRES_PASSWORD`,
   `ERP_DOMAIN` and the three `OWNER_` lines. With the bundled proxy, also set
   `PROXY_NETWORK` to the value of `APP_SUBNET`. `docker/.env` is ignored by git; it holds
   the database password.
4. **Start.**

   ```sh
   cd docker
   docker compose --profile proxy up -d --build
   ```

   Leave out `--profile proxy` if the host already runs nginx or a load balancer; see
   *Behind an existing proxy* below.

   The image build runs the whole test suite. An image whose tests fail is never produced,
   so a failing build means nothing changed on the host.
5. **Sign in** at `https://ERP_DOMAIN` as the owner, change the password, then clear the
   `OWNER_` lines from `.env`.

The database schema is created and migrated by the application on start. There is no
separate migration step to forget.

## HTTPS

With the bundled proxy, Caddy obtains the certificate and renews it thirty days before it
expires, with nothing to do. Its certificates live on the `caddy-data` volume; keep that
volume, because Let's Encrypt limits how many certificates one name may be issued in a week.

The application sets every security header itself — HSTS, the content security policy,
`X-Content-Type-Options` and the rest — so the proxy adds none. It speaks plain HTTP on 8080
and never redirects to HTTPS itself, because behind a proxy that terminates TLS the redirect
would loop.

### Behind an existing proxy

Send the domain to `127.0.0.1:${WEB_PORT}`, pass the client's address and scheme, and set
`PROXY_NETWORK` to the address the proxy connects from — for a proxy on the same host,
`127.0.0.1/32`. An nginx site, for example:

```nginx
server {
    listen 443 ssl http2;
    server_name erp.jiranisokotech.co.ke;
    # ssl_certificate and ssl_certificate_key as the host already manages them

    client_max_body_size 12m;   # the largest upload the application accepts is 10 MB

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Without `PROXY_NETWORK` the application still works, but records the proxy's address for
every sign-in and puts every visitor to the careers page in one rate-limit bucket. It says so
in its log at startup.

## Updating

```sh
cd jiranitecherp
git pull
cd docker
docker compose --profile proxy up -d --build
```

Compose builds the new image, runs the tests inside it, and replaces the `web` container only
if that succeeded. Migrations run as the new container starts. Sessions survive, because the
cookie keys are on the `keys` volume.

**Migrations only go forward.** Rolling back the code after a migration has run leaves an
older application facing a newer schema, which usually works — new columns are ignored — and
sometimes does not. To go back reliably, restore the database from the backup taken before
the update, then check out the earlier commit and rebuild. Take that backup first; see
section 88 in the checklist, which is not yet done.

## Is it running

| Check | Answers | Used by |
|---|---|---|
| `GET /health` | the process is up | Docker's healthcheck; restarts a container that stops answering |
| `GET /ready` | the process can reach its database | a load balancer, which stops sending traffic without restarting anything |
| `/settings/machinery` | queues, jobs, the last run of each, dead letters | a person, signed in |
| `GET /metrics` with the token | counters for a collector | Prometheus or similar, when `METRICS_TOKEN` is set |

`/health` deliberately checks nothing outside the process. If it checked the database, an
outage would restart every instance in a loop.

## Logs

Production and staging write one JSON object per line to standard output:

```sh
docker compose logs -f web
```

Each line has a timestamp, level, category and message, and the message's values as
separate fields, so a collector can search by them without parsing prose. The proxy writes
its access log the same way.

## Files that cannot be regenerated

| Volume | Holds |
|---|---|
| `db-data` | the database — everything |
| `documents` | attachments |
| `cvs` | applicants' CVs |
| `keys` | the keys that sign cookies and links |
| `caddy-data` | the certificate and the Let's Encrypt account |

`cache-data` and `mail` can be lost without harm. What backing these up involves is section 88,
which is not yet done.
