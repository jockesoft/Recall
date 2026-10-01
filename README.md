
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

Production runs the app in Docker behind nginx installed directly on the host (apt, systemd). nginx
terminates TLS and proxies to `127.0.0.1:8701`. `compose.prod.yml` publishes the app on `127.0.0.1:8701`
and Postgres on `127.0.0.1:5433` only, so nginx is the only way in from outside. CI does not copy
`compose.prod.yml` to the server — update the copy in `recall-deploy/` by hand when it changes.

nginx must send these three headers (the app builds sign-in links from the scheme and host, and
rate-limits per client IP):
```
location / {
    proxy_pass http://127.0.0.1:8701;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```
`$proxy_add_x_forwarded_for` appends the real client address to whatever the client sent; the app reads
only that last entry (`TrustedProxies:ForwardLimit` is 1), so a forged `X-Forwarded-For` is ignored.

Requests reach the container from the compose network's gateway, a private address. The app trusts
`X-Forwarded-*` from loopback and the private ranges by default, so this works with nothing configured.
To trust only the compose network instead, look up its subnet and add it to `.env.prod`:
```
# docker network inspect recall-deploy_recall_prodnet --format '{{(index .IPAM.Config 0).Subnet}}'
# TrustedProxies__Networks__0=172.18.0.0/16
```
Setting it replaces the default entirely. Docker may hand the network a different subnet if it is ever
recreated (`docker compose down` then `up`); a stale value here makes the app ignore nginx's headers, which
shows up as sign-in links with `http://` and every visitor sharing one rate-limit bucket. Re-check the
subnet after recreating the network, or pin it under `networks:` in `compose.prod.yml`.


Take database dump from postgres container

```
cd Sync/Development/Receptus/DB_Backup/
docker exec -t PostgreSQL_recall pg_dump -U postgres -d recall_db > dump.sql
docker exec -t PostgreSQL_recall pg_dump -U postgres -d recall_db | gzip > dump.sql.gz
```

Restore DB Dump
```
docker cp dump.sql.gz PostgreSQL_recall:/dump.sql.gz
docker exec -i PostgreSQL_recall bash -c "gunzip -c /dump.sql.gz | psql -U postgres -d recall_db"
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