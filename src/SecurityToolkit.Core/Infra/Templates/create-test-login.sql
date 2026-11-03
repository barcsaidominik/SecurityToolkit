-- Run by `security-toolkit infra add-test-login`; $(TestLogin)/$(TestPassword) come from sqlcmd's environment.
-- Idempotent: an existing login gets the new password (rerunning the script rotates it).
-- CREATE ANY DATABASE (not dbcreator): the login can create databases and, as their owner, alter
-- and drop them, but cannot touch another project's databases (dbcreator could drop any of them).
IF SUSER_ID(N'$(TestLogin)') IS NULL
    CREATE LOGIN [$(TestLogin)] WITH PASSWORD = N'$(TestPassword)', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [$(TestLogin)] WITH PASSWORD = N'$(TestPassword)';
GO

GRANT CREATE ANY DATABASE TO [$(TestLogin)];
GO

PRINT N'Test login ready: $(TestLogin)';
