#!/usr/bin/env bash
# UI review harness: screenshots of every page with realistic data, without
# touching the development database.
#
#   ./review.sh setup      clone recall_db twice, fetch extra titles, seed (seed.sql)
#   ./review.sh start      start three app instances on the clones
#   ./review.sh capture    run the Playwright capture (Capture/), writes screenshots/
#   ./review.sh import-progress on|off   add or remove an import that is still running (used by the capture)
#   ./review.sh stats-sparse on|off      give the empty clone's user a bulk-marked and imported history only, or take it away (used by the capture)
#   ./review.sh stop       stop the app instances
#   ./review.sh teardown   stop, drop the clones, clear the Redis database used
#   ./review.sh all        setup, start, capture, teardown
#
# Needs the local_postgres and my-redis containers from the README and TheTVDB
# keys in user secrets. "start" builds Recall.Web (Debug) first.
#
# The instances run with the import job switched off (Jobs:Disabled), so rows
# left waiting for a screenshot are never looked up on TheTVDB, and with the
# series refresh job off: the clones' series were cached before genres were
# kept, and the job would start re-fetching them ten seconds after each start. They apply
# pending migrations to the clones at startup (the clones are copies of
# recall_db, which may be a migration behind the code; recall_db itself is not
# touched). The weekly digest is switched on so its Profile switch, Dashboard
# offer and preview show, with its job off so nothing is queued.
#
# recall_db itself is only read (as the template for the clones). The apps use
# Redis database 1, so the development cache in database 0 is left alone too.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUN="$ROOT/ui-review/.run"
PG_CONTAINER=local_postgres
REDIS_CONTAINER=my-redis
REDIS_DB=1
SOURCE_DB=recall_db
SEEDED_DB=recall_uireview          # dev user with a full library
EMPTY_DB=recall_uireview_empty     # dev user with nothing: the empty states

SIGNED_IN_URL=http://localhost:7461   # Development: DevAuthMiddleware signs in the dev user
EMPTY_URL=http://localhost:7462       # same, on the empty clone
ANONYMOUS_URL=http://localhost:7463   # environment "Test": DevAuthMiddleware stands aside

# Titles the dev database has no cached aggregate for; opened once through the
# app so TheTVDB data for them lands in the clone. Short series on purpose:
# every uncached episode costs one translation call.
# Adolescence, Baby Reindeer, Mare of Easttown, Band of Brothers, The Penguin
EXTRA_SERIES=(452467 417223 370112 74205 417648)
# Heat, Interstellar, Dune: Part Two
EXTRA_MOVIES=(1305 131079 290272)

psql_db() { docker exec -i -e PGPASSWORD=devpassword "$PG_CONTAINER" psql -U postgres -v ON_ERROR_STOP=1 -q -At -d "$1" "${@:2}"; }

connection() { echo "Host=localhost;Port=5432;Database=$1;Username=postgres;Password=devpassword"; }

start_app() { # name url database environment [directory]
  mkdir -p "$RUN"
  ( cd "${5:-$ROOT/Recall.Web}"
    local dll=Recall.Web.dll
    [ -e "$dll" ] || dll=bin/Debug/net10.0/Recall.Web.dll
    ASPNETCORE_ENVIRONMENT="$4" \
    ASPNETCORE_URLS="$2" \
    ConnectionStrings__DefaultConnection="$(connection "$3")" \
    REDIS_CONNECTION="localhost:6379,defaultDatabase=$REDIS_DB" \
    Database__MigrateOnStartup=true \
    Jobs__Disabled__0=WatchlistImportTimer \
    Jobs__Disabled__1=WeeklyDigestTimer \
    Jobs__Disabled__2=UpdateTvDbInfoTimer \
    Digest__Enabled=true \
    Site__BaseUrl="$2" \
    nohup dotnet "$dll" > "$RUN/$1.log" 2>&1 &
    echo $! > "$RUN/$1.pid" )
  for _ in $(seq 1 60); do
    curl -fsS -o /dev/null "$2/health/live" 2>/dev/null && { echo "started $1 at $2"; return; }
    sleep 0.5
  done
  echo "$1 did not start; see $RUN/$1.log" >&2; exit 1
}

