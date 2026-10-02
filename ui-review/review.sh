#!/usr/bin/env bash
# UI review harness: screenshots of every page with realistic data, without
# touching the development database.
#
#   ./review.sh setup      clone recall_db twice, fetch extra titles, seed (seed.sql)
#   ./review.sh start      start three app instances on the clones
#   ./review.sh capture    run the Playwright capture (Capture/), writes screenshots/
#   ./review.sh stop       stop the app instances
#   ./review.sh teardown   stop, drop the clones, clear the Redis database used
#   ./review.sh all        setup, start, capture, teardown
#
# Needs the local_postgres and my-redis containers from the README and TheTVDB
# keys in user secrets. "start" builds Recall.Web (Debug) first.
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
    Database__MigrateOnStartup=false \
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

setup() {
  mkdir -p "$RUN"
  stop_apps   # instances from an earlier run would hold the ports and the clones
  row_counts "$SOURCE_DB" > "$RUN/source-counts-before.txt"
  echo "recall_db before: $(cat "$RUN/source-counts-before.txt")"

  for db in "$SEEDED_DB" "$EMPTY_DB"; do
    psql_db postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);"
    psql_db postgres -c "CREATE DATABASE $db TEMPLATE $SOURCE_DB;"
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
  # Starting it fresh also resets the sign-in page's rate limit (8 requests per
  # 5 minutes per IP), which one capture run uses up exactly.
  dotnet publish "$ROOT/Recall.Web" -c Debug -o "$RUN/publish" --nologo -v quiet
  start_app anonymous "$ANONYMOUS_URL" "$SEEDED_DB" Test "$RUN/publish"
}

capture() {
  dotnet run --project "$ROOT/ui-review/Capture" -- \
    --signed-in "$SIGNED_IN_URL" --empty "$EMPTY_URL" --anonymous "$ANONYMOUS_URL" \
    --out "$ROOT/ui-review/screenshots" "$@"
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

case "${1:-}" in
  setup) setup ;;
  start) start ;;
  capture) shift; capture "$@" ;;
  stop) stop_apps ;;
  teardown) teardown ;;
  all) setup; start; capture; teardown ;;
  *) sed -n '2,16p' "$0"; exit 1 ;;
esac
