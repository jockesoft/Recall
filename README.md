
Initialize secrets for your web project
```
dotnet user-secrets init --project ./Recall.Web
```

Set your TheTVDB values
```
dotnet user-secrets set "TheTvDb:ApiKey" "YOUR_REAL_API_KEY" --project ./Recall.Web
dotnet user-secrets set "TheTvDb:Pin" "YOUR_PIN_IF_ANY" --project ./Recall.Web
```

Set your OMDb values
```
dotnet user-secrets set "Omdb:ApiKey" "YOUR_REAL_API_KEY" --project Recall.Web
```

Set your allowed email addresses
```
dotnet user-secrets set "Login:AllowedEmails:0" "dev@email.com" --project Recall.Web
dotnet user-secrets set "Login:AllowedEmails:1" "user@email.com" --project Recall.Web
```

Start the cache container (Valkey, from Docker Hardened Images)

The image is on `dhi.io`, which needs a Docker Hub login once per machine (`docker login dhi.io`, with your
Docker Hub username and an access token). The PostgreSQL test suite pulls the same image.
```
docker run --name my-redis -p 127.0.0.1:6380:6379 -d dhi.io/valkey:9.1 \
  --protected-mode no --save "" --appendonly no
```
The container keeps the name `my-redis`, which `ui-review/review.sh` uses. It is published on host port **6380**, not
Valkey's usual 6379, which another project on the development machine holds; the development connection string
(`ConnectionStrings:RedisConnection` in `appsettings.json`, `localhost:6380`) and `review.sh`'s default `REDIS_PORT`
match it. Locally it has no password, so the port
is bound to 127.0.0.1 only; `--protected-mode no` is needed because the app reaches it through Docker's port
forwarding, which Valkey does not count as loopback (with protected mode on it refuses the app, and the app quietly
reads everything from Postgres instead). To replace an old Redis container: `docker rm -f my-redis`, then the
command above; it only ever held cache.

Start the Postgres container (PostgreSQL 18, from Docker Hardened Images, the same image as production and the
PostgreSQL test suite). It keeps the name `local_postgres`, which `ui-review/review.sh` uses.
```
docker run --name local_postgres \
  -p 5432:5432 \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=devpassword \
  -e POSTGRES_DB=recall_db \
  -v local_pgdata_dhi:/var/lib/postgresql \
  -d dhi.io/postgres:18.7-debian13
```
The hardened image runs as UID 70 and keeps its data in `/var/lib/postgresql/18/data`, so it cannot start on a volume
made by the official `postgres` image (UID 999, `/var/lib/postgresql/18/docker`). To move an existing `local_postgres`
over, dump first, start the new container on a new volume as above, and restore; the old volume stays as it was:
```
docker exec local_postgres pg_dump -U postgres recall_db | gzip > ~/recall_db_before_dhi.sql.gz
docker stop local_postgres && docker rename local_postgres local_postgres_old
# the docker run command above, then, once it accepts connections:
until docker exec local_postgres pg_isready -h 127.0.0.1 -q; do sleep 1; done
gunzip -c ~/recall_db_before_dhi.sql.gz \
  | docker exec -i -e PGPASSWORD=devpassword local_postgres psql -h 127.0.0.1 -U postgres -d recall_db -v ON_ERROR_STOP=1 -q
```
Repeat the dump and restore for any other database in the container (create it first with `createdb`). When
everything works, `docker rm local_postgres_old` and `docker volume rm pgdata`.

If DB is empty in dev env. Add user:
```
11111111-1111-1111-1111-111111111111 - dev-user - dev@example.com
```

Launch the application in your dev environment
```
dotnet watch run --project Recall.Web --launch-profile Recall.Web
```

How a deploy works

A push to `main` builds the image and deploys it (`build_and_deploy` in `.github/workflows/dotnet.yml`). The deploy
folder on the server, `~/recall-deploy`, holds `compose.prod.yml`, `.env.prod`, `dump_db.sh`, `logs/` and
`dataprotection-keys/`. **`compose.prod.yml` and `dump_db.sh` come from the repository on every deploy**
(`compose.prod.yml` and `deploy/dump_db.sh`): change them in the repository, never on the server (the next deploy
would overwrite the edit). **`.env.prod` is the only file kept by hand on the server**, and it never leaves it. The
deploy:

