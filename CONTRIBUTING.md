# Contributing to Query Gate

Issues, documentation improvements and code contributions are welcome. Before starting a large change, open an [issue](https://github.com/arjunreddy-001/query-gate/issues) to describe the problem and proposed approach.

## Set up locally

1. Install the .NET 10 SDK and make a SQL Server instance available for the application database. Use an isolated development database, not production data.
2. Clone the repository and configure the application database connection and bootstrap administrator as described in the [README](./README.md). Keep connection strings, credentials and keys out of commits.
3. Apply migrations and run the application using the instructions in the README. See the [deployment guide](./docs/DEPLOYMENT.md) for additional configuration and hosting details.

## Make a change

1. Create a branch for your change. Keep changes focused and update relevant documentation and tests.
2. Build and run the tests:

   ```bash
   dotnet build DbAccessManager.csproj
   dotnet test tests/DbAccessManager.Tests/DbAccessManager.Tests.csproj
   ```

3. Open a pull request describing the change, how to verify it and any configuration or migration implications. Include screenshots for relevant UI changes, using only fictional or sanitized data.

When reporting a problem, include reproduction steps and relevant environment details. Remove secrets and sensitive data from logs and screenshots.
