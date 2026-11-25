-- One-time, per database (piranhacommerce_staging, then piranhacommerce_live). Run it YOURSELF, connected to that database as an Entra
-- administrator of the server (for example in the portal's Query editor). The pipeline never has server-wide access, so it cannot touch
-- any other database on the server.
--
-- It gives two identities access to this one database:
--   $(DeployIdentity)  the pipeline's app registration: db_owner, so it can apply migrations and nothing else on the server.
--   $(AppIdentity)     the web app's managed identity: read and write only. It cannot change the schema.
-- Staging database: AppIdentity is  awa-piranhacommerce-demo/slots/staging
-- Live database:    AppIdentity is  awa-piranhacommerce-demo
-- DeployIdentity is  github-piranhacommerce-deploy  in both.
-- Safe to run again. Replace the $(...) values below (or run it with sqlcmd -v).

DECLARE @deploy sysname = N'$(DeployIdentity)';
DECLARE @app sysname = N'$(AppIdentity)';
DECLARE @sql nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @deploy)
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@deploy) + N' FROM EXTERNAL PROVIDER'; EXEC (@sql);
END
SET @sql = N'ALTER ROLE db_owner ADD MEMBER ' + QUOTENAME(@deploy); EXEC (@sql);

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @app)
BEGIN
    SET @sql = N'CREATE USER ' + QUOTENAME(@app) + N' FROM EXTERNAL PROVIDER'; EXEC (@sql);
END
SET @sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@app); EXEC (@sql);
SET @sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@app); EXEC (@sql);