1. copies both files to the server, saving the ones they replace as `compose.prod.yml.prev` and `dump_db.sh.prev`;
   `dump_db.sh` keeps its path and name, `~/recall-deploy/dump_db.sh`, with mode 750;
2. runs `docker compose --env-file .env.prod -f compose.prod.yml config -q` and stops if it fails, before anything is
   pulled or started: every variable `compose.prod.yml` uses is required, so one missing from `.env.prod` fails here
   with "`NAME` must be set in .env.prod". Both previous files are then put back, so the folder still describes what
   is running and never holds one new file beside an old one;
3. logs in to GHCR, runs `bash dump_db.sh` (a backup before every deploy), runs
   `docker compose --env-file .env.prod -f compose.prod.yml up -d --pull always --wait --wait-timeout 180` (the deploy
   fails unless every container is running and healthy within three minutes), and prunes old images. Any failing
   step stops the deploy, a failed backup included.

So a change that needs a new variable is deployed in two steps: add the variable to the server's `.env.prod` first,
then push the change.

Variables in .env.prod

Names only; the values are secrets or server-specific. Every setting in `appsettings.json` can also be overridden here
as `Section__Key` (for example `Login__AllowedEmails__0`, `TrustedProxies__Networks__0`, `Retention__EmailDays`).

Required by `compose.prod.yml` (the deploy fails without them):

| Variable | What it is |
|---|---|
| `POSTGRES_HOST` | Name of the Postgres container (`recall_postgres`), also the host in the connection string |
| `POSTGRES_PORT` | Port Postgres listens on inside its container (5432), published on the host's loopback as 5433 |
| `POSTGRES_DB` | Database Postgres creates on first start (`recall_db`) |
| `POSTGRES_USER` | Owner of that database, used by the healthcheck and the backups |
| `POSTGRES_PASSWORD` | Its password |
| `VALKEY_PASSWORD` | Password Valkey requires; the same value goes in `REDIS_CONNECTION` |

`dump_db.sh` reads four of them too: `POSTGRES_HOST`, `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD`.

Not in `.env.prod` except on a new server's first start: `Database__AllowEmptyDatabase` (see "A new server's first
start").

Read by the app (the container gets the whole file):

| Variable | What it is |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | Where the app listens inside its container: `http://+:8701`. Or, instead, `ASPNETCORE_HTTP_PORTS=8701`, which avoids the startup warning "Overriding HTTP_PORTS '8080'" (the image sets HTTP_PORTS to 8080 and URLS overrides it); keep only one of the two, since URLS wins when both are set |
| `ConnectionStrings__DefaultConnection` | Npgsql connection string to `POSTGRES_HOST`/`POSTGRES_DB` with that user and password; startup fails without it |
| `REDIS_CONNECTION` | `redis:6379,password=<VALKEY_PASSWORD written out>` (see "Cache settings in .env.prod") |
| `TheTvDb__ApiKey` | TheTVDB API key |
| `TheTvDb__Pin` | TheTVDB subscriber PIN, if the key has one |
| `Omdb__ApiKey` | OMDb API key (IMDb ratings) |
| `Mail__FromAddress` | Sender address of sign-in emails and digests |
| `Mail__Host` | SMTP server |
| `Mail__Port` | SMTP port |
| `Mail__Username` | SMTP user |
| `Mail__Password` | SMTP password |
| `Turnstile__SiteKey` | Cloudflare Turnstile site key for the sign-in form (blank turns Turnstile off) |
| `Turnstile__SecretKey` | Cloudflare Turnstile secret key |
| `Site__BaseUrl` | Public address (`https://recall.nu`) for links in emails; required while the digest is on |
| `Digest__Enabled` | `true`: the weekly digest is on |
| `Digest__MaxPerRun` | Users handled per hourly digest run (50) |
| `Digest__DayOfWeek` | Optional: day the digest goes out (default Friday) |
| `Digest__HourUtc` | Optional: UTC hour it goes out (default 15) |
| `Push__Enabled` | Only while push notifications are on: `true` |
| `Push__PublicKey` | Only while push is on: VAPID public key (see "Enabling push notifications") |
| `Push__PrivateKey` | Only while push is on: VAPID private key |
| `Push__Subject` | Only while push is on: `mailto:` contact address |

