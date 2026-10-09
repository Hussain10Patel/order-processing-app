# React + Vite

## Delivery workflow and deployment

- Production confirms the scheduled delivery date before assignment. The saved
  `Order.DeliveryDate` is the selected schedule date; `OriginalCsvDeliveryDate`
  is captured on new CSV imports and is never changed by scheduling. Assignment
  atomically saves the date, assignment flag, Scheduled status and one delivery
  schedule. Existing assigned Approved orders can confirm their date to enter
  this lifecycle.
- Production / Delivery owns departure planning: Set En Route asks only for a
  departure time (HH:mm, Johannesburg time) and positive duration hours, including
  decimals. It uses the existing saved scheduled date, never asks for the date
  again, and never uses an unsaved planner date draft. The backend combines that
  date and local time and persists UTC departure and expected timestamps.
  `EnRouteAtUtc` now represents the saved departure, including future departures;
  the persisted status remains Scheduled until the worker reaches departure.
  Existing En Route timestamps retain their meaning. Repeating the same request
  does not restart the timer. Pending departures can be edited before departure.
  The request contract is `{ "departureTime": "10:30", "durationHours": 2.5 }`;
  the server derives the date from the order, not from request data.
- Unassign or Unschedule returns Scheduled orders to Approved, clears the
  assignment and schedule, and preserves both dates and production data. These
  operations and scheduled-date edits are blocked at the saved departure time,
  even before the next worker check, and after En Route or Delivered. Cancelling
  before departure clears the pending departure/duration/expected timestamps.
  Changing the scheduled date before departure retains the same Johannesburg
  departure time and duration, recalculating the UTC timestamps on the new date.
  Unassign/unschedule before adjusting or deleting a Scheduled order.
- Reports use persisted order statuses, not inferred production quantities or
  schedule existence. Date/range queries use the saved schedule date, each order
  is counted once, and supplier names are drawn from existing item metadata.
  Automatic Delivered labels are **estimates**, not proof of physical receipt.
- Existing planner row sequence, production events and stock arithmetic are
  retained when delivery status changes. Invoice and delivery exports include
  the new lifecycle statuses. No global zoom or CSS changes are required.

### Backend prerequisites

Deploy the backend and frontend together. The EF migration
[AddDeliveryLifecycle](../OrderProcessingApp/Migrations/20261009105028_AddDeliveryLifecycle.cs)
adds nullable original-date/timing fields, an estimated-delivery marker and a
status/assignment index. Existing enum values 1-7 and the scheduled-date field
are retained. Application startup already runs `Database.MigrateAsync`; the
deployment database account must have migration permissions. Back up the
database before deployment. The migration is not applied to the hosted database
by local tests.

Historical original CSV dates remain null: an already edited schedule date
cannot safely be treated as the original imported date. This migration does not
invent original dates or rewrite historical statuses. Review legacy assignments
and confirm their dates through Production.

The departure-time update reuses the existing lifecycle fields and requires no
additional migration. Deploy backend and frontend together because the dispatch
request now requires `departureTime`; older clients sending only hours will
receive a validation error rather than starting delivery immediately.

[Backend configuration](../OrderProcessingApp/appsettings.json):

```json
"DeliveryLifecycle": {
  "BusinessTimeZone": "Africa/Johannesburg",
  "PollIntervalSeconds": 60
}
```

The registered `DeliveryLifecycleWorker` checks on startup and every 60 seconds:

- Assigned Scheduled orders with a saved departure remain Scheduled before it,
  become En Route at/after departure, and become Delivered (estimated) at/after
  the saved expected timestamp. A startup check after both thresholds catches up
  through both transitions in one atomic save. Long deliveries are not completed
  merely because the scheduled day has ended.
- Assigned Scheduled orders with a delivery schedule, no saved departure,
  and a scheduled date before today's Johannesburg calendar date become
  Delivered (estimated). They cannot be completed during their scheduled day.
- Assigned En Route orders with a delivery schedule become Delivered (estimated)
  when their saved UTC expected timestamp is reached.
- Status/date/assignment concurrency checks prevent stale transitions from
  overwriting a competing change. Database failures are logged and retried.

For a saved 16 October date, 10:30 departure and 2.5 hours, the persisted UTC
departure is 08:30 and expected arrival is 11:00 (13:00 Johannesburg). Eligibility
changes exactly at those timestamps; persistence occurs on the first worker
check at/after the boundary, normally within the 60-second interval. This is not
a second-precision scheduler. Browser refreshes display persisted statuses only
and never perform delivery transitions.

**The backend must remain running.** A sleeping/free Render web service cannot
run a hosted worker while suspended. Use an always-on hosting plan or a dedicated
always-on backend worker deployment. Persisted timestamps allow catch-up after
restart, but do not provide on-time execution while the host is stopped.
Configure `DeliveryLifecycle__BusinessTimeZone` and
`DeliveryLifecycle__PollIntervalSeconds` to override settings, and verify the
startup log (`Delivery lifecycle worker started`) and subsequent check logs on
the deployed host. A bad timezone fails startup rather than silently using UTC.

Operational pages refresh statuses every 60 seconds and on window focus;
production date drafts and planner quantity edits are preserved by status-only
refreshes. Delivery timestamps are displayed in the configured business timezone,
while calendar dates remain timezone-free.

### Validation

From the repository root:

```powershell
dotnet test OrderProcessingApp.Tests\OrderProcessingApp.Tests.csproj
Set-Location frontend
npm run build
```

Lifecycle tests use an isolated SQLite relational database (including assignment
rollback, concurrency, day boundaries, decimal durations, timer persistence,
exact departure/arrival boundaries, cancellation/rebasing, report counts and the
real hosted worker restarting before/after arrival). Existing tests also cover CSV import,
planner stock and production/delivery regressions. Browser checks must mock
write APIs or use a local test backend; do not dispatch test orders on the hosted
production API.

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend using TypeScript with type-aware lint rules enabled. Check out the [TS template](https://github.com/vitejs/vite/tree/main/packages/create-vite/template-react-ts) for information on how to integrate TypeScript and [`typescript-eslint`](https://typescript-eslint.io) in your project.
