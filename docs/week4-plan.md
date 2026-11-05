# Week 4 Plan: Pull Sync

Week 4 plan for the three of us. Each person's section has their tasks and a prompt to paste into the agent (it already has the `expensesync` skill).

## Assignment checklist

- [x] Implement pull endpoint
- [x] Apply incoming changes
- [x] Handle deletes
- [x] README.md
  - [x] Name and description of the mobile application
  - [x] Week 1 progress documentation
  - [x] Week 2 progress documentation
  - [x] Week 3 progress documentation
  - [x] Week 4 progress documentation
- [x] Screenshots
  - [x] Server database table(s) of data needed to be downloaded to device before changes pulled
  - [x] Device table(s) before data is pulled (must include both a new record and a record that will be updated)
  - [x] Device table(s) after data is pulled
  - [x] Device table(s) after data is processed
  - [x] Server database table(s) after changes are successfully downloaded to device

Submission evidence was captured from the Windows MAUI app (`device-01`), with
API requests labeled `device-02` representing the remote device. The five
screenshots and verified test sequence are in README.md. Physical Android and
Dev Tunnel testing from the original plan below remain unverified.

## Where the project is now (end of Week 3)

- Device: `Expenses` (with `IsDeleted` tombstones) and `PendingChanges` in `expensesync.db3`.
- Server: `Expenses` and a `ReceivedChanges` inbox in `expensesync-server.db`.
- `POST /api/sync/push` stores device changes in `ReceivedChanges`; `POST /api/sync/process` (called by hand) applies them to `Expenses`.
- After a push is accepted, the device hard-deletes tombstoned expenses whose `Delete` was accepted.
- `DeviceSettings.DeviceId` is hardcoded to `device-01` on every platform.
- `SyncSettings.BaseUrl` points to `localhost` / `10.0.2.2`, so a physical Android phone cannot reach the API yet.

## Decisions

1. We use the endpoint names from the assignment: `POST /pushChanges` and `GET /changes?since=timestamp`. Week 4 adds `GET /changes` and maps `POST /pushChanges` to the existing push handler. `/api/sync/push` stays as an alias so the Week 3 evidence and the `.http` file still work.
2. No extra server tracking table. Screenshot 5 shows the server tables after the device downloaded the changes. Pulling doesn't write anything on the server, so the screenshot shows the server data unchanged and matching what the device now has.
3. Pulled changes are saved to a device `IncomingChanges` table first, then applied to `Expenses`. The server already does this with `ReceivedChanges`, and it gives us separate "after pulled" and "after processed" screenshots.
4. Conflicts wait for Week 5. If a pulled change is for an expense that still has a row in `PendingChanges`, the device skips it (marks the incoming row `Skipped`) and keeps the local edit. Last-captured-wins comes in Week 5.
5. For testing, Windows is `device-01` and the Android phone is `device-02`. The phone reaches the API through a Microsoft Dev Tunnel.

## Pull contract

All three of us agree on this first (about 30 minutes).

```http
GET /changes?since=2026-11-03T18:00:00.0000000Z
```

- `since` is optional. When it is missing, return every expense (first sync).
- `since` must be an ISO 8601 UTC timestamp. An invalid value returns a `400` validation problem.

Response:

```json
{
  "changes": [
    {
      "expenseId": "8bcf3d32-c624-4372-a0f0-2154b9233445",
      "description": "Client lunch",
      "amount": 48.75,
      "expenseDate": "2026-10-28",
      "lastModifiedUtc": "2026-10-29T03:25:11.270Z",
      "isDeleted": false,
      "lastDeviceId": "device-02",
      "serverUpdatedUtc": "2026-11-03T18:05:42.120Z"
    }
  ],
  "serverTimeUtc": "2026-11-03T18:10:00.000Z"
}
```

- Rows are filtered by a new server column, `ServerUpdatedUtc`, not by `LastModifiedUtc`. `LastModifiedUtc` comes from the device clock, so an offline change pushed late could have a time earlier than another device's last sync and would never be downloaded.
- Deleted expenses are returned with `isDeleted: true` (tombstones) so other devices learn about deletes.
- `serverTimeUtc` is read before the query runs. The device stores it as its next `since` value only after every change has been applied.
- The device's own changes come back too. Applying them is harmless because the values are the same.

