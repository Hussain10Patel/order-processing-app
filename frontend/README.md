# React + Vite

## Delivery workflow and deployment

- Production confirms the scheduled delivery date before assignment. The saved
  `Order.DeliveryDate` is the selected schedule date; `OriginalCsvDeliveryDate`
  is captured on new CSV imports and is never changed by scheduling. Assignment
  atomically saves the date, assignment flag, Scheduled status and one delivery
  schedule. Existing assigned Approved orders can confirm their date to enter
  this lifecycle.
- Production / Delivery owns dispatch: Set En Route requires positive hours
  (including decimals). The backend saves UTC start and expected timestamps once.
  Repeating the same dispatch request does not restart the timer.
- Unassign or Unschedule returns Scheduled orders to Approved, clears the
  assignment and schedule, and preserves both dates and production data. These
  operations and scheduled-date edits are blocked after En Route or Delivered.
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

[Backend configuration](../OrderProcessingApp/appsettings.json):

```json
"DeliveryLifecycle": {
  "BusinessTimeZone": "Africa/Johannesburg",
  "PollIntervalSeconds": 60
}
```

The registered `DeliveryLifecycleWorker` checks on startup and every 60 seconds:

- Assigned Scheduled orders with a delivery schedule, no saved En Route start,
  and a scheduled date before today's Johannesburg calendar date become
  Delivered (estimated). They cannot be completed during their scheduled day.
- Assigned En Route orders with a delivery schedule become Delivered (estimated)
  when their saved UTC expected timestamp is reached.
- Status/date/assignment concurrency checks prevent stale transitions from
  overwriting a competing change. Database failures are logged and retried.

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
report counts and the real hosted worker). Existing tests also cover CSV import,
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