Rolling back the deployed files

The deploy keeps the files it replaced. To go back to them, in the deploy folder:
```
cp compose.prod.yml.prev compose.prod.yml
docker compose --env-file .env.prod -f compose.prod.yml up -d --wait

cp -p dump_db.sh.prev dump_db.sh      # -p keeps its mode; the cron job runs it from here
```
Roll back the one that is wrong, or both. The next deploy copies the repository's files again, so revert the change in
the repository as well. To go back to an
earlier image too, set `image:` to `ghcr.io/jockesoft/recall:<run number>` in the repository and deploy.

Folders the app writes to

The app's image runs as UID 65532 (the hardened image's `nonroot` user) and can write only two folders on the server,
both in `~/recall-deploy` and both bind-mounted by `compose.prod.yml`: `logs/` (its log files) and
`dataprotection-keys/` (the keys that protect sign-in cookies and the unsubscribe links in sent digests). Both must be
owned by 65532:
```
cd ~/recall-deploy
mkdir -p logs dataprotection-keys
sudo chown -R 65532:65532 logs dataprotection-keys
ls -ln logs dataprotection-keys        # 65532 65532 on every line
```
Keep the key files: losing them signs everyone out and breaks every unsubscribe link already sent. If the app cannot
write its key folder it does not start: `docker logs recall_app` then shows a critical line naming the folder and UID
65532, the container restarts and reports unhealthy, and the deploy's `up --wait` fails.

Debugging the app container

The image has no shell, so `docker exec recall_app sh` does not work. Instead:
- `docker logs recall_app`, and the files in `~/recall-deploy/logs`;
- `docker inspect recall_app --format '{{json .State.Health}}'` for the healthcheck's last answers, and
  `docker exec recall_app dotnet /app/Recall.Web.dll healthcheck` to ask once (it needs no shell);
- `docker debug recall_app`, which opens a shell with tools beside the container, where Docker Desktop's
  `docker debug` is installed;
- otherwise a throwaway container from the hardened `-dev` image (shell, `apt-get`) that joins the app's processes and
  network; the app's files are under `/proc/1/root`:
  ```
  docker run --rm -it --pid container:recall_app --network container:recall_app --cap-add SYS_PTRACE \
    dhi.io/aspnetcore:10-dev bash
  ```

Reverse proxy on the server

Production runs the app in a Docker container behind nginx installed directly on the host (apt, systemd).
nginx proxies to the app's published port, 8701, and sends these three headers (the app builds sign-in
links from the scheme and host, and rate-limits per client IP):
```
proxy_set_header Host              $host;
proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
proxy_set_header X-Forwarded-Proto $scheme;
```
The app accepts `X-Forwarded-*` from loopback and the private address ranges, which is how nginx on the
host reaches the container, so nothing needs configuring for this.


Cache settings in .env.prod

The cache is Valkey (`dhi.io/valkey:9.1`, the `redis` service in `compose.prod.yml`), protected with a password.
`.env.prod` holds it twice: once for compose, which passes it to Valkey, and once inside the app's connection string.
```
VALKEY_PASSWORD=<long random value>
REDIS_CONNECTION=redis:6379,password=<the same value, written out>
```
Generate the value with `openssl rand -hex 32`: hex has no `,` (which would split the connection string) and no `$`
(which compose would try to expand). Write it out in `REDIS_CONNECTION` rather than referring to `${VALKEY_PASSWORD}`.
Compose refuses to start when `VALKEY_PASSWORD` is missing.

Switching production to Valkey

Done once, on the server, in the folder that holds `compose.prod.yml` and `.env.prod`. The cache holds nothing that
is not also in Postgres, so nothing is migrated: Valkey starts empty and fills as pages are read.

1. Log in to the hardened-image registry as the user that runs compose (Docker Hub username and a read-only access
   token): `docker login dhi.io`.
