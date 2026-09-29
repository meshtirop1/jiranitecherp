#!/bin/sh
# A backup of everything this application cannot regenerate: the database, the attachments,
# the CVs, and — somewhere else — the key ring. Section 88 of the brief; docs/backups.md is
# how to schedule it and docs/disaster-recovery.md is what to do with what it writes.
#
# On a host this runs inside the `backup` service in compose.yaml, which is the database's
# own image — so pg_dump is the same major version as the server it reads, and a dump taken
# by a newer server is never handed to an older client that refuses it:
#
#     cd docker && docker compose run --rm backup
#
# It is plain POSIX sh, not bash, because the postgres alpine image's shell is busybox. The
# test suite runs the same file under dash against a real PostgreSQL (BackupRestoreTests).
#
# What it writes, per run, named for the moment it started in UTC:
#
#     $BACKUP_TO/20260929T021500Z/        database.dump documents.tar.gz cvs.tar.gz
#                                          manifest SHA256SUMS
#     $KEYS_BACKUP_TO/20260929T021500Z/   keys.tar.gz manifest SHA256SUMS
#
# Settings, all environment variables (compose sets every one of them):
#
#     PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE   the database, as libpq reads them
#     BACKUP_TO         where the database and files go
#     KEYS_BACKUP_TO    where the key ring goes; must not be BACKUP_TO or inside it
#     DOCUMENTS_FROM    the attachments directory             (default /documents)
#     CVS_FROM          the CVs directory                     (default /cvs)
#     KEYS_FROM         the key ring directory                (default /keys)
#     RETENTION_DAYS    backups older than this are removed   (default 30; 0 keeps everything)
#
# Every failure ends the run with a non-zero exit and a line on stderr saying which step
# failed. That is the whole point of the file's shape: a backup job that fails quietly is
# discovered on the day somebody needs the backup, which is the one day it cannot be fixed.

set -eu

# Readable by the owner only. The dump holds every salary, next of kin and personal detail the
# firm has recorded, and the CVs archive is other people's documents; created with the
# ordinary umask they would be readable by every account on the host.
umask 077

say() { printf 'backup: %s\n' "$*" >&2; }
fail() { printf 'backup FAILED: %s\n' "$*" >&2; exit 1; }

BACKUP_TO=${BACKUP_TO:-}
KEYS_BACKUP_TO=${KEYS_BACKUP_TO:-}
DOCUMENTS_FROM=${DOCUMENTS_FROM:-/documents}
CVS_FROM=${CVS_FROM:-/cvs}
KEYS_FROM=${KEYS_FROM:-/keys}
RETENTION_DAYS=${RETENTION_DAYS:-30}

[ -n "$BACKUP_TO" ] || fail "BACKUP_TO is not set; there is nowhere to write the backup."
[ -n "$KEYS_BACKUP_TO" ] || fail "KEYS_BACKUP_TO is not set. The key ring is backed up apart from the database, on purpose, and never into the same place; see docs/backups.md."
[ -n "${PGDATABASE:-}" ] || fail "PGDATABASE is not set; there is no database to dump."

case $RETENTION_DAYS in
    ''|*[!0-9]*) fail "RETENTION_DAYS must be a whole number of days, not '$RETENTION_DAYS'." ;;
esac

for tool in pg_dump pg_restore tar gzip sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool is not installed here."
done

# A source directory that is missing is a volume that was not mounted, and tarring the
# empty directory the container has at that path would produce a perfectly valid archive
# of nothing. An empty documents directory is legitimate on a new system; a missing one
# never is.
for source in "$DOCUMENTS_FROM" "$CVS_FROM" "$KEYS_FROM"; do
    [ -d "$source" ] || fail "$source does not exist. Is the volume mounted?"
done

# The key ring is never empty once the application has started even once — the first form
# anybody opens creates a key. An empty one here means the wrong thing is mounted, and a
# backup of it would look exactly like a backup until the day it was restored.
if [ -z "$(ls -A "$KEYS_FROM")" ]; then
    fail "$KEYS_FROM is empty. The application writes a key the first time it starts, so this is the wrong directory, or the application has never run."
fi

mkdir -p "$BACKUP_TO" "$KEYS_BACKUP_TO" || fail "could not create the backup directories."

# The two destinations must really be two places. Comparing the paths is not enough: in a
# container they arrive as two different mount points, /backups/data and /backups/keys, and
# the host directories behind them can be one directory — or one inside the other, where a
# copy of the data backup made offsite takes the keys with it. So a marker is written in each
# and looked for in the other, which catches every one of those whatever the paths say.
marker=".apart-$$-$(date +%s)"
: > "$KEYS_BACKUP_TO/$marker" || fail "could not write to $KEYS_BACKUP_TO."
: > "$BACKUP_TO/$marker.data" || { rm -f "$KEYS_BACKUP_TO/$marker"; fail "could not write to $BACKUP_TO."; }
together=
if [ -n "$(find "$BACKUP_TO" -name "$marker" 2>/dev/null)" ] || [ -n "$(find "$KEYS_BACKUP_TO" -name "$marker.data" 2>/dev/null)" ]; then
    together=yes
fi
rm -f "$KEYS_BACKUP_TO/$marker" "$BACKUP_TO/$marker.data"
[ -z "$together" ] || fail "KEYS_BACKUP_TO and BACKUP_TO are the same place, or one is inside the other. The key ring decrypts every second factor and webhook secret in the database; a copy of both together undoes that encryption. Give the keys a destination of their own."

stamp=$(date -u +%Y%m%dT%H%M%SZ)
work="$BACKUP_TO/.$stamp.partial"
keys_work="$KEYS_BACKUP_TO/.$stamp.partial"