## Who does what

| Person | Area | Main files |
|---|---|---|
| A | API: `GET /changes`, `POST /pushChanges`, `ServerUpdatedUtc` | `ExpenseSync.Api/Endpoints/SyncEndpoints.cs`, `Models/Expense.cs`, `Data/*`, `ExpenseSync.Api.http` |
| B | App: `IncomingChanges`, `SyncState`, pull + apply, Sync now flow | `ExpenseSync.App/Data/ExpenseDatabase.cs`, `Models/*`, `Services/SyncService.cs`, `ViewModels/MainViewModel.cs` |
| C | Dev Tunnel, device IDs, test script, screenshots, README | `Services/SyncSettings.cs`, `DeviceSettings.cs`, `README.md`, `docs/images/week4-*` |

Order:

1. All three agree on the contract above.
2. A and B build at the same time against the contract. C sets up the Dev Tunnel and device IDs, and drafts the README section and test script.
3. A merges first (B needs the endpoint to test end to end), then B.
4. C runs the test script on Windows + Android and captures screenshots, then finishes the README.

---

## Person A: API

### Tasks

1. Add `ServerUpdatedUtc` (`DateTime`, UTC) to the server `Expense` model with a migration. Backfill existing rows (for example, copy `LastModifiedUtc`) so the first pull returns them.
2. In `/api/sync/process`, set `ServerUpdatedUtc = DateTime.UtcNow` every time a row is inserted, updated, or tombstoned.
3. Add `GET /changes?since=` as described in the contract. Include tombstones. Read `serverTimeUtc` before the query.
4. Map `POST /pushChanges` to the existing push handler. Keep `/api/sync/push`.
5. Add sample requests to `ExpenseSync.Api.http`: pull with no `since`, pull with a `since`, and a `device-02` push that creates one expense and updates another (for testing without the phone).
6. Build, run the API, and check the endpoint with the `.http` file or `curl`.

### Prompt

```text
Week 4 pull sync, API side. Read docs/week4-plan.md first and follow the
"Decisions" and "Pull contract" sections exactly.

In src/ExpenseSync.Api:
1. Add a ServerUpdatedUtc (DateTime, UTC) column to the server Expense model
   with an EF Core migration. Backfill existing rows from LastModifiedUtc.
2. In the existing /api/sync/process handler, set ServerUpdatedUtc to
   DateTime.UtcNow whenever an expense is inserted, updated, or tombstoned.
3. Add GET /changes?since=<ISO 8601 UTC> in Endpoints/SyncEndpoints.cs.
   since is optional (missing = return everything). Invalid since returns a
   400 validation problem. Return expenses where ServerUpdatedUtc > since,
   including IsDeleted tombstones, plus serverTimeUtc read before the query.
   Use the exact JSON shape from the plan. Add OpenAPI metadata like the
   existing endpoints.
4. Map POST /pushChanges to the existing push handler and keep
   /api/sync/push as an alias.
5. Add sample requests to ExpenseSync.Api.http: pull with no since, pull with
   a since, and a device-02 push that creates one new expense and updates an
   existing one.

Do not change the device app. Build the API, run it, and verify each new
request returns the expected data. Tell me what you ran and the results.
```

---

## Person B: App

### Tasks

1. Add two device tables (created in `ExpenseDatabase.InitializeAsync`):
   - `SyncState`: one row with `LastSyncUtc` (nullable).
   - `IncomingChanges`: `IncomingChangeId` (Guid PK), `ExpenseId`, `Description`, `Amount`, `ExpenseDate`, `LastModifiedUtc`, `IsDeleted`, `LastDeviceId`, `ServerUpdatedUtc`, `ReceivedAtUtc`, `IsProcessed`, `ProcessedAtUtc`, `Result` (`Inserted` / `Updated` / `Deleted` / `Skipped`). Processed rows are kept for evidence, like the server's `ReceivedChanges`.
