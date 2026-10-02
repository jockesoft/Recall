
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

Start redis container
```
docker run --name my-redis -p 6379:6379 -d redis:7
```

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