2. Add `VALKEY_PASSWORD` and change `REDIS_CONNECTION` in `.env.prod` as above.
3. Replace `compose.prod.yml` with the repository's version. (Since the deploy ships `compose.prod.yml`, the next
   deploy does this and step 4 itself, provided steps 1 and 2 are done first; otherwise it stops at its config check.)
4. `docker compose --env-file .env.prod -f compose.prod.yml up -d --wait`. This recreates the `redis` container on
   Valkey and the app container with the new connection string.

Checks afterwards:
```
docker compose --env-file .env.prod -f compose.prod.yml ps                          # redis and recall_app running, redis healthy
docker compose --env-file .env.prod -f compose.prod.yml exec redis valkey-cli -e INFO server | grep -E 'valkey_version|redis_version'
docker logs recall_app 2>&1 | grep -c "Redis read failed"                            # 0 for everything since the restart
docker compose --env-file .env.prod -f compose.prod.yml exec redis valkey-cli -e INFO stats | grep keyspace   # hits rise as pages are opened
docker compose --env-file .env.prod -f compose.prod.yml exec redis valkey-cli -e INFO memory | grep -E 'maxmemory_human|maxmemory_policy'   # 256.00M, allkeys-lru
docker compose --env-file .env.prod -f compose.prod.yml exec redis valkey-cli -e CONFIG GET save              # empty: no snapshots
```
`valkey-cli` inside the container authenticates through `VALKEYCLI_AUTH`. The first page loads after the switch read
from the Postgres snapshots, not from TheTVDB.

Rollback: put back the previous `redis` service (`image: redis:7`, no password, `redis-cli ping` healthcheck),
set `REDIS_CONNECTION=redis:6379` again (the app also connects to a Redis without a password while it still sends
one, but the setting should say what is true) and remove `VALKEY_PASSWORD`, then run the same `up -d --wait`.
Nothing needs restoring.

Switching production to DHI Postgres

Done once, as the deploy user, in `~/recall-deploy`. The database moves from `postgres:18.1` on the compose volume
`pgdata` to `dhi.io/postgres:18.7-debian13` on a new volume, `recall_pgdata_dhi`, by dump and restore. The hardened
image runs as a different user (UID 70) and keeps its data in a different folder (`/var/lib/postgresql/18/data`), so
it cannot start on the old volume. The old volume is left exactly as it is, as the rollback. The site is down from
step 3 until the deploy in step 8 is healthy, which is about as long as a CI run.

Run everything in one bash session. Step 1 sets the shell variables the later steps use; in a new session, run step 1
again first. The values stay in `.env.prod`: nothing below spells one out.

1. Read the database settings from `.env.prod`, and define two helpers: `sql <container> -c "…"` runs a query in
   either Postgres container, and `ROWS` is the query that counts the rows of every table (the one `review.sh` uses,
   one table per line so `diff` can say which one differs).
   ```
   cd ~/recall-deploy
   env_value() { grep -E "^$1=" .env.prod | tail -n 1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }
   export POSTGRES_HOST="$(env_value POSTGRES_HOST)" POSTGRES_DB="$(env_value POSTGRES_DB)" \
          POSTGRES_USER="$(env_value POSTGRES_USER)" POSTGRES_PASSWORD="$(env_value POSTGRES_PASSWORD)"
   export PGPASSWORD="$POSTGRES_PASSWORD"
   sql() { docker exec -i -e PGPASSWORD "$1" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password -v ON_ERROR_STOP=1 -At "${@:2}"; }
   ROWS="SELECT table_name || ' ' || (xpath('/row/c/text()', query_to_xml('SELECT count(*) AS c FROM ' || quote_ident(table_name), false, true, '')))[1]::text FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name;"
   mkdir -p ~/pg-switch
   ```