2. `SyncService.PullAsync`: read `LastSyncUtc`, call `GET changes?since=`, and save every change to `IncomingChanges` with `IsProcessed = 0`.
3. `ExpenseDatabase.ApplyIncomingChangesAsync`: for each unprocessed row, ordered by `ServerUpdatedUtc`:
   - Expense has a `PendingChanges` row: `Skipped` (Week 5 handles conflicts).
   - `IsDeleted` and the local row exists: mark the local row `IsDeleted = 1` so the UI hides it. Result `Deleted`.
   - `IsDeleted` and no local row: nothing to do. Result `Deleted`.
   - New `ExpenseId`: insert. Result `Inserted`.
   - Existing `ExpenseId`: overwrite the fields and `LastModifiedUtc`. Result `Updated`.
   - Never write a `PendingChanges` row here (prevents pushing the same change back).
   - Set `IsProcessed = 1` and `ProcessedAtUtc`.
4. Only after every row is processed, save the response's `serverTimeUtc` as `LastSyncUtc`. If the pull or apply fails, keep the old value.
5. Switch the push path to `pushChanges`.
6. Sync now runs push, then pull, then apply, and shows one status message, for example "Sent 2, received 3 change(s)." Refresh the list and pending count afterward.

### Prompt

```text
Week 4 pull sync, device side. Read docs/week4-plan.md first and follow the
"Decisions", "Pull contract", and "Person B" sections exactly.

In src/ExpenseSync.App:
1. Add sqlite-net models and tables for SyncState (single row, nullable
   LastSyncUtc) and IncomingChanges (columns listed in the plan). Create them
   in ExpenseDatabase.InitializeAsync.
2. Add SyncService.PullAsync that reads LastSyncUtc, calls
   GET changes?since=<utc> with the existing typed HttpClient, and saves every
   returned change to IncomingChanges with IsProcessed = 0. Handle
   HttpRequestException, timeouts, and JsonException the same way PushAsync
   does.
3. Add ExpenseDatabase.ApplyIncomingChangesAsync that applies unprocessed
   IncomingChanges in ServerUpdatedUtc order using the rules in the plan
   (insert / update / tombstone / skip when the expense has a PendingChanges
   row). It must never create PendingChanges rows. Mark each row processed
   with ProcessedAtUtc and Result. Use a transaction where it keeps the
   Expenses and IncomingChanges updates together.
4. Save serverTimeUtc as LastSyncUtc only after every incoming row was
   processed. On any failure keep the old LastSyncUtc.
5. Change the push path from api/sync/push to pushChanges.
6. Make the Sync now command run push, then pull, then apply, through one
   SyncService method, and show a combined status message. Keep the button
   disabled while syncing and refresh the list and pending count afterward.

Store dates in UTC. Keep the existing MVVM and DI patterns. Build the app,
run it on Windows against the local API, and verify with DevFlow and by
reading expensesync.db3 that a pulled new record is inserted, a pulled
update overwrites the local row, a pulled delete hides the row, and no
PendingChanges rows are created by the pull. Tell me what you verified.
```

---

## Person C: Dev Tunnel, test script, screenshots, README

### Tasks

1. Device IDs: make `DeviceSettings.DeviceId` `device-02` on Android and `device-01` elsewhere.
2. Dev Tunnel: host the API (port 5157) on a Dev Tunnel with anonymous access for development. Put the tunnel URL in `SyncSettings.BaseUrl` for Android. Check the three layers: PC → local API, PC → tunnel URL, phone → tunnel URL.
3. Test script (write it in the README draft before taking screenshots):
   1. Fresh server DB and fresh device DBs.
   2. On Windows (`device-01`): create "Client lunch" ($42.50) and "Parking" ($18.00), Sync now, then call `/api/sync/process`.
   3. On Android (`device-02`): Sync now so the phone downloads both expenses. The phone now has "Client lunch" locally (the record that will be updated).
   4. On Windows: add "Hotel - conference night" ($189.00) (new record), change Client lunch to $48.75 (update), delete Parking (delete). Sync now, then call `/api/sync/process`.
   5. **Screenshot 1:** server `Expenses` showing Hotel, Client lunch $48.75, and Parking `IsDeleted = 1` with `ServerUpdatedUtc` newer than the phone's `LastSyncUtc`.
   6. **Screenshot 2:** phone `Expenses` (Client lunch $42.50 and Parking, no Hotel), empty `IncomingChanges`, and `SyncState.LastSyncUtc`.
   7. On Android: Sync now.
   8. **Screenshot 3:** phone `IncomingChanges` with 3 new rows. To capture the state between pull and apply, use a breakpoint on `ApplyIncomingChangesAsync` (or a temporary debug option, then remove it).
   9. **Screenshot 4:** phone `Expenses` with Hotel inserted, Client lunch $48.75, Parking hidden, `IncomingChanges` rows processed with `Result`, `PendingChanges` empty, and `LastSyncUtc` advanced.
   10. **Screenshot 5:** server `Expenses` and `ReceivedChanges` after the download, unchanged and matching the phone.
   11. Optional UI screenshots of the phone list before and after.
