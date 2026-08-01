USE msdb;
GO

IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = N'MigrationStudio Representative Fixture Job')
    EXEC dbo.sp_delete_job @job_name = N'MigrationStudio Representative Fixture Job';
GO

EXEC dbo.sp_add_job
    @job_name = N'MigrationStudio Representative Fixture Job',
    @enabled = 0,
    @description = N'Disabled fixture used only to validate SQL Agent inventory.';

EXEC dbo.sp_add_jobstep
    @job_name = N'MigrationStudio Representative Fixture Job',
    @step_name = N'Inventory fixture step',
    @subsystem = N'TSQL',
    @database_name = N'master',
    @command = N'SELECT SYSUTCDATETIME();';
GO