2. Check, while the app still runs:
   ```
   ls -lt /var/backups/recall | head -3                     # last night's automatic dump is there
   gunzip -t "$(ls -t /var/backups/recall/*.sql.gz | head -1)" && echo "latest dump OK"
   curl -s http://127.0.0.1:8701/ | grep -o 'Version v1\.0\.[^ <]*' | tee ~/pg-switch/build-before.txt
   docker inspect "$POSTGRES_HOST" --format '{{.Config.Image}} {{range .Mounts}}{{.Name}}{{end}}'
   docker inspect "$POSTGRES_HOST" --format '{{range .Mounts}}{{.Name}}{{end}}' > ~/pg-switch/old-volume.txt
   docker pull dhi.io/postgres:18.7-debian13               # now, rather than during the downtime
   ```
   The `inspect` line shows `postgres:18.1` and the old volume's name (`recall-deploy_pgdata`: compose puts the
   project's name, the folder's, in front of `pgdata`); it is kept in `~/pg-switch/old-volume.txt` for step 11.

3. Stop the app, so nothing is written from here on, and take the final dump. A copy goes to `~/pg-switch`, because
   `dump_db.sh` deletes dumps after 9 days and this one is the state of the old volume.
   ```
   docker compose --env-file .env.prod -f compose.prod.yml stop recall
   ./dump_db.sh
   DUMP="$(ls -t /var/backups/recall/*.sql.gz | head -1)"; echo "$DUMP"
   cp -p "$DUMP" ~/pg-switch/
   ```

4. Create the new volume and start a temporary Postgres on it: the hardened image, another container name, no
   published port, the same database, user and password (`-e NAME` without a value takes it from step 1, so the
   password is not on the command line). Wait with `-h 127.0.0.1`, which only answers once the first-start setup is
   done and the real server runs.
   ```
   docker volume create recall_pgdata_dhi
   docker run -d --name recall_postgres_new -e POSTGRES_DB -e POSTGRES_USER -e POSTGRES_PASSWORD \
     -v recall_pgdata_dhi:/var/lib/postgresql dhi.io/postgres:18.7-debian13
   until docker exec recall_postgres_new pg_isready -h 127.0.0.1 -q; do sleep 1; done
   ```

