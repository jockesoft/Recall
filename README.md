
Initialize secrets for your web project
```
dotnet user-secrets init --project ./Recall.Web
```

Set your TheTVDB values
```
dotnet user-secrets set "TheTvDb:ApiKey" "YOUR_REAL_API_KEY" --project ./Recall.Web
dotnet user-secrets set "TheTvDb:Pin" "YOUR_PIN_IF_ANY" --project ./Recall.Web
```

Launch the application in your dev environment
```
dotnet watch run --project Recall.Web --launch-profile Recall.Web
```

Take database dump from postgres container

```
cd /media/jockesoft/ExtraDisk/SynologyDrive/Development/Receptus/DB_Backup/
docker exec -t PostgreSQL_receptus pg_dump -U postgres -d receptus_db > dump.sql
docker exec -t PostgreSQL_receptus pg_dump -U postgres -d receptus_db | gzip > dump.sql.gz
```

Restore DB Dump
```
docker cp dump.sql.gz PostgreSQL_receptus:/dump.sql.gz
docker exec -i PostgreSQL_receptus bash -c "gunzip -c /dump.sql.gz | psql -U postgres -d receptus_db"
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

To tail logfiles:
```
docker logs -f aspnetcore_app
```

If port is in use on localhost:
```
lsof -i :8172
kill -9 <PID>
```