# Quick start

1. Install the application or extract the self-contained portable ZIP.
2. Open the Workspace and enter the SQL Server endpoint. Prefer Windows authentication where
   available.
3. Test the connection and select a database.
4. Choose Complete Database, Selected Schemas, Excel Selection, or Manual Selection.
5. Run discovery and review exclusions, dependency additions, findings, and unresolved references.
6. Select PostgreSQL 14-18 and conversion policies, then run conversion.
7. Review manual and unsupported conversions before generating a deployment package.
8. Configure the PostgreSQL endpoint, assess the package, and deploy to a non-production target.
9. Run data migration, then deploy programmable objects if the chosen plan separates those phases.
10. Run full validation and resolve every critical blocker.
11. Generate the report package and retain it with the inventory, deployment journal, and
    validation run.

Always rehearse with production-representative scale and data distribution. Do not treat successful
script generation as proof that a migration is production-ready.