stop_apps() {
  for pidfile in "$RUN"/*.pid; do
    [ -e "$pidfile" ] || continue
    kill "$(cat "$pidfile")" 2>/dev/null || true
    rm -f "$pidfile"
  done
}

row_counts() { # database
  psql_db "$1" -c "
    SELECT string_agg(t || '=' || n, ' ' ORDER BY t) FROM (
      SELECT table_name AS t,
             (xpath('/row/c/text()', query_to_xml('SELECT count(*) AS c FROM ' || quote_ident(table_name), false, true, '')))[1]::text AS n
      FROM information_schema.tables WHERE table_schema = 'public') x;"
}

# Copies recall_db into a new database. The quick way (TEMPLATE) needs nobody
# else to be connected to recall_db; when someone is (a database client left
# open, the dev app running), it is copied with pg_dump instead, which only
# reads. Nobody's session is closed either way.
clone_source() { # database
  if psql_db postgres -c "CREATE DATABASE $1 TEMPLATE $SOURCE_DB;" 2> "$RUN/clone-error.txt"; then
    return
  fi
  if ! grep -q "is being accessed by other users" "$RUN/clone-error.txt"; then
    cat "$RUN/clone-error.txt" >&2; exit 1
  fi
  echo "$SOURCE_DB is in use by another session; copying it into $1 with pg_dump instead"
  psql_db postgres -c "CREATE DATABASE $1;"
  docker exec -e PGPASSWORD=devpassword "$PG_CONTAINER" pg_dump -U postgres --no-owner "$SOURCE_DB" \
    | docker exec -i -e PGPASSWORD=devpassword "$PG_CONTAINER" psql -U postgres -v ON_ERROR_STOP=1 -q -d "$1" > /dev/null
}

setup() {
  mkdir -p "$RUN"
  stop_apps   # instances from an earlier run would hold the ports and the clones
  row_counts "$SOURCE_DB" > "$RUN/source-counts-before.txt"
  echo "recall_db before: $(cat "$RUN/source-counts-before.txt")"

  for db in "$SEEDED_DB" "$EMPTY_DB"; do
    psql_db postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"
    clone_source "$db"
    # Nothing is stale, so the hourly refresh jobs make no TheTVDB calls of their own.
    psql_db "$db" -c "
      UPDATE cached_series_aggregate SET retrieved_utc = now();
      UPDATE cached_movie_aggregate SET retrieved_utc = now();
      UPDATE cached_series_extended SET retrieved_utc = now();
      UPDATE cached_episode_extended SET retrieved_utc = now();"
  done

  psql_db "$EMPTY_DB" -c "
    TRUNCATE tracked_series, tracked_movie, episode_watch, user_movie_watch, user_like, user_rating,
             notification, notified_episode, login_token, email, watchlist_import_item, watchlist_import_job;"

  # A second administrator in the empty clone, so the dev user is not the only
  # one there and "Delete my account" shows its confirmation form. In the seeded
  # clone the dev user is the only admin and gets the explanation instead.
  psql_db "$EMPTY_DB" -c "
    UPDATE app_user SET role = 'Admin' WHERE id = '11111111-1111-1111-1111-111111111111';
    INSERT INTO app_user (id, email, user_name, role, created_utc, updated_utc)
    VALUES (gen_random_uuid(), 'second-admin@example.com', 'second-admin', 'Admin', now(), now());"
  psql_db "$SEEDED_DB" -c "
    UPDATE app_user SET role = 'Admin' WHERE id = '11111111-1111-1111-1111-111111111111';"

  docker exec "$REDIS_CONTAINER" redis-cli -n "$REDIS_DB" FLUSHDB > /dev/null

  if [ ${#EXTRA_SERIES[@]} -gt 0 ] || [ ${#EXTRA_MOVIES[@]} -gt 0 ]; then
    start_app fetch "$SIGNED_IN_URL" "$SEEDED_DB" Development
    for id in "${EXTRA_SERIES[@]}"; do
      echo "fetching series $id: HTTP $(curl -s -o /dev/null -w '%{http_code}' -m 120 "$SIGNED_IN_URL/Series/Details/$id")"
    done
    for id in "${EXTRA_MOVIES[@]}"; do
      echo "fetching movie $id: HTTP $(curl -s -o /dev/null -w '%{http_code}' -m 120 "$SIGNED_IN_URL/Movies/Details/$id")"
    done
    stop_apps
  fi

  psql_db "$SEEDED_DB" < "$ROOT/ui-review/seed.sql"

  # The seed edits cached payloads; drop the Redis copies so the apps read them.
  docker exec "$REDIS_CONTAINER" redis-cli -n "$REDIS_DB" FLUSHDB > /dev/null
  echo "setup done"
}

start() {
  dotnet build "$ROOT/Recall.Web" -c Debug --nologo -v quiet > /dev/null
  start_app signed-in "$SIGNED_IN_URL" "$SEEDED_DB" Development
  start_app empty "$EMPTY_URL" "$EMPTY_DB" Development

  # Signed out: DevAuthMiddleware only stands aside in the "Test" environment,
  # and outside Development the static files (fingerprinted CSS and JS) are
  # only served from published output. So this instance runs a Debug publish.
  # Starting it fresh also resets the limit on requests for a sign-in link
  # (8 POSTs per 5 minutes per IP; page loads are not counted).
  dotnet publish "$ROOT/Recall.Web" -c Debug -o "$RUN/publish" --nologo -v quiet
  start_app anonymous "$ANONYMOUS_URL" "$SEEDED_DB" Test "$RUN/publish"
}

capture() {
  dotnet run --project "$ROOT/ui-review/Capture" -- \
    --signed-in "$SIGNED_IN_URL" --empty "$EMPTY_URL" --anonymous "$ANONYMOUS_URL" \
    --out "$ROOT/ui-review/screenshots" --review-script "$ROOT/ui-review/review.sh" "$@"
}

teardown() {
  stop_apps
  sleep 1
  for db in "$SEEDED_DB" "$EMPTY_DB"; do
    psql_db postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"
  done
  docker exec "$REDIS_CONTAINER" redis-cli -n "$REDIS_DB" FLUSHDB > /dev/null
  row_counts "$SOURCE_DB" > "$RUN/source-counts-after.txt"
  echo "recall_db after:  $(cat "$RUN/source-counts-after.txt")"
  if diff -q "$RUN/source-counts-before.txt" "$RUN/source-counts-after.txt" > /dev/null; then
    echo "recall_db row counts are unchanged"
  else
    echo "recall_db row counts CHANGED" >&2; exit 1
  fi
}

# An import that is still running, put in place for one screenshot and taken
# away again (the capture calls this; see import-in-progress.sql for why it is
# not part of the seed).
import_progress() { # on|off
  local job="cccccccc-0000-4000-8000-000000000001"
  psql_db "$SEEDED_DB" -c "
    DELETE FROM watchlist_import_item WHERE job_id = '$job';
    DELETE FROM watchlist_import_job WHERE id = '$job';"
  if [ "${1:-}" = "on" ]; then
    psql_db "$SEEDED_DB" < "$ROOT/ui-review/import-in-progress.sql"
  fi
}

# A history that was only ever marked in bulk or imported, for the Stats page's
# sparse state: put in the empty clone for one screenshot and taken away again,
# so the other empty-state shots still find a user with nothing.
stats_sparse() { # on|off
  psql_db "$EMPTY_DB" -c "
    DELETE FROM episode_watch WHERE user_id = '11111111-1111-1111-1111-111111111111';
    DELETE FROM user_movie_watch WHERE user_id = '11111111-1111-1111-1111-111111111111';
    DELETE FROM tracked_series WHERE user_id = '11111111-1111-1111-1111-111111111111';"
  if [ "${1:-}" = "on" ]; then
    psql_db "$EMPTY_DB" < "$ROOT/ui-review/stats-sparse.sql"
  fi
}

case "${1:-}" in
  setup) setup ;;
  import-progress) import_progress "${2:-off}" ;;
  stats-sparse) stats_sparse "${2:-off}" ;;
  start) start ;;
  capture) shift; capture "$@" ;;
  stop) stop_apps ;;
  teardown) teardown ;;
  all) setup; start; capture; teardown ;;
  *) sed -n '2,21p' "$0"; exit 1 ;;
esac
