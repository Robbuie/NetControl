# Part D - naming and identity

Design only. Nothing here is built yet.

The question behind this: *can a project, and each item in its plan and its log, be named well
enough that a file opened six months later still explains itself?* Most of the storage is already
there. The gaps are in the UI and in the log, and one of them is worth closing **before** the bench
session rather than after.

## What already exists

Checked in the code, not assumed:

| | Stored | Editable | Shown |
|---|---|---|---|
| Project name | `Project.Name`, and `ProjectStore.Rename` | **no UI caller** | status bar only |
| Host name | `Device.HostName` | grid | grid |
| Panel / tag ref | `Device.PanelRef` | grid | grid |
| Role ("PowerFlex 525 conveyor 3") | `Device.Role` | grid | grid |
| Notes | `Device.Notes` | grid | grid |
| Vendor | `Device.Vendor`, from the OUI table | derived, read-only | grid + log |
| Event -> device link | `Event.DeviceId` FK | n/a | not surfaced |

So per-device naming is largely done. `ProjectStore.Rename` exists and is tested, and the only
thing that calls it is `SaveAs` deriving a name from the file name. A user cannot name a project.

## The gaps, in the order they are worth doing

### D2 - a device's name in the live log - **DONE**

Built as designed, with one thing found on the way that was worth more than the column itself: the
plan index was backed by `StaticMapPolicy`, which holds only rows that became a valid assignment, so
a device planned with a MAC and no address read as a stranger in the log. `IPlanIndex` went from
`Contains(mac)` to `Find(mac)` and is now backed by `PlanIndex` over `DeviceRepository.All()`.
`PolicyPlanIndex` moved to `_trash/`. Built, tested and green.

<details>
<summary>Original design</summary>



**The gap.** `LogEntryViewModel` carries `Mac`, `Vendor`, `ArrivalAdapter`. It has no idea the plan
holds a name for that MAC. So a request from a device you spent the morning planning reads
`00:1D:9C:C7:B0:70 - Rockwell Automation`, identical to a stranger's. The one screen where naming
pays for itself is the one screen it never reaches.

**Why first.** `BENCH.md` is the next real hour, and the log is the whole output of a bench session.
Reading it against a printed plan is much easier if the rows say `Conveyor 3 drive`. This is also
the cheapest item here - no schema change at all.

**Shape.** `RequestLogViewModel` already asks the plan `is this MAC planned?` to set
`IsUnknownDevice`. Widen that one lookup to return a display name as well as the boolean, and add a
`PlannedName` column between MAC and Vendor. The name is a snapshot taken when the row is made, not
a live binding: a log row is a record of a moment, and having it change because someone edited the
grid afterwards would be wrong.

**Naming rule.** One string, computed in `DeviceRecord`, so the grid, the log and any future report
cannot disagree: `Role` if set, else `HostName`, else `PanelRef`, else empty. Put it on
`DeviceRecord` as `DisplayName`, beside `TryToAssignment`, for the same reason the amber wording
lives there.

**Files.** `DeviceRecord.cs`, `Serving/` (the plan lookup), `LogEntryViewModel.cs`,
`RequestLogViewModel.cs`, `MainWindow.xaml`.

**Tests.** Name resolution precedence; a planned device's row carries its name and a stranger's does
not; renaming a device in the grid does not retitle log rows already written.

</details>

### D1 - name the project, and say so in the title bar

**The gap.** No rename UI; window title is the constant `NetControl - BOOTP/DHCP`; the name only
appears in the status bar.

**Shape.** File -> Rename project... (a small dialog, or make the status-bar name click-to-edit),
calling the existing `ProjectStore.Rename`. Window title becomes
`{ProjectName} - NetControl` and appends ` *` - no, not a dirty marker; the existing
`(not saved to a file yet)` wording is correct and should be reused verbatim so there are not two
vocabularies for the same state.

One decision worth making deliberately: **`SaveAs` currently overwrites the display name from the
file name.** Once a user can name a project, that is wrong - it would silently discard a name they
typed. `SaveAs` should only derive the name from the file when the project is still on its default
name. That is a behaviour change to a tested path, so it needs its own test.

**Log it.** A rename is a change to the commissioning record's identity, so it goes in the `Event`
table under a project category, old name and new. Cheap, and it is exactly the sort of thing a
report is later asked about.

**Files.** `ProjectStore.SaveAs` (the name-derivation rule), `MainViewModel`, `MainWindow.xaml(.cs)`.

### D3 - a Group / Area per device

**The gap.** A project file is a flat list. One line with four panels is either four files or one
long grid.

**Shape.** Schema migration **V2**: `ALTER TABLE Device ADD COLUMN GroupName TEXT;` - append a step
to `SchemaMigrations.Steps`, bump `CurrentVersion` to 2, never touch V1. Then a `Group` column in
the grid, a group filter above it, and the same filter applied to the live log so a bench session on
Panel A is not buried by Panel B.

**Open question, and I would not guess at it.** `PanelRef` may already be this field. If in practice
you type `Panel A` there, then D3 is not a new column - it is a filter over an existing one, which
is a much smaller job and no migration at all. **This is the one item that needs your answer before
it can be designed properly**, and the honest answer probably comes from the bench, not the desk.

### D4 - export a named commissioning report

**The gap.** The event log is the product of a commissioning session and it cannot leave the file.
Nothing in `src/` mentions CSV or export.

**Shape.** Two exports off the File menu, both named from the project: the plan as CSV (which pairs
with the CSV *import* already outstanding as A5 in `PLAN-NEXT.md` - do them together, one column
list, one place to get it wrong), and the event log as CSV or a simple report. Read-only over
`EventLog`; no schema change.

### D5 - recent projects

Smallest and least important. A recent-files list on the File menu, stored in user settings, with
missing files greyed rather than removed - a project file on a disconnected site laptop still
existing is information.

## Sequencing

| | Leaves you with |
|---|---|
| D2 | A bench log that reads as device names - **done** |
| D1 | Project files that identify themselves |
| D3 | Multi-panel projects, *after* the bench answers the `PanelRef` question |
| D4 | A commissioning record that can leave the file (bundle with A5, CSV import) |
| D5 | Convenience |

## What this does not change

The layering rule holds: `DisplayName` is a `DeviceRecord` property in Core, the filter is a view
model, and nothing in Core learns that a window exists. The `Event` table stays append-only - a
rename writes a new row, it does not edit history.

## What D2 touched

`dotnet build` and `dotnet test` are green.

Files: `src/NetControl.Core/Persistence/DeviceRecord.cs`,
`src/NetControl.App/Serving/{IPlanIndex,PlanIndex,PlannedDevice}.cs`,
`src/NetControl.App/ViewModels/{LogEntryViewModel,RequestLogViewModel,MainViewModel}.cs`,
`src/NetControl.App/Views/MainWindow.xaml`,
`tests/NetControl.Tests/{FakePlanIndex,RequestLogViewModelTests,DeviceNamingTests}.cs`.
`src/NetControl.App/Serving/PolicyPlanIndex.cs` is gone - moved to `_trash/`, safe to delete.
