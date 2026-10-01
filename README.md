
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