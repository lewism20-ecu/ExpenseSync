# ExpenseSync

ExpenseSync is a mobile expense reporting app for employees who need to log expenses while away from a reliable network connection. The .NET MAUI app stores expenses in a local SQLite database on the device. In later weeks, the app will sync with a central ASP.NET Core API (which has its own SQLite database) so users can share expenses.

## Week 1 Progress: Planning & Setup

### Domain entity

The app has one entity, `Expense`:

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
- The home page shows "Ready" when the local database opens, along with the file's location.

### Server API

- Created `src/ExpenseSync.Api`, an ASP.NET Core Minimal API.
- Uses Entity Framework Core with SQLite (`ExpenseSyncDbContext`). The connection string is in `appsettings.json`, and an `InitialCreate` migration creates `expensesync-server.db` with an `Expenses` table when the API starts.
- `GET /` checks that the API is running, and `GET /health` checks that it can reach the server database.
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
- The MAUI app runs on Windows, shows "Ready", and creates `expensesync.db3` with the `Expenses` table.

## Week 2 Progress: Local CRUD + Change Tracking

This week the app went from "opens a database" to a working expense list that can add, edit, and delete records on the device, with every change recorded in a `PendingChanges` table for later sync. Nothing talks to the API yet.

### Create, update, delete

The home page is now an expense list. Add Expense opens `ExpenseEditPage` with an empty form. Tapping a row opens the same page filled in, with a Delete button that asks for confirmation. Save and Delete are commands on `ExpenseEditViewModel`; they call `ExpenseDatabase` and then navigate back, and the list reloads in `OnAppearing`.

Everything reads and writes the local SQLite file only. There is no network check, so this all works with the device offline.

Two columns were added to `Expenses`:

- `LastModifiedUtc`: set to `DateTime.UtcNow` whenever the row is created, edited, or deleted.
- `IsDeleted`: deleted expenses stay in the table with this set to `1` (a tombstone) so the delete can be sent to the server later. The list only shows rows where it is `0`.

`DateTime` values are now stored as ISO text instead of ticks so the tables are readable in `sqlite3`.

### Change tracking

`PendingChanges` holds one row for each expense that has a change the server has not seen:

| Column | Notes |
|---|---|
| `PendingChangeId` | `Guid` |
| `ExpenseId` | The expense that changed |
| `Operation` | `Create`, `Update`, or `Delete`, stored as text |
| `CapturedAtUtc` | When the user made the change on this device, in UTC |
| `DeviceId` | Hardcoded to `device-01` in `DeviceSettings.cs`. Each group member builds with a different value |

Every write goes through `ExpenseDatabase`. The expense row and its pending row are written inside one `RunInTransactionAsync` call, so the database cannot end up with an edited expense and no pending row, or the other way around.

```text
ExpenseEditViewModel.SaveCommand / DeleteCommand
        |
        v
ExpenseDatabase.CreateExpenseAsync / UpdateExpenseAsync / DeleteExpenseAsync
        |   one SQLite transaction
        |-- insert or update the Expenses row, set LastModifiedUtc
        '-- insert or update the PendingChanges row for that ExpenseId
        |
        v
MainViewModel.LoadExpensesCommand reloads the list and pending count
```

Rather than keeping a history of operations, each expense has at most one pending row and the operation is adjusted as the user keeps editing:

| Pending row already there | User does | Result |
|---|---|---|
| none | create | `Create` |
| none | update | `Update` |
| none | delete | `IsDeleted = 1`, pending `Delete` |
| `Create` | update | still `Create`; `CapturedAtUtc` moves forward. The server only needs the final version |
| `Create` | delete | both rows removed. The server never knew about this expense |
| `Update` | update | still `Update`; `CapturedAtUtc` moves forward |
| `Update` | delete | `IsDeleted = 1`, pending row changes to `Delete` |

The app never removes pending rows on its own yet. That happens in Week 3 once the server acknowledges a push.

The home page shows the row count as "Pending changes (not synced yet)" so it is easy to see tracking happen without opening the database.

### Screenshots

Taken on Windows starting from a fresh database. The table dumps are `sqlite3` reading the same `expensesync.db3` file whose path is printed at the bottom of the app.

#### Before any change

| Device UI | Device tables |
|---|---|
| ![Empty expense list, pending count 0](docs/images/week2-ui-01-before.png) | ![Empty Expenses and PendingChanges tables with schema](docs/images/week2-db-01-before.png) |

Empty list, pending count 0, both tables exist with no rows. The schema dump shows the new `LastModifiedUtc` and `IsDeleted` columns and the `PendingChanges` table.

#### Create

| Add form filled in | After saving two expenses |
|---|---|
| ![Add Expense form with Client lunch / 42.50](docs/images/week2-ui-02-add-form.png) | ![List showing two expenses, pending count 2](docs/images/week2-ui-03-after-create.png) |

![Expenses has two rows, PendingChanges has two Create rows](docs/images/week2-db-02-after-create.png)

Each save added one `Expenses` row and one `PendingChanges` row with `Operation = Create`. The `ExpenseId` values line up across the two tables and `CapturedAtUtc` matches the expense's `LastModifiedUtc`.

#### Update

| Edit form (opened by tapping the row) | After saving the edit |
|---|---|
| ![Edit Expense form showing Client lunch with Delete button](docs/images/week2-ui-04-edit-form.png) | ![List showing Client lunch (2 people) $58.75, pending count still 2](docs/images/week2-ui-05-after-update.png) |

![Expenses row updated; pending row still Create with newer CapturedAtUtc](docs/images/week2-db-03-after-update.png)

The description and amount changed and `LastModifiedUtc` moved forward. This expense had never been synced, so its pending row stayed `Create` with the newer timestamp instead of adding a second row, and the count stayed at 2.

To show an update on an expense the server already has, I cleared `PendingChanges` with `sqlite3` (standing in for a finished sync, since push is Week 3) and edited the amount again:

| After editing a "synced" expense | Device tables |
|---|---|
| ![List showing $65.00, pending count 1](docs/images/week2-ui-06-after-update-synced.png) | ![PendingChanges has one Update row](docs/images/week2-db-04-pending-update.png) |

This time a pending row with `Operation = Update` was written.

#### Delete

| Confirmation | After delete |
|---|---|
| ![Delete expense? dialog for Parking](docs/images/week2-ui-07-delete-confirm.png) | ![List shows only Client lunch, pending count 2](docs/images/week2-ui-08-after-delete.png) |

![Parking row has IsDeleted = 1; PendingChanges has Update and Delete rows](docs/images/week2-db-05-after-delete.png)

Parking is gone from the list, but its row is still in `Expenses` with `IsDeleted = 1`. A `Delete` pending row was added, so the count is back to 2: one `Update`, one `Delete`.

### Files touched

- `Models/Expense.cs`: added `LastModifiedUtc` and `IsDeleted`
- `Models/PendingChange.cs`, `Models/ChangeOperation.cs`: new
- `DeviceSettings.cs`: new, holds the hardcoded `DeviceId`
- `Data/ExpenseDatabase.cs`: CRUD methods, each writing the expense and its pending row in one transaction
- `ViewModels/MainViewModel.cs`, `Views/MainPage.xaml`: list, pending count, Add button, tap to edit
- `ViewModels/ExpenseEditViewModel.cs`, `Views/ExpenseEditPage.xaml`: new add/edit page
- `AppShell.xaml.cs`, `MauiProgram.cs`: route and DI registration for the edit page

### Not done yet

- Nothing is sent to the API. `PendingChanges` only grows. Push is Week 3.
- No audit log (Week 5).
- `DeviceId` is a compile-time constant.
