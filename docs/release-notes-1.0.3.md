# SQL Server to PostgreSQL Migration Studio 1.0.3

## Fixes

- The Data Migration PostgreSQL target password is now forwarded explicitly
  from the WPF `PasswordBox` to the structured connection ViewModel.
- A missing password is rejected before opening a PostgreSQL connection.
- PostgreSQL authentication failures distinguish an incorrect password from a
  password that was never supplied.
- Changing the password invalidates a previously successful connection test.

## Security

- Password values remain masked and are excluded from JSON serialization.
- Connection diagnostics record password presence as booleans only.
- Password values and complete connection strings are never written to status
  messages or structured connection-test logs.
