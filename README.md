# ExpenseSync

ExpenseSync is an offline-capable mobile expense reporting app for field employees and teams who need to record expenses where they don't always have a network connection. The .NET MAUI app keeps expenses in a local SQLite database on the device. A central ASP.NET Core API with its own SQLite database will be used to share expenses between users once synchronization is added in later weeks.

## Week 1 Progress — Planning & Setup

### Domain entity

The app is built around a single entity, **Expense**:

| Field | Type | Notes |
|---|---|---|
| `ExpenseId` | `Guid` | Generated on the device, so an expense can be created without contacting the server |
| `Description` | `string` | What the expense was for |
| `Amount` | `decimal` | Expense amount |
| `ExpenseDate` | date | Day the expense happened |

### MAUI project

- Created `src/ExpenseSync.App` from the .NET 11 MAUI template, using AppShell.
- Added CommunityToolkit.Mvvm. The home page (`MainPage`) gets `MainViewModel` through constructor injection, and both are registered in `MauiProgram.cs`.

### Local SQLite

- Added `sqlite-net-pcl`.
- `Data/ExpenseDatabase.cs` opens `expensesync.db3` in `FileSystem.AppDataDirectory` with `SQLiteAsyncConnection` and creates the `Expenses` table.
- The home page shows whether the local database opened (**Ready**) and where the file is stored.

### Server API

- Created `src/ExpenseSync.Api`, an ASP.NET Core Minimal API.
- Uses Entity Framework Core with SQLite (`ExpenseSyncDbContext`). The connection string is in `appsettings.json`, and an `InitialCreate` migration creates `expensesync-server.db` with an `Expenses` table when the API starts.
- `GET /` confirms the API is running and `GET /health` confirms it can reach the server database.
- The MAUI app does not connect to the API yet.

### Solution layout

```text
ExpenseSync/
├── src/
│   ├── ExpenseSync.App/   .NET MAUI app + local SQLite
│   └── ExpenseSync.Api/   ASP.NET Core API + server SQLite
├── global.json            .NET 11 SDK version
└── ExpenseSync.slnx
```

### Verified

- The solution builds with no warnings or errors.
- The API starts, creates the server database, and `/health` reports the database as connected.
- The MAUI app runs on Windows, shows the local database as **Ready**, and creates `expensesync.db3` with the `Expenses` table.