5. Restore the final dump into it. `ON_ERROR_STOP` stops at the first error; the exit code must be 0 (the log then
   holds only the dump's one `set_config` result).
   ```
   gunzip -c "$DUMP" | docker exec -i -e PGPASSWORD recall_postgres_new \
     psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password -v ON_ERROR_STOP=1 -q > ~/pg-switch/restore.log
   echo "restore exit code: $?"
   ```

6. Compare the two databases. The row counts must be identical (`diff` prints nothing), both must have the same
   number of migrations and the same latest one, and both collations must be `en_US` UTF-8 from the C library
   (`datlocprovider` `c`). The old image spells it `en_US.utf8` and the new one `en_US.UTF-8`; they are the same
   locale.
   ```
   sql "$POSTGRES_HOST"     -c "$ROWS" > ~/pg-switch/rows-old.txt
   sql recall_postgres_new -c "$ROWS" > ~/pg-switch/rows-new.txt
   diff ~/pg-switch/rows-old.txt ~/pg-switch/rows-new.txt && echo "row counts match in $(wc -l < ~/pg-switch/rows-new.txt) tables"
   for c in "$POSTGRES_HOST" recall_postgres_new; do
     echo "== $c"
     sql "$c" -c 'SELECT count(*), max("MigrationId") FROM "__EFMigrationsHistory";' \
              -c "SELECT datcollate, datctype, datlocprovider, pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database();" \
              -c 'SELECT version();'
   done
   ```

7. Stop and remove the temporary container; the volume keeps the data. The old container keeps running, still on
   the old volume: the deploy's backup in step 8 reads from it.
   ```
   docker stop recall_postgres_new && docker rm recall_postgres_new
   ```

8. Merge the pull request and push. The deploy copies the new `compose.prod.yml`, runs `dump_db.sh` against the old
   container once more, and `up -d --pull always --wait` recreates `recall_postgres` on the hardened image and
   `recall_pgdata_dhi` and starts the app again (compose restarts the app whenever it replaces the database container,
   so the app's startup checks always see the new database). Wait for the workflow to finish green. If it fails at `--wait`, look
   at `docker logs recall_app` and `docker logs "$POSTGRES_HOST"`, then roll back (step 10).

9. Check after the deploy:
   ```
   docker compose --env-file .env.prod -f compose.prod.yml ps        # recall_app, recall_postgres and redis healthy
   docker inspect "$POSTGRES_HOST" --format '{{.Config.Image}} {{range .Mounts}}{{.Name}}{{end}}'   # dhi.io/postgres:18.7-debian13 recall_pgdata_dhi
   sql "$POSTGRES_HOST" -c 'SELECT version();'                       # PostgreSQL 18.7
   docker logs recall_app 2>&1 | grep -E 'migration' | tail -1       # "Database schema is up to date; no migrations to apply."
   docker logs recall_app 2>&1 | grep -c 'FTL'                       # 0: no Critical line
   sql "$POSTGRES_HOST" -c "$ROWS" | diff ~/pg-switch/rows-old.txt - # see below
   ```
   Right after the start, the row counts are those of step 6. Once the app has run for a while its jobs change a few
   tables (`cached_*`, `email`, `login_token`, `notification`, `notified_episode`, `digest_send`), and signing in adds
   a `login_token` and an `email`; the tables of users' own data (`app_user`, `tracked_series`, `episode_watch`,
   `user_rating`, …) only change when someone uses the site. Then sign in on the site (a code or the link from the
   email), and check that a backup still works against the new image:
   ```
   ./dump_db.sh && gunzip -t "$(ls -t /var/backups/recall/*.sql.gz | head -1)" && echo "dump OK"
   ```

10. Rollback, while the old volume still exists. Right after the switch's deploy, `compose.prod.yml.prev` is the
    previous file (after a later deploy, take `compose.prod.yml` from the commit before the merge instead):
    ```
    cp compose.prod.yml.prev compose.prod.yml
    docker compose --env-file .env.prod -f compose.prod.yml up -d --wait
    ```
    This recreates `recall_postgres` on `postgres:18.1` and the old volume, which holds the data as it was in step 3.
    Then revert the merge in the repository (`git revert -m 1 <merge commit>`) and push, so the next deploy does not
    switch again. To try the switch again later, first `docker volume rm recall_pgdata_dhi`, then start from step 1.

    Anything written after the switch (sign-ups, watches, ratings) is only in the new database. To keep it, move the
    whole new database back instead: it holds everything the old one had plus those writes. Instead of the two
    commands above:
    ```
    ./dump_db.sh                                       # the new database, with everything written since the switch
    DUMP="$(ls -t /var/backups/recall/*.sql.gz | head -1)"; cp -p "$DUMP" ~/pg-switch/
    docker compose --env-file .env.prod -f compose.prod.yml stop recall
    cp compose.prod.yml.prev compose.prod.yml
    docker compose --env-file .env.prod -f compose.prod.yml up -d --wait postgres
    docker exec -e PGPASSWORD "$POSTGRES_HOST" psql -h 127.0.0.1 -U "$POSTGRES_USER" -d postgres --no-password -v ON_ERROR_STOP=1 \
      -c "DROP DATABASE \"$POSTGRES_DB\" WITH (FORCE);" -c "CREATE DATABASE \"$POSTGRES_DB\" OWNER \"$POSTGRES_USER\";"
    gunzip -c "$DUMP" | docker exec -i -e PGPASSWORD "$POSTGRES_HOST" \
      psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-password -v ON_ERROR_STOP=1 -q > ~/pg-switch/rollback-restore.log
    echo "restore exit code: $?"
    docker compose --env-file .env.prod -f compose.prod.yml up -d --wait
    ```
    This replaces the old volume's database with the new one's; the old state is still in the step 3 dump in
    `~/pg-switch`. Check the row counts as in step 9, then revert the merge as above.

11. Delete the old volume a few weeks after the switch, once a rollback is out of the question (after that, only the
    dumps go back to before it):
    ```
    OLD_VOLUME="$(cat ~/pg-switch/old-volume.txt)"; echo "$OLD_VOLUME"     # recall-deploy_pgdata, not recall_pgdata_dhi
    docker ps -a --filter volume="$OLD_VOLUME" --format '{{.Names}}'       # nothing: no container uses it
    docker volume rm "$OLD_VOLUME"
    ```
    Then remove the `pgdata:` entry from `compose.prod.yml` in the repository, and `~/pg-switch` from the server.

A new server's first start

`recall_pgdata_dhi` is declared `external` in `compose.prod.yml`: compose never creates or removes it, so it is created
by hand once, `docker volume create recall_pgdata_dhi`, before the first `up`. In Production the app also refuses to
start on a database with no applied migrations (a critical line, "has no applied migrations … Not starting", and the
deploy's `--wait` fails), because on a running server that means the wrong volume, not a new install. For a genuine
first install, add `Database__AllowEmptyDatabase=true` to `.env.prod` for the first start, which creates the schema,
then remove it and run `docker compose --env-file .env.prod -f compose.prod.yml up -d --wait` again.

Back up the production database

Backups are made by `dump_db.sh` (`deploy/dump_db.sh` in the repository, shipped by every deploy to
`~/recall-deploy/dump_db.sh`). A daily cron job on the server runs it from there, and every deploy runs it before
starting anything. It contains no secrets:

- it reads `POSTGRES_HOST` (the Postgres container's name), `POSTGRES_DB`, `POSTGRES_USER` and `POSTGRES_PASSWORD` from
  the `.env.prod` next to it (`ENV_FILE=...` points it at another file), and stops with "`NAME` is not set" when one
  is missing;
- it runs `pg_dump` inside the Postgres container and writes `/var/backups/recall/<database>_<UTC date and
  time>.sql.gz` (owner-only permissions), first as `.partial`, renamed only after `gzip -t` has checked it;
- only after a successful dump does it delete dumps older than 9 days, so a failing backup never removes the old ones.

Run it by hand the same way: `bash ~/recall-deploy/dump_db.sh`.

The Postgres container is `recall_postgres` (database `recall_db`, user `recall_user`). The same dump without the
script, for reference:

```
# In the folder that holds .env.prod: load POSTGRES_USER, POSTGRES_DB and POSTGRES_PASSWORD
set -a; . ./.env.prod; set +a

docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" recall_postgres \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  | gzip > "/var/backups/recall/recall_db_$(date +%Y%m%d_%H%M%S).sql.gz"
```
Do not add `-t` to `docker exec` here: a terminal rewrites line endings and corrupts the dump.

Restore a backup

The dump is plain SQL without `DROP` statements, so restore it into an empty database, with the app
container stopped. Replace the file name with the backup to restore. `ON_ERROR_STOP` makes psql stop at the first
error instead of carrying on with a half-restored database.
```
set -a; . ./.env.prod; set +a

gunzip -c /var/backups/recall/recall_db_YYYY-MM-DD_HH-MM-SS.sql.gz \
  | docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" recall_postgres \
      psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -q
```

Weekly digest

The weekly email is enabled in production: `.env.prod` sets `Digest__Enabled=true`, `Digest__MaxPerRun=50` and `Site__BaseUrl`.
It stays off everywhere else (`Digest:Enabled` is false in `appsettings.json`), so a local instance sends nothing.

This is the checklist that was followed before enabling it. Go through it again when the mail provider, the sending domain or the site's address changes, or when setting up a new environment.

1. Set `Site__BaseUrl` to the site's public address, for example `https://recall.nu`. The links in the email are built from it. There is no default, and the app refuses to start with the digest enabled and this missing.
2. Set `Digest__MaxPerRun` to fit the mail provider's sending limits. It is the number of users handled per hourly run (default 100, 50 in production); the rest follow in the next runs, for up to `Digest__CatchUpHours` (default 48). Sign-in links are always sent before digests.
3. Check SPF, DKIM and DMARC for the sending domain (the domain of `Mail__FromAddress`). A weekly mail from a domain without them lands in spam, and takes the sign-in links with it.
4. Preview your own digest: sign in as an admin and open Admin, "Preview the weekly email". Check the text, the links and the unsubscribe page. The preview sends nothing.
5. Set `Digest__Enabled=true` and deploy. Users then see a "Weekly email" switch in their profile and a one-time offer on the Dashboard; only those who turn it on are sent anything. The digest goes out on `Digest__DayOfWeek` (default Friday) from `Digest__HourUtc` (default 15, UTC).

To switch it off again, set `Digest__Enabled=false`. Users keep their choice.

Enabling push notifications

Push notifications tell users on their phone or computer when a new episode is out. They are off unless `Push:Enabled` is true; while off, nothing about them shows anywhere.

1. Generate a VAPID key pair, once per environment:
   ```
   dotnet run --project Recall.Web -- generate-vapid-keys
   ```
   It prints `Push__PublicKey=…` and `Push__PrivateKey=…` and exits. Keep the private key out of the repository. Keep the pair, too: a new pair makes every existing subscription stop working until each device turns notifications on again.
2. Set the keys and a contact address. Locally, in user secrets:
   ```
   dotnet user-secrets set "Push:Enabled" "true" --project Recall.Web
   dotnet user-secrets set "Push:PublicKey" "<public key>" --project Recall.Web
   dotnet user-secrets set "Push:PrivateKey" "<private key>" --project Recall.Web
   dotnet user-secrets set "Push:Subject" "mailto:you@example.com" --project Recall.Web
   ```
   In production, in `.env.prod`:
   ```
   Push__Enabled=true
   Push__PublicKey=<public key>
   Push__PrivateKey=<private key>
   Push__Subject=mailto:admin@recall.nu
   ```
   The app refuses to start when push is enabled with a key missing, malformed or from another pair, or a subject that is not a `mailto:` address, and the message says which.
3. nginx: the app serves `/sw.js` (the service worker) and `/manifest.webmanifest` from the site root with `Cache-Control: no-cache`. nginx only has to pass that through: do not add caching rules (`expires`, `proxy_cache`) that cover these two paths, or a new service worker reaches browsers late. Push needs HTTPS, which the site has; `localhost` also works in Chrome and Firefox for local tests.
4. Deploy, sign in as an admin and test from the devices below. The admin page's "Send a test push to my devices" sends to your own devices only, whatever the time. New-episode pushes are sent by the hourly `NewEpisodeNotificationTimer`; each run logs a line like `NewEpisodeNotificationTimer: 1 push(es) sent, 0 held for quiet hours, 0 failed (...)`.

Testing on Android (Chrome, Edge, Firefox or Samsung Internet):
1. Open the site, sign in, go to Profile, Notifications, and press "Turn on" under "This device". Allow notifications when the browser asks (it asks only then).
2. The device appears under "Your devices" as, for example, "Chrome on Android", with its time zone.
3. On Admin, press "Send a test push to my devices". "Recall test notification" should arrive within seconds, also with the browser in the background; tapping it opens Profile.
4. Optional: install the app ("Add to Home screen" or "Install app" in the browser menu) and repeat from the installed app.
5. Quiet hours: with them on (the default) and the phone's clock between 22:00 and 08:00, a new-episode push waits until 08:00 there. The test push ignores quiet hours.

Testing on iPhone or iPad (iOS/iPadOS 16.4 or later):
1. In Safari, open the site and use Share, "Add to Home Screen". Push only exists for the Home Screen app; in Safari itself Profile shows these instructions instead of the switch.
2. Open Recall from the Home Screen icon. The Home Screen app has its own cookies, separate from Safari's, so it starts signed out even when Safari is signed in.
3. Sign in inside the app: enter your email and press "Send sign-in link". Don't open the link from the email (it opens in Safari); type the 6-digit code from the same email into the field under "Check your inbox" and press "Sign in". Spaces are fine.
4. Check the code's limits once: a wrong code says "That code didn't work"; after five wrong codes the page asks for a new email; using the link afterwards, or the code after the link, does not sign in.
5. In the app, Profile, Notifications, "Turn on", and allow. Then send a test push from Admin as above; it arrives like any app notification and opens Recall when tapped.
6. If it was refused once, allow it again under Settings, Notifications, Recall.

To switch push off again, set `Push__Enabled=false`. Subscriptions stay stored but nothing is sent, and they are deleted after 180 days without a successful push (`Retention__PushSubscriptionDays`).

Add update to DB
```
dotnet ef migrations add <Any name> --project Recall.Web
dotnet ef database update --project Recall.Web
```

If the app logs that its Data Protection key folder is not writable, give the folder to the app's user, as in "Folders
the app writes to": `sudo chown -R 65532:65532 dataprotection-keys` in `~/recall-deploy`.

If port is in use on localhost:
```
lsof -i :7123
kill -9 <PID>
```