# Everything is written under a name beginning with a dot and ending .partial, and renamed
# only once it has been verified. So a run that dies halfway — a full disk, a killed
# container, a database that went away — leaves nothing that the retention below, the
# restore script, or a person looking for "the latest backup" could mistake for a complete
# one. The trap removes the half-written directories on every way out but success.
finished=
cleanup() {
    if [ -z "$finished" ]; then
        rm -rf "$work" "$keys_work"
    fi
}
trap cleanup EXIT
trap 'exit 1' INT TERM HUP

mkdir "$work" || fail "$work already exists; is another backup running?"
mkdir "$keys_work" || fail "$keys_work already exists; is another backup running?"

say "dumping database $PGDATABASE on ${PGHOST:-the local socket}"
# The custom format, because it is compressed, and because pg_restore can list it — which
# is how the verification below reads every entry without restoring it — and can restore it
# into a database of another name. No --clean: the restore script creates a fresh database
# instead, and a dump carrying DROP statements is one mistyped target away from emptying
# the live one.
pg_dump --format=custom --file="$work/database.dump" \
    || fail "pg_dump could not dump $PGDATABASE; see its message above."

say "archiving $DOCUMENTS_FROM"
tar -czf "$work/documents.tar.gz" -C "$DOCUMENTS_FROM" . || fail "could not archive $DOCUMENTS_FROM."

say "archiving $CVS_FROM"
tar -czf "$work/cvs.tar.gz" -C "$CVS_FROM" . || fail "could not archive $CVS_FROM."

say "archiving $KEYS_FROM, apart from the rest, into $KEYS_BACKUP_TO"
tar -czf "$keys_work/keys.tar.gz" -C "$KEYS_FROM" . || fail "could not archive $KEYS_FROM."

# The owner of each source directory, recorded so that a restore into a brand-new volume can
# hand the directory back to the application's user. A new named volume takes its ownership
# from whichever container mounts it first, and if that is the restore — which runs as root —
# the application starts, cannot write an attachment or a key, and fails on the first form.
owner_of() { stat -c '%u:%g' "$1"; }

{
    echo "created $stamp"
    echo "database $PGDATABASE"
    echo "pg_dump $(pg_dump --version)"
    echo "owner documents $(owner_of "$DOCUMENTS_FROM")"
    echo "owner cvs $(owner_of "$CVS_FROM")"
} > "$work/manifest" || fail "could not write the manifest."

{
    echo "created $stamp"
    echo "owner keys $(owner_of "$KEYS_FROM")"
} > "$keys_work/manifest" || fail "could not write the keys manifest."

( cd "$work" && sha256sum database.dump documents.tar.gz cvs.tar.gz manifest > SHA256SUMS ) \
    || fail "could not checksum the backup."
( cd "$keys_work" && sha256sum keys.tar.gz manifest > SHA256SUMS ) \
    || fail "could not checksum the keys backup."

# Verified before it is called a backup. The checksums prove the files on disk are the files
# that were written; pg_restore --list reads the dump's whole table of contents, which a
# truncated or corrupt dump fails; and tar -t reads each archive to its end, which is where
# gzip checks its own CRC. None of this proves the database restores — the tested restore in
# the suite, and the drill in docs/backups.md, are what prove that.
say "verifying"
( cd "$work" && sha256sum -c SHA256SUMS >/dev/null ) || fail "the backup's checksums do not match what was just written."
( cd "$keys_work" && sha256sum -c SHA256SUMS >/dev/null ) || fail "the keys backup's checksums do not match what was just written."
pg_restore --list "$work/database.dump" >/dev/null || fail "the database dump cannot be read back."
for archive in "$work/documents.tar.gz" "$work/cvs.tar.gz" "$keys_work/keys.tar.gz"; do
    tar -tzf "$archive" >/dev/null || fail "$archive cannot be read back."
done

mv "$keys_work" "$KEYS_BACKUP_TO/$stamp" || fail "could not put the keys backup in place."
mv "$work" "$BACKUP_TO/$stamp" || fail "could not put the backup in place."
finished=yes

say "wrote $BACKUP_TO/$stamp and $KEYS_BACKUP_TO/$stamp"

# Retention, only after a backup has succeeded — so a job that has been failing for a month
# does not also delete the last good backup while it fails. The age is read from the name
# rather than the directory's modification time, which a copy or a restore of the backup
# directory resets. Only names this script writes are touched; anything else an operator
# keeps in the directory is left alone.
if [ "$RETENTION_DAYS" -gt 0 ]; then
    # As a number, YYYYMMDDHHMMSS, because test's string ordering is not POSIX and dash and
    # busybox disagree about it; fourteen digits fit the shell's arithmetic comfortably.
    cutoff=$(date -u -d "@$(( $(date +%s) - RETENTION_DAYS * 86400 ))" +%Y%m%d%H%M%S) \
        || fail "the backup is written, but the retention cut-off could not be worked out, so nothing old was removed."

    for destination in "$BACKUP_TO" "$KEYS_BACKUP_TO"; do
        for old in "$destination"/[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T[0-9][0-9][0-9][0-9][0-9][0-9]Z; do
            [ -d "$old" ] || continue
            name=$(basename "$old")
            if [ "$(printf '%s' "$name" | tr -d TZ)" -lt "$cutoff" ] && [ "$name" != "$stamp" ]; then
                say "removing $old, older than $RETENTION_DAYS days"
                rm -rf "$old" || fail "the backup is written, but $old could not be removed."
            fi
        done

        # A run killed outright — SIGKILL, the host losing power — cannot run its trap, and
        # leaves its .partial directory behind. One more than a day old is not in progress.
        find "$destination" -maxdepth 1 -name '.*.partial' -type d -mtime +0 -exec rm -rf {} + 2>/dev/null || true
    done
fi

say "done"
