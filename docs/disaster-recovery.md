# Disaster recovery

What to do when something has been lost, in the order to do it, and what cannot be got back.
Section 89 of the brief. It depends entirely on the backups described in
[backups.md](backups.md) existing and having been copied off the host; read that first, and
set it up before it is needed.

Every command here runs from the `docker/` directory of a checkout of the repository.

## How long, and how much

| | What it is | For this system |
|---|---|---|
| **RPO** — how much work can be lost | the time since the last backup that survives | **Up to 24 hours**, with the nightly backup. If the host is lost, up to 24 hours plus however long ago the last offsite copy ran. |
| **RTO** — how long until people can work | from deciding to recover to signing in | **About two hours** to rebuild on a new host, most of it provisioning, DNS and building the image; **under thirty minutes** to restore the database on a host that is still there. |

These are honest figures for what is built, not targets. There is one PostgreSQL server,
backed up with `pg_dump` once a night; there is no replica to fail over to and no
write-ahead-log archive to replay, so there is no restoring to "five minutes before it went
wrong" — only to the last nightly backup. The database's share of the recovery time grows
with its size; the monthly drill in [backups.md](backups.md#verifying-a-backup) measures it,
and the figure above should be replaced with what the drill shows.

To get the RPO down, run the backup more often — hourly costs nothing but disk at this
firm's volume — or add WAL archiving with a tool such as pgBackRest, which is the step to
take when losing a day's work stops being tolerable.

## Before anything: what to have to hand

Recovery needs things that are not on the host, because the host may be what was lost:

1. **The backups**, offsite, both halves — the data backup and the keys backup, from their
   separate stores.
2. **`docker/.env`**, from the firm's password manager (see *Secrets* below).
3. **Access to DNS** for `ERP_DOMAIN`, if the host's address will change.
4. **The repository**, `https://github.com/meshtirop1/jiranitecherp.git`, and the commit that
   was deployed. Recover to the same commit first and update afterwards; restoring and
   upgrading in one step makes a failure impossible to place.

If any of these is missing, find it before starting. A recovery that stops halfway is worse
than one that has not started.

## The host is lost

The machine, its disk, or access to it is gone. Everything is rebuilt from the offsite copies.

1. **A new host** with Docker and the Compose plugin, as for a first deployment
   ([deployment.md](deployment.md#a-first-deployment)).
2. **The code**, at the commit that was running:

   ```sh
   git clone https://github.com/meshtirop1/jiranitecherp.git /opt/jiranitecherp
   cd /opt/jiranitecherp && git checkout <commit>
   ```
3. **The settings.** Put `docker/.env` back from the password manager. `POSTGRES_PASSWORD`
   may be a new value — the restore creates the database afresh under whatever user and
   password this host has — but the webhook secrets must be the ones the code hosts have, or
   see *Secrets* below.
4. **The backups onto the host**, each into its own directory:

   ```sh
   sudo install -d -m 700 /var/backups/jiranisokotech/data /var/backups/jiranisokotech-keys
   rclone copy erp-data-crypt:<latest>   /var/backups/jiranisokotech/data/<latest>
   rclone copy erp-keys-crypt:<newest>   /var/backups/jiranisokotech-keys/<newest>
   ```
5. **The database only.** Start `db` and nothing else:

   ```sh
   docker compose up -d db
   ```

   Not `web`. The application migrates and seeds an empty database on start, and the restore
   would then — correctly — refuse to write over it.
6. **Restore**, the data backup and the newest keys backup:

   ```sh
   docker compose run --rm restore /backups/data/<latest> /backups/keys/<newest>
   ```

   It checks the checksums, creates the database, restores it in one transaction, unpacks the
   attachments, CVs and key ring into the new volumes and hands them to the application's
   user. It stops and says why at the first thing that is wrong.
7. **Start everything**, and point DNS at the new host if its address changed:

   ```sh
   docker compose --profile proxy up -d --build
   ```
8. **Check**, in this order: `curl -s http://127.0.0.1:8080/ready` answers; sign in as an
   account with two-step on, which proves the key ring came back with the database; open a
   client with an attachment and download it; look at `/settings/machinery` for jobs that
   have not run.
9. **Tell people what was lost** — everything recorded after the backup's timestamp — so
   that it can be entered again.
10. **Set up the backup schedule again** on the new host ([backups.md](backups.md#scheduling-it)).
    The new host has no timer until somebody installs one.

## The database is damaged or wrong

The host is fine; the database is corrupt, or somebody has deleted or changed a great deal
that cannot be put right through the screens.

1. **Stop the application**, so nothing more is written:

   ```sh
   docker compose stop web
   ```
2. **Back up what is there now**, even though it is wrong — it holds the work done since the
   last backup, which may be worth copying out by hand afterwards:

   ```sh
   docker compose run --rm backup
   ```

   If the database is too damaged to dump, this fails and says so; carry on.
3. **Replace the database** with the chosen backup. `--overwrite` because the live database
   is not empty; `--only database` because the attachments and keys are fine:

   ```sh
   docker compose run --rm restore --overwrite --only database /backups/data/<chosen>
   ```

   The database is dropped and created, not merged into, so a table a later migration added
   does not survive into a database whose history says it was never created.
4. **Start** with `docker compose up -d` and check as above.

Attachments uploaded after the chosen backup are still on disk with nothing in the database
pointing at them. They are harmless, and can be found by comparing the `documents` volume
with the database if they need to be re-attached.

**To recover only some records** — one client deleted by mistake — do not restore over the
live database. Restore the backup beside it under another name, copy the rows across by hand
with `psql`, and drop it:

```sh
docker compose run --rm -e PGDATABASE=recovered restore --only database /backups/data/<chosen>
docker compose exec db psql -U jiranisokotech -d recovered
docker compose exec db dropdb -U jiranisokotech recovered
```

## The keys are lost

The `keys` volume is gone or was replaced, and the database is fine. Everyone is signed out,
links already emailed no longer work, and every account with two-step on cannot complete it:
its authenticator key reads as nothing.

1. **Stop the application:** `docker compose stop web`.
2. **Put the newest keys backup back.** If the application has run since the loss it will
   have made a new key ring, and anything encrypted since — an account that set up two-step
   again, a new webhook subscription — used it. Keep those keys and add the old ones beside
   them; each key is its own file, named by its id, so the two sets do not collide:

   ```sh
   docker compose run --rm --entrypoint sh restore -c \
       'cd /backups/keys/<newest> && sha256sum -c SHA256SUMS && tar -xzf keys.tar.gz -C /keys && chown -R "$(stat -c %u:%g /keys)" /keys'
   ```

   If nothing has been encrypted since, the simpler
   `docker compose run --rm restore --overwrite --only keys /backups/data/<any> /backups/keys/<newest>`
   replaces the ring outright.
3. **Start** with `docker compose up -d`, and sign in with two-step to check.

**Without a keys backup, this cannot be recovered.** What the keys encrypted is unreadable
for ever. What has to be done instead:

- **Two-step:** an administrator clears the second factor of each account that had it, from
  the account's page (`/accounts/<id>`), and each person sets it up again. The owner's own
  account needs another administrator, or clearing directly in the database.
- **Outbound webhook subscriptions:** each needs a new secret from `/settings/webhooks`, given
  to whoever receives it.
- **Everyone signs in again**, and any password-reset or invitation link already sent must be
  sent again.

## An update went wrong

A deploy that will not start, or a migration that did the wrong thing.

**If the application will not start and no migration ran**, go back to the previous commit
and rebuild; nothing in the database changed:

```sh
cd /opt/jiranitecherp && git checkout <previous commit>
cd docker && docker compose up -d --build
```

**If a migration ran**, the schema has moved forward, and migrations only go forward. The way
back is the backup taken immediately before the update — which is why
[deployment.md](deployment.md#updating) says to take one:

```sh
docker compose stop web
docker compose run --rm restore --overwrite --only database /backups/data/<before the update>
cd /opt/jiranitecherp && git checkout <previous commit>
cd docker && docker compose up -d --build
```

Anything recorded between the update and the restore is lost; take the backup of the broken
state first, as for a damaged database, if that matters.

## Secrets

`docker/.env` holds the database password, the webhook secrets the code hosts sign with,
`METRICS_TOKEN` and the SMTP password. It is **not** in any backup: the backups are copied
offsite, and a secret in an offsite copy is a secret in one more place. Keep it in the firm's
password manager, and update the entry whenever a value in it changes.

If it is lost with the host, each secret can be replaced rather than recovered:

| Secret | To replace it |
|---|---|
| `POSTGRES_PASSWORD` | Choose a new one. The restore creates the database under it. |
| `GITHUB_WEBHOOK_SECRET` and the others | Generate one (`openssl rand -hex 32`), and set the same value in each repository's webhook settings on the code host. Deliveries signed with the old secret are refused in the meantime; the code host's webhook page can redeliver them once both sides agree. |
| `METRICS_TOKEN` | Generate one, and give it to the collector. |
| `MAIL_PASSWORD` | From the mail provider. |

The key ring is a secret too, and is handled above: it is backed up, apart.

## Infrastructure

Nothing about the host is special. It is Docker, the Compose plugin, a checkout of the
repository, `docker/.env`, the backup directories and the timer — all described in
[deployment.md](deployment.md) and [backups.md](backups.md), so a host is rebuilt from those
two documents and the password manager. There is no configuration held only on the machine.

What is outside the host and must be kept up separately: the DNS record for `ERP_DOMAIN`,
the webhook settings on each code host, the offsite backup stores and their credentials, and
the mail provider's account.

## What cannot be recovered

- **Anything written after the last backup that survives.** Up to a day, or more if the
  offsite copy had stopped.
- **Second factors, webhook secrets and signed links, without the keys backup.** See above.
- **Applicants' personal data that the retention had removed** is not *lost* by a restore —
  it comes back with an older backup, and the nightly retention sweep removes it again. It is
  worth knowing that, for a night, it is there.
- **Mail sent by the File transport** and the cache. Neither is backed up; neither matters.
