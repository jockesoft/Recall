
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
docker run --name my-redis -p 127.0.0.1:6379:6379 -d dhi.io/valkey:9.1 \
  --protected-mode no --save "" --appendonly no
```
The container keeps the name `my-redis`, which `ui-review/review.sh` uses. Locally it has no password, so the port
is bound to 127.0.0.1 only; `--protected-mode no` is needed because the app reaches it through Docker's port
forwarding, which Valkey does not count as loopback (with protected mode on it refuses the app, and the app quietly
reads everything from Postgres instead). To replace an old Redis container: `docker rm -f my-redis`, then the
command above; it only ever held cache.

Start postgres container
```
docker run --name local_postgres \
  -p 5432:5432 \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=devpassword \
  -e POSTGRES_DB=recall_db \
  -v pgdata:/var/lib/postgresql \
  -d postgres:18.1
```

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
`dataprotection-keys/`. **`compose.prod.yml` comes from the repository on every deploy**: change it in the repository,
never on the server (the next deploy would overwrite the edit). **`.env.prod` is the only file kept by hand on the
server**, and it never leaves it. `dump_db.sh` also lives only on the server. The deploy:

1. copies the repository's `compose.prod.yml` to the server, saving the file it replaces as `compose.prod.yml.prev`;
2. runs `docker compose --env-file .env.prod -f compose.prod.yml config -q` and stops if it fails, before anything is
   pulled or started: every variable `compose.prod.yml` uses is required, so one missing from `.env.prod` fails here
   with "`NAME` must be set in .env.prod". The previous `compose.prod.yml` is then put back, so the folder still
   describes what is running;
3. logs in to GHCR, runs `dump_db.sh`, runs `docker compose --env-file .env.prod -f compose.prod.yml up -d --pull
   always`, and prunes old images. Any failing step stops the deploy.

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

Read by the app (the container gets the whole file):

| Variable | What it is |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | Where the app listens inside its container: `http://+:8701` |
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

Rolling back compose.prod.yml

The deploy keeps the file it replaced. To go back to it, in the deploy folder:
```
cp compose.prod.yml.prev compose.prod.yml
docker compose --env-file .env.prod -f compose.prod.yml up -d --wait
```
The next deploy copies the repository's file again, so revert the change in the repository as well. To go back to an
earlier image too, set `image:` to `ghcr.io/jockesoft/recall:<run number>` in the repository and deploy.

On the server after first deploy run:
mkdir -p logs dataprotection-keys && sudo chown -R 64198:64198 logs dataprotection-keys

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

Back up the production database

Runs on the server. The Postgres container is `recall_postgres` (database `recall_db`, user `recall_user`);
dumps are gzipped into `/var/backups/recall/`. Nothing here contains a password: the user, database and
password come from the server's `.env.prod`.

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
container stopped. Replace the file name with the backup to restore.
```
set -a; . ./.env.prod; set +a

gunzip -c /var/backups/recall/recall_db_YYYYMMDD_HHMMSS.sql.gz \
  | docker exec -i -e PGPASSWORD="$POSTGRES_PASSWORD" recall_postgres \
      psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"
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

If the following error appears:
```
Access to the path '/home/devuser/.aspnet/DataProtection-Keys/key-b645bc76-25a2-4024-9014-948416852792.xml' is denied.
aspnetcore_app  |  ---> System.IO.IOException: Permission denied
```
Then login to docker using root and run the following command:
```
chown -R 1000:1000 /home/devuser/.aspnet/.

```

If port is in use on localhost:
```
lsof -i :7123
kill -9 <PID>
```