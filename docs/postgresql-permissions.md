# PostgreSQL permissions

Use separate administrative and application roles where practical.

For deployment to an existing database, the deployment role needs `CONNECT`, `USAGE`/`CREATE` on
the target schemas as appropriate, and permission to create the selected object types. Installing
extensions may require database ownership or superuser-equivalent authority depending on the
extension.

Creating a target database requires `CREATEDB` or an administrative role connected to the
maintenance database. Dropping or replacing a database requires explicit application confirmation
and sufficient ownership/administrative privilege.

Data migration needs `INSERT` and, for restart/replace strategies, the applicable `DELETE`,
`TRUNCATE`, or sequence privileges. Validation normally needs `CONNECT`, schema `USAGE`, and
`SELECT` on migrated objects.

Avoid running routine migrations as PostgreSQL superuser. Assess privileges before deployment and
record every administrative override.

