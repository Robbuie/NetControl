# Part F - importing the IP address workbook as the plan

A working document, in the shape of [PLAN-TFTP.md](PLAN-TFTP.md). **Status: F1, F2 and F3 are all written - without an SDK,
so not yet compiled or run. See "Build and test" at the end.**

## The goal, stated plainly

The project IP workbook (`Flint IP Addresses - Rev1 (v4).xlsx`: one tab per cell - SI100, SF101,
SF102, SF103 - with Device / IP / IP2 in columns D / E / F) becomes the plan, **one cell at a time**,
without anybody retyping it. The MACs the workbook does not have are filled in afterwards, from the
network, as devices are connected.

## Why it does not import today

| | Workbook | NetControl plan | Decision |
|---|---|---|---|
| 1 | No MAC anywhere - it comes from drawings | `Device.Mac` is `NOT NULL UNIQUE`; import refuses a file without a MAC column | **Allow MAC-less rows** (F1) |
| 2 | SF101/102/103 reuse the same 120.15.106.x addresses | Duplicate IP anywhere refuses the import | **One cell per import** - the project is the cell |
| 3 | No mask or gateway | A row needs IP + mask to be servable | Asked once per subnet in the import dialog (F2) |
| 4 | IP2 = second port, so a second MAC | One row per MAC | IP2 becomes its own row, `<Device> (IP2)` |
| 5 | `.xlsx` | CSV only | Read `.xlsx` with `System.IO.Compression` + `XmlReader` - **no new package** |

## F1 - a plan row without a MAC

A row with a name and a planned address and no MAC is **awaiting MAC**. It is shown in the grid
(amber, "awaiting MAC"), compared against scans, and **never served** - which the existing rule
already guarantees, because `TryToAssignment` needs a usable MAC.

- Schema V4: rebuild `Device` with `Mac TEXT NULL` and a partial unique index
  `ON Device(Mac) WHERE Mac IS NOT NULL`. Rows are identified by `Id`, not MAC, everywhere that
  currently assumes the MAC is the key (`DeviceRepository.Delete`, the grid's `PlanDevice`,
  `NoteSeen`/`NoteServed`).
- `PlanValidation`: a blank MAC stops being a problem **when the row has a planned IP**. A row with
  neither is still refused (or, from the workbook, skipped - see F2). Duplicate-IP checking is
  unchanged and still refuses.
- `PlanCsv`: the Mac column is no longer mandatory; CSV import and export carry MAC-less rows too.

## F2 - the workbook reader

- File, Import plan accepts `.xlsx`. The dialog lists the cell tabs (from the workbook's
  *Formula Builder* table when present: Include = Yes, sheet, Name/IP/IP2 columns, first row) and
  the user picks **one**.
- Reads cached cell values, so formulas need Excel to have saved the file (it always has).
- Rows: Device -> HostName, Notes -> Notes, `<sheet> <Switch>-<Port>` -> PanelRef, sheet name ->
  Role prefix left alone. A row with no IP (spare ports, "Molex Port n") is skipped and counted in
  the summary, not reported as a problem.
- The same name on the same IP twice (the switch header row and its port-map row, e.g. `SF101ES1`
  at .13 on rows 12 and 46) is one device, not a duplicate.
- Mask / gateway: the dialog lists each /24 the cell uses (SF101: 120.15.106, 192.168.1,
  192.168.2, 192.168.5) with mask defaulted to 255.255.255.0 and gateway blank.
- Then it goes through `PlanValidation` like any CSV: **all or nothing, every problem at once.**

**This will refuse the current workbook, correctly.** Within each SF cell, `SCC1.EN1`/`EN2` share
120.15.106.135 and `C1.EN0`/`EN2` share .137 - already on the Revision History open-items list.
They must be fixed in the workbook (or one of each pair blanked) before the import goes through.

## F3 - filling the MACs in from the network

**Never silently.** A wrong MAC on a row hands that address to the wrong device. Every fill is
proposed, shown, confirmed, and written to the event log as `Plan` with the source.

1. **From the live BOOTP log** - the main path, for new devices with no address yet. A request from
   an unplanned MAC already offers *Plan device*; it gains *Assign to awaiting row...*, a picker of
   MAC-less rows (name, planned IP, PanelRef). Picking one writes the MAC onto that row. The device
   is then servable on its next retransmit, behind the existing serve arming.
