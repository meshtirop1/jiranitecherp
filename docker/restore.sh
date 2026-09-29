#!/bin/sh
# Puts a backup written by backup.sh back. docs/disaster-recovery.md says when, and in what
# order; this is the mechanism.
#
#     restore.sh [--overwrite] [--only PARTS] [--verify-only] BACKUP [KEYS_BACKUP]
#
#     BACKUP        one timestamped directory under BACKUP_TO, e.g. /backups/data/20260929T021500Z
#     KEYS_BACKUP   one timestamped directory under KEYS_BACKUP_TO — usually the NEWEST, not
#                   the one with the same name: the key ring only ever gains keys, so the
#                   latest copy decrypts every older database, and an older copy may lack the
#                   key a newer database was written with
#     --only        a comma-separated subset of database,documents,cvs,keys; by default every
#                   part the arguments provide
#     --overwrite   restore over a database or directory that already holds something. The
#                   database is dropped and created afresh, not merged into
#     --verify-only check the checksums and that every archive reads back, and stop
#
# On a host, with the application stopped so that nothing writes while this does:
#
#     cd docker
#     docker compose stop web
#     docker compose run --rm restore /backups/data/20260929T021500Z /backups/keys/20260930T021500Z
#     docker compose up -d
#
# Settings, as environment variables, which compose sets for the restore service:
#
#     PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE   the server, and the database to create
#     MAINTENANCE_DATABASE   where to connect to create or drop PGDATABASE (default postgres)
#     DOCUMENTS_TO CVS_TO KEYS_TO                  the directories to fill
#                                                  (default /documents, /cvs, /keys)
#
# Nothing is touched until every checksum has passed and every target has been found empty
# or --overwrite has been given; a restore that fails halfway through replacing the live
# system is a second disaster on top of the first.

set -eu

say() { printf 'restore: %s\n' "$*" >&2; }
fail() { printf 'restore FAILED: %s\n' "$*" >&2; exit 1; }

overwrite=
verify_only=
only=
while [ $# -gt 0 ]; do
    case $1 in
        --overwrite) overwrite=yes ;;
        --verify-only) verify_only=yes ;;
        --only) [ $# -ge 2 ] || fail "--only needs a list, e.g. --only database,keys"; only=$2; shift ;;
        --only=*) only=${1#--only=} ;;
        --) shift; break ;;
        -*) fail "unknown option $1" ;;
        *) break ;;
    esac
    shift
done

[ $# -ge 1 ] && [ $# -le 2 ] || fail "usage: restore.sh [--overwrite] [--only PARTS] [--verify-only] BACKUP [KEYS_BACKUP]"

backup=$1
keys_backup=${2:-}

DOCUMENTS_TO=${DOCUMENTS_TO:-/documents}
CVS_TO=${CVS_TO:-/cvs}
KEYS_TO=${KEYS_TO:-/keys}
MAINTENANCE_DATABASE=${MAINTENANCE_DATABASE:-postgres}

[ -d "$backup" ] || fail "$backup is not a directory."
[ -f "$backup/SHA256SUMS" ] || fail "$backup has no SHA256SUMS; it is not a complete backup from backup.sh."
if [ -n "$keys_backup" ]; then
    [ -d "$keys_backup" ] || fail "$keys_backup is not a directory."
    [ -f "$keys_backup/SHA256SUMS" ] || fail "$keys_backup has no SHA256SUMS; it is not a complete keys backup from backup.sh."
    [ -f "$keys_backup/keys.tar.gz" ] || fail "$keys_backup holds no keys.tar.gz. Is it the data backup given twice?"
fi

if [ -z "$only" ]; then
    only=database,documents,cvs
    [ -z "$keys_backup" ] || only=$only,keys
fi

wants() {
    case ",$only," in *",$1,"*) return 0 ;; *) return 1 ;; esac
}

for part in $(printf '%s' "$only" | tr ',' ' '); do
    case $part in
        database|documents|cvs|keys) ;;
        *) fail "--only knows database, documents, cvs and keys, not '$part'." ;;
    esac
done

if wants keys && [ -z "$keys_backup" ]; then
    fail "the keys are to be restored but no keys backup was given. It is the second argument, from KEYS_BACKUP_TO."
fi

# Every checksum first, before anything is looked at, let alone replaced. A backup copied
# back from offsite storage can arrive truncated, and restoring half a dump over a database
# is worse than restoring nothing.
say "checking $backup"
( cd "$backup" && sha256sum -c SHA256SUMS ) || fail "$backup does not match its checksums. It is damaged or incomplete; use another."
if [ -n "$keys_backup" ]; then
    say "checking $keys_backup"
    ( cd "$keys_backup" && sha256sum -c SHA256SUMS ) || fail "$keys_backup does not match its checksums. It is damaged or incomplete; use another."
fi

pg_restore --list "$backup/database.dump" >/dev/null || fail "$backup/database.dump cannot be read."
for archive in "$backup/documents.tar.gz" "$backup/cvs.tar.gz" ${keys_backup:+"$keys_backup/keys.tar.gz"}; do
    tar -tzf "$archive" >/dev/null || fail "$archive cannot be read."
