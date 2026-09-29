# Backups

What is backed up, where it goes, how to schedule it, how to tell it worked, and how to put
it back. Section 88 of the brief, which ends: *a backup that has never been tested for
restoration should not be considered reliable.* So the restore is tested — by the suite on
every push, and by you on the host once a month; both are described below.

What to do when something has actually gone wrong is in
[disaster-recovery.md](disaster-recovery.md). The settings are in
[configuration.md](configuration.md#backups).

## What is backed up

| What | Volume | In the backup as | Destination |
|---|---|---|---|
| The database — everything | `db-data` | `database.dump`, `pg_dump` custom format | `BACKUP_DIRECTORY` |
| Attachments | `documents` | `documents.tar.gz` | `BACKUP_DIRECTORY` |
| Applicants' CVs | `cvs` | `cvs.tar.gz` | `BACKUP_DIRECTORY` |
| The key ring | `keys` | `keys.tar.gz` | `KEYS_BACKUP_DIRECTORY` — **never the same place** |

Not backed up, on purpose:

| Volume | Why not |
|---|---|
| `cache-data` | A cache. The application computes every answer without it. |
| `mail` | What the File transport "sent" — nothing that reached anybody. |
| `caddy-data` | The certificate is issued again on first start. Let's Encrypt allows five duplicate certificates a week, so this only matters if the proxy is rebuilt several times in one week; copy the volume by hand before experimenting. |
| `docker/.env` | Secrets. It belongs in the firm's password manager, not in a backup that is copied offsite. See *Secrets* in [disaster-recovery.md](disaster-recovery.md#secrets). |

Each run writes one directory in each destination, named for the moment it started, in UTC:

```
/var/backups/jiranisokotech/data/20260929T021500Z/
    database.dump  documents.tar.gz  cvs.tar.gz  manifest  SHA256SUMS
/var/backups/jiranisokotech-keys/20260929T021500Z/
    keys.tar.gz  manifest  SHA256SUMS
```

A run that fails leaves nothing behind in either place. It writes into a hidden
`.<time>.partial` directory and renames it only after verifying it, so anything with a
timestamp for a name is a complete backup.

## The keys go somewhere else

The key ring decrypts every account's authenticator key and recovery codes, and the secrets
of the outbound webhook subscriptions. The database holds them encrypted. **Anybody holding
a copy of both can read every second factor in the firm**, which is the thing the encryption
exists to prevent — so the two are backed up to different directories, and copied off the
host to different places: a different storage account, a different provider, or at the very
least a different set of credentials.

The backup refuses to run if `BACKUP_DIRECTORY` and `KEYS_BACKUP_DIRECTORY` are the same
directory or one is inside the other. It checks by writing a marker into each and looking
for it in the other, so two mount points onto one host directory are caught too.

And keep the keys backup: **without it the database backup restores every account with its
second factor switched on and unusable.** Each such account then has to have it cleared by
an administrator. `BackupRestoreTests` shows both halves — a restore with the keys reads the
authenticator key back, and one without reads nothing.

## Running it

From `docker/`:

```sh
docker compose run --rm backup
```

The `backup` service is the database's own image, so its `pg_dump` is always the same major
version as the server — `pg_dump` refuses a server newer than itself, and a separate client
image would start failing every night the day the database was upgraded. It mounts the
volumes read-only, runs `docker/backup.sh`, and exits. **Its exit status is the report:** 0
means a complete, verified backup exists; anything else means none was written, and the last
line of its output says which step failed and why.

The first time, create the two directories so that they belong to root and nobody else:

```sh
sudo install -d -m 700 /var/backups/jiranisokotech/data /var/backups/jiranisokotech-keys
```

The files inside are written readable by their owner only. The dump holds salaries and
personal details, and the CVs archive is other people's documents.

## Scheduling it

Nightly, at a quiet hour. `pg_dump` takes a consistent snapshot without stopping anything,
so the application stays up while it runs.

### With a systemd timer — preferred

A timer can run something when the job fails, which cron cannot do without help, and a
backup failure nobody hears about is the failure this whole section exists to prevent.

`/etc/systemd/system/jiranisokotech-backup.service`:

```ini
[Unit]
Description=Back up the Jiranisoko Tech delivery system
OnFailure=jiranisokotech-backup-failed.service

[Service]
Type=oneshot
WorkingDirectory=/opt/jiranitecherp/docker
ExecStart=/usr/bin/docker compose run --rm backup
```

`/etc/systemd/system/jiranisokotech-backup.timer`:

```ini
[Unit]
Description=Nightly backup of the Jiranisoko Tech delivery system

[Timer]
OnCalendar=*-*-* 02:15:00
Persistent=true
RandomizedDelaySec=10m

[Install]
WantedBy=timers.target
```

`Persistent=true` runs a backup that was missed while the host was off as soon as it is back.

`/etc/systemd/system/jiranisokotech-backup-failed.service` is whatever reaches a person —
an email through the host's mailer, a message to a chat webhook:

```ini
[Unit]
Description=Tell somebody the backup failed

[Service]
Type=oneshot
ExecStart=/bin/sh -c 'journalctl -u jiranisokotech-backup --since -1h --no-pager | mail -s "ERP backup FAILED" delivery@jiranisokotech.co.ke'
```

Then:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now jiranisokotech-backup.timer
systemctl list-timers jiranisokotech-backup.timer     # when it next runs
journalctl -u jiranisokotech-backup                    # what it said last time
```

### With cron

```cron
MAILTO=delivery@jiranisokotech.co.ke
15 2 * * * cd /opt/jiranitecherp/docker && docker compose run --rm backup > /var/log/jiranisokotech-backup.log 2>&1 || cat /var/log/jiranisokotech-backup.log
```

cron mails whatever a job prints, and this prints only on failure. That depends on the host
being able to send mail; check that it can before relying on it.

### Before every update

The only way back from a migration is the backup taken before it ran
([deployment.md](deployment.md#updating)). Run one by hand before `docker compose up --build`.

## Copying it off the host

A backup on the host it protects does not survive losing the host. After each run, copy
each destination to its own offsite place — for example with `rclone`, which can encrypt on
the way:

```sh
rclone sync /var/backups/jiranisokotech/data   erp-data-crypt:  --max-age 48h
rclone sync /var/backups/jiranisokotech-keys   erp-keys-crypt:  --max-age 48h
```

`erp-data-crypt` and `erp-keys-crypt` should be two remotes with **different credentials**,
so that whoever or whatever holds one cannot read the other. Add these lines to the service
above as further `ExecStartPost=` lines, so a failed copy fails the unit and is reported the
same way. Encrypting the data remote matters: the dump holds everything the firm knows about
its people.

## How long they are kept

`BACKUP_RETENTION_DAYS`, 30 by default. After each **successful** run, backups whose names
are older than that are removed from both destinations; a failed run removes nothing, so a
job that has been failing for weeks has not also been deleting the last good backup. Only
directories named like a backup are touched. `0` keeps everything.

The offsite copies are governed by the offsite store, not by this setting. A useful shape is
thirty dailies on the host and twelve monthlies offsite, which answers both "restore
yesterday" and "what did this record say in March".

Restoring an old database brings back everything the application's own retention had since
removed — applicants past their period, withdrawn accounts. [privacy.md](privacy.md) says
how long those are kept, and the retention sweep removes them again on the first night after
a restore.

## Verifying a backup

Each run verifies what it wrote before calling it a backup: the checksums in `SHA256SUMS`
match the files, `pg_restore --list` reads the dump's whole table of contents, and each
archive reads to its end, which is where gzip checks its own CRC. To check a backup later —
say, one just copied back from offsite storage:

```sh
docker compose run --rm restore --verify-only \
    /backups/data/20260929T021500Z /backups/keys/20260929T021500Z
```

`/backups/data` and `/backups/keys` are where the restore service sees `BACKUP_DIRECTORY` and
`KEYS_BACKUP_DIRECTORY`. Nothing is changed.

Verification proves the files are the files that were written. It does not prove they
restore. Two things do:

**The suite, on every push.** `BackupRestoreTests` runs `docker/backup.sh` and
`docker/restore.sh` — the scripts the host runs — against PostgreSQL: it writes rows through
the application's own migrations and model, backs up, restores into a new database, and
compares every row of every table and every sequence. It then starts the application on the
restored database and key ring, checks it reports ready, and reads an account's
authenticator key back through the restored keys. It also checks that a restore refuses a
database or directory that already holds something, refuses a backup whose checksums do not
match, and that a failed backup leaves nothing behind.

**A drill, once a month, on the host.** Restore last night's backup into a scratch database
beside the live one and look at it:

```sh
cd docker
docker compose run --rm -e PGDATABASE=restore_drill restore \
    --only database /backups/data/<latest>
docker compose exec db psql -U jiranisokotech -d restore_drill \
    -c 'SELECT count(*) FROM clients' -c 'SELECT max("OccurredAt") FROM audit_entries'
docker compose exec db dropdb -U jiranisokotech restore_drill
```

The count should match the live system, and the newest audit entry should be from the
evening before. Note how long the restore took: that is the database's share of the recovery
time in [disaster-recovery.md](disaster-recovery.md#how-long-and-how-much).

## Restoring

`docker compose run --rm restore [--overwrite] [--only PARTS] BACKUP [KEYS_BACKUP]`, with the
application stopped. It checks every checksum first, then refuses to write into a database
or directory that already holds anything unless `--overwrite` is given — in which case the
database is dropped and created afresh, not merged into. The database restore is one
transaction: it ends with the whole backup or an empty database, never half.

For `KEYS_BACKUP`, use the **newest** keys backup, not the one with the same timestamp. The
key ring only gains keys — old ones are kept so that what they encrypted stays readable — so
the newest copy decrypts every older database, and an older copy may lack the key a newer
database was written with.

The procedures for each kind of loss — the host, the database, the keys, a bad update — are
in [disaster-recovery.md](disaster-recovery.md).