2. **From a scan or sweep** - for devices already sitting at their planned address (plant switches,
   anything configured before). New `PlanFindingKind.AwaitingMacFound`: a MAC-less row's planned
   address answered, and ARP resolved a MAC (`CanBePlanned` - same segment only). The scan results
   offer *Fill in MAC* per row and *Fill in all n* with a confirm listing every pairing.
   **Fixes a latent bug:** today a MAC-less row whose address answers is reported as
   `AddressTakenByAnother` (Error), because `occupant.Mac == row.Mac` compares against Empty.
3. Not filled: an unattributable or contested address (`AddressUnattributable`, `ContestedAddress`)
   - those stay findings. A MAC already on another row is refused with that row named.

## Order of work

1. Schema V4 + repository by Id + `PlanValidation` / `PlanCsv` changes, with tests.
2. `AwaitingMacFound` in `PlanConformance` + tests (including the latent-bug case).
3. Grid: awaiting-MAC state; log: *Assign to awaiting row...*; scan: *Fill in MAC*.
4. `.xlsx` reader (`Core/Plan/PlanWorkbook.cs`) + import dialog (sheet pick, mask/gateway per subnet).
5. BENCH.md run: import SF101 from the real workbook, plug the simulator in, fill its MAC from the
   log and from a scan.

## Rev3 workbook (2026-11-04)

`Flint IP Addresses - Rev3.xlsx` dropped the Formula Builder and split the addresses into Final
IP1/IP2 and Temp IP1/IP2, with IP2 Type and Dev Port columns. The reader now:

- Finds a header by `Final IP1` (and `Final IP2`) as well as `IP` / `IP2`.
- Never offers a hidden tab (Calc), and when any detected sheet is a port map (Switch or Port
  column), offers only the port maps - the Net tabs and the IP List each have an IP / Device header
  but mix every cell, and would import cleanly as the wrong plan.
- **Temp before Final** (Robert, 2026-11-04): Temp IP1/IP2 when filled, else Final; the final goes
  in Notes (`Temp address - final x`). Re-import after cutover.
- **IP2 Type "Same port - ..."** = a second address on the same MAC. Not a row; recorded on the main
  row's Notes (`Also x on the same port (...)`). Not duplicate-checked by the import. When it is
  the row's only address it becomes the row's address. "Port 2 - ..." IP2s are rows as before.
- Dev Port is appended to PanelRef: `SF101 Direct (Motion)`.

Expected on Rev3 (by a Python port of the reader): offered SI100, SF101, SF102, SF103 only.
SF101 60 rows, SF102 62, SF103 62, SI100 21. Still refused, correctly: SCC1.EN1/EN2 on .135 (all
SF sheets), C1.EN0/EN2 on .137 (SF101, SF103 - SF102's now plan on their temps), SI100 DV200LRVE1
`120.7.164.###`.

## Open questions

- 192.168.2.x appears on every SF sheet but has no grid tab in the workbook - intentional?
- Should *Assign to awaiting row* filter the picker by the requesting device's vendor (OUI) against
  the row name? Cheap, but a guess - left out until it is asked for.

## Build and test

Written without an SDK. In order:

1. `dotnet build NetControl.sln -c Release` - warnings are errors in `src/`.
2. `dotnet test tests/NetControl.Tests` - the new tests are `PlanWorkbookTests`, and additions to
   `DeviceRepositoryTests`, `PlanValidationTests`, `PlanCsvTests`, `PlanConformanceTests`,
   `ProjectStoreTests` (the V3 -> V4 migration), `DeviceRowViewModelTests`,
   `DeviceGridViewModelTests` and `MainViewModelPlanFileTests`.
3. **On a copy** of a real 0.12.0 project file: open it, check the plan and the record are intact.
   0.12.0 cannot open it afterwards.
4. Import `Flint IP Addresses - Rev1 (v4).xlsx`, sheet SF101. Expect a refusal listing rows 16/17
   (SCC1.EN1/EN2 on .135) and 18/20 (C1.EN0/EN2 on .137). SI100 is refused on DV200LRVE1's
   placeholder address `120.7.164.###`. Fix them in a copy of the workbook and import again:
   SF101 should give 60 rows awaiting a MAC (2 repeats merged, 18 spares skipped).
5. With `NetControl.DeviceSim` asking for an address: right-click its request > Assign to awaiting
   plan row, pick a row, and check the row turns servable and the record says "MAC filled in".
6. With the simulator holding a planned address: Scan, then Tools > Fill in awaiting MACs from the
   scan.