done

if [ -n "$verify_only" ]; then
    say "the backup is intact. Nothing was restored (--verify-only)."
    exit 0
fi

# --- is every target safe to write? ------------------------------------------------------

sql() {
    # Through psql's variables rather than pasted into the text, so a database name holding a
    # quote cannot become part of the statement.
    psql --no-psqlrc --quiet --tuples-only --no-align --set ON_ERROR_STOP=1 "$@"
}

database_state() {
    # "absent", "empty", or "holds N relations". Empty means nothing in any schema but the
    # system's own — the state createdb leaves a database in.
    exists=$(printf "SELECT 1 FROM pg_database WHERE datname = :'target';\n" \
        | sql --dbname="$MAINTENANCE_DATABASE" --set target="$PGDATABASE") \
        || fail "could not reach the server to ask whether $PGDATABASE exists."
    if [ -z "$exists" ]; then
        echo absent
        return
    fi
    relations=$(sql --dbname="$PGDATABASE" --command "
        SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%';") \
        || fail "could not look inside $PGDATABASE."
    if [ "$relations" = 0 ]; then echo empty; else echo "holds $relations relations"; fi
}

directory_is_empty() { [ -z "$(ls -A "$1")" ]; }

if wants database; then
    [ -n "${PGDATABASE:-}" ] || fail "PGDATABASE is not set; there is no database to restore into."
    state=$(database_state)
    case $state in
        absent|empty) ;;
        *) [ -n "$overwrite" ] || fail "database $PGDATABASE is not empty ($state). Restore into a new database name, or pass --overwrite to drop and replace it." ;;
    esac
fi

for part in documents cvs keys; do
    wants "$part" || continue
    case $part in
        documents) target=$DOCUMENTS_TO ;;
        cvs) target=$CVS_TO ;;
        keys) target=$KEYS_TO ;;
    esac
    [ -d "$target" ] || fail "$target does not exist. Is the volume mounted?"
    if ! directory_is_empty "$target" && [ -z "$overwrite" ]; then
        fail "$target is not empty. Restore into an empty directory, or pass --overwrite to replace what is there."
    fi
done

# --- restore ------------------------------------------------------------------------------

if wants database; then
    if [ "$state" != absent ] && [ "$state" != empty ]; then
        say "dropping $PGDATABASE ($state), as --overwrite asked"
        # Dropped and created rather than restored over with --clean: --clean removes only
        # what the dump contains, so a table a later migration added would survive into a
        # database whose migration history says it was never created.
        dropdb --maintenance-db="$MAINTENANCE_DATABASE" --force "$PGDATABASE" \
            || fail "could not drop $PGDATABASE. Nothing has been restored."
        state=absent
    fi
    if [ "$state" = absent ]; then
        createdb --maintenance-db="$MAINTENANCE_DATABASE" "$PGDATABASE" \
            || fail "could not create $PGDATABASE. Nothing has been restored."
    fi

    say "restoring the database into $PGDATABASE"
    # One transaction, stopping at the first error, so the result is the whole backup or an
    # empty database — never a schema with half its rows, which starts, serves pages and
    # is wrong. No owner and no grants, so the dump restores under whichever user this is:
    # the host being rebuilt may not have the role the old one had.
    pg_restore --dbname="$PGDATABASE" --no-owner --no-acl --single-transaction --exit-on-error \
        "$backup/database.dump" \
        || fail "pg_restore failed; $PGDATABASE has been left empty. Nothing else has been restored."
fi

owner_from() {
    # The owner backup.sh recorded for the source directory, if it did.
    awk -v part="$2" '$1 == "owner" && $2 == part { print $3 }' "$1/manifest" 2>/dev/null || true
}

restore_files() {
    part=$1 archive=$2 target=$3 manifest_dir=$4
    say "restoring $part into $target"
    if ! directory_is_empty "$target"; then
        find "$target" -mindepth 1 -maxdepth 1 -exec rm -rf {} + || fail "could not empty $target."
    fi
    tar -xzf "$archive" -C "$target" || fail "could not unpack $archive into $target."

    # Handed back to the application's user when this runs as root, which it does in the
    # container. A new volume is owned by whoever mounts it first, and that is this script:
    # without this the application starts on a restored system and cannot write a key, an
    # attachment or a CV — the first form anybody opens fails.
    owner=$(owner_from "$manifest_dir" "$part")
    if [ -n "$owner" ] && [ "$(id -u)" = 0 ]; then
        chown -R "$owner" "$target" || fail "restored $target but could not give it to $owner."
    fi
}

wants documents && restore_files documents "$backup/documents.tar.gz" "$DOCUMENTS_TO" "$backup"
wants cvs && restore_files cvs "$backup/cvs.tar.gz" "$CVS_TO" "$backup"
wants keys && restore_files keys "$keys_backup/keys.tar.gz" "$KEYS_TO" "$keys_backup"

say "done: restored $only"