4. Copy the device database off the phone to read it on the PC (for example, `adb exec-out run-as <package> cat files/expensesync.db3 > device.db3`, or the DevFlow file tools).
5. README: add "Week 4 Progress: Pull Sync" in the Week 3 style (what changed, endpoint and JSON, flow diagram, apply rules, screenshots with captions, other testing, files touched, not done yet). Update the Week 3 "Not done yet" list. Check that the name/description and Weeks 1–3 are still accurate.

Screenshot files:

| # | File |
|---|---|
| 1 | `docs/images/week4-db-01-server-before.png` |
| 2 | `docs/images/week4-db-02-device-before.png` |
| 3 | `docs/images/week4-db-03-device-after-pull.png` |
| 4 | `docs/images/week4-db-04-device-after-process.png` |
| 5 | `docs/images/week4-db-05-server-after.png` |

### Prompt 1: Dev Tunnel and device IDs (before A and B merge)

```text
Week 4 setup. Read docs/week4-plan.md, "Person C".

1. Change DeviceSettings.DeviceId so Android uses "device-02" and every other
   platform uses "device-01".
2. Help me host the ExpenseSync API (port 5157) on a Microsoft Dev Tunnel
   with anonymous access for development, and set SyncSettings.BaseUrl to the
   tunnel URL on Android (keep localhost on Windows). Keep the URL in
   SyncSettings only. Do not commit any tunnel tokens.
3. Walk me through checking the three layers: PC to local API, PC to tunnel
   URL, and my physical Android phone to the tunnel URL. Run the app on the
   phone and confirm Sync now reaches the API (check with DevFlow network
   logs).
```

### Prompt 2: Evidence and README (after A and B merge)

```text
Week 4 evidence and README. Read docs/week4-plan.md, "Person C", and follow
the test script exactly. Windows is device-01, my physical Android phone is
device-02 through the Dev Tunnel.

1. Reset the server database and both device databases.
2. Walk through the test script step by step. At each screenshot step, dump
   the relevant tables (server expensesync-server.db and the phone's
   expensesync.db3, copied off the phone) in the same style as the Week 3
   screenshots, and save them as docs/images/week4-db-0N-*.png using the
   filenames in the plan. Show GUIDs in lower case like Week 3.
3. Confirm each screenshot shows what the plan says it should before moving
   on (for example, screenshot 2 has Client lunch at $42.50 and no Hotel).
4. Add a "Week 4 Progress: Pull Sync" section to README.md in the same
   style as Week 3: what changed, the GET /changes endpoint and JSON, a flow
   diagram, the apply rules (insert, update, tombstone, skip), the five
   screenshots with a caption explaining what each one proves, other
   testing, files touched, and not done yet. Update the Week 3 "Not done
   yet" list. Check that the app name/description and the Weeks 1-3 sections
   are still accurate. Describe only what the code actually does.
```

## Done when

- [x] `GET /changes` and `POST /pushChanges` work on the API.
- [x] Sync now on Windows downloads a new expense, an update, and a delete sent as `device-02`.
- [x] Pulled changes do not create `PendingChanges` rows.
- [x] `LastSyncUtc` advances only after a successful apply.
- [x] The five screenshots are in `docs/images` and the README has the Week 4 section.
- [ ] Optional follow-up: repeat the workflow on a physical Android phone through a Dev Tunnel.
