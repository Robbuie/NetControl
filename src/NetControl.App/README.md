# NetControl.App

The WPF front end. It is a thin shell over `NetControl.Core`: everything it shows comes from a
Core event or a Core repository, and nothing in here opens a socket or a database connection.

```powershell
dotnet run --project src/NetControl.App

# The single exe that gets copied onto a plant laptop
dotnet publish src/NetControl.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Layout

```
Composition/   the object graph, and the one abstraction over the WPF dispatcher
Diagnostics/   readiness grading, and the port + firewall probe run off the UI thread
Help/          the user guide: the repo README, embedded, read into blocks for HelpWindow
Serving/       the listener's lifetime, and "is this MAC in the plan?"
ViewModels/    all the logic. No WPF types - see below
Views/         XAML, two value converters, the file dialogs
```

## Why the view models have no WPF types in them

The roadmap asks for UI logic that is testable without a window, and `NetControl.Tests`
references this project to do exactly that. That only works if constructing a view model does not
drag in a message pump, so:

- Core raises its events on the DHCP receive loop's thread. `MainViewModel` is the only place
  that marshals, and it does so through `IUiDispatcher` rather than `Dispatcher` directly. Tests
  substitute an implementation that runs inline.
- `InterfaceBarViewModel` and `RequestLogViewModel` assume they are already on the UI thread and
  say so. Keeping the marshalling in one place means there is one thing to get right.
- Filtering the log is done in the view model with two collections rather than with a WPF
  `CollectionView`, which would have put a `PresentationFramework` type in a view model's API.

`WpfDispatcher` uses `BeginInvoke`, never `Invoke`. `Invoke` would park the receive loop until the
UI thread got round to it, and a receive loop that is not reading the socket is a tool that misses
requests.

## The three things worth knowing about the interface bar

1. **Grey is not green.** `ReadinessState` is ordered so that `Unknown` sorts *worse* than
   `Ready`, which is what lets the overall grade be a plain maximum and still never report a
   check it could not run as good news. A firewall that could not be read is not a firewall that
   is open.
2. **It grades rather than summarises.** A wildcard bind by VMware and the Hyper-V Default Switch
   holding its own address are both "port in use". Showing them the same way is what trains
   people to ignore the warning, so `PortConflictSeverity` is carried through to the colour.
3. **The user's adapter choice survives the adapter.** `AdapterOption` is keyed on interface index
   and outlives its `NicInfo`, so unplugging a USB adapter greys the row instead of silently
   clearing the selection, and replugging it makes the tool work again with no click and no
   restart.

## The plan grid

`DeviceGridViewModel` over `ProjectStore.Devices`, one `DeviceRowViewModel` per row.

**There is no Save button, and there is no dirty flag.** SQLite writes as the plan is typed, so a
cell that has been left is already in the file. The only thing a project can be missing is a
*location*, which is what `File > Save as` gives it - the status bar says "not saved to a file yet"
rather than pretending there is unsaved work. `ProjectStore.SaveAs` carries the plan, the
assignments and the whole event log recorded before anyone picked a filename across together, and
counts the rows afterwards to prove it. It refuses to write over an existing file; the confirmation
belongs to the dialog.

**Every cell is text.** Half of `192.168.1.` is not an `IPAddress`, and binding a typed property
would have WPF swallow the value and paint a red box with no sentence in it. The row keeps what was
typed and does its own parsing, so it can say `'192.168.1.999' is not an IPv4 IP address` while the
mistake is still on screen.

**Two kinds of wrong, kept apart.** `Problem` (red) means nothing was written. `ServeNote` (amber)
means the row stored fine and will not be answered - a MAC copied off a label at 7am with the
address still to be decided is a normal row, not an error, and colouring the two the same is how a
grid full of amber gets ignored. The amber wording comes from `DeviceRecord.TryToAssignment`, the
same source the plan summary uses, so the row and the log can never disagree about what is wrong.

**Editing while serving is allowed, and is the normal workflow.** You watch a device ask, you plan
it, and it is served on its next retransmit. `StaticMapPolicy` is a concurrent dictionary for
exactly this; the grid raises `PlanChanged` and `MainViewModel.ReloadPlan` rebuilds what the receive
loop reads. That reload does *not* log plan problems - every cell the user leaves triggers it, and a
log line per keystroke would bury the requests the log exists to show.

`Vendor` is resolved through `IOuiLookup` at the moment a MAC is entered and stored with the row,
never looked up on read. The IEEE registry moves; a project file has to read the same way in six
months.

## What a device is called

`DeviceRecord.DisplayName` is the single rule: `Role`, else `HostName`, else `PanelRef`, else
nothing. It lives in Core rather than in a view model so the grid, the live log and any report that
follows cannot disagree - the same reason the amber serve wording lives there.

The log's **Device** column carries that name, and carries it as a *snapshot* taken when the row was
made. A log row is an account of a moment; renaming a device in the grid must not reach back and
retitle the requests it made before it had that name.

`IPlanIndex` answers "is this MAC in the plan, and what did we call it". It is backed by
`PlanIndex`, rebuilt from `DeviceRepository.All()` in the same breath as the policy in
`MainViewModel.ReloadPlan`. It used to be backed by `StaticMapPolicy` instead, which was wrong in a
way worth remembering: **the policy holds only rows that became a valid assignment**, so a device
planned with a MAC and no address - exactly what "Add to plan" produces - was absent from it, and
the log filed that device as a stranger for as long as it took to type an address. "May I serve
this" and "did somebody type this in" are different questions.

## Planning a device from the log

Double-click a row in the live request log, or right-click it and choose "Add to plan", and the
device that sent it is planned with its MAC and vendor filled in. That is the loop the window's
layout exists for - plan above, wire below, both visible at once - and it is the reason you never
type a MAC you are already looking at.

`MainWindow` turns the gesture into `MainViewModel.PlanFromLog`, which is the only thing the
code-behind does with it: what a row with no MAC means, and what to say afterwards, are the view
model's to decide. Adding a row transmits nothing. Serve mode is still two deliberate actions away.

**The address cell is left empty, and the tool never guesses at one.** An address comes off a
drawing - drives at .51, remote I/O at .21, the HMI where the HMI always goes - and none of that is
knowable from a MAC. A "next free address" suggestion offers .1, which on a real panel is the
gateway: wrong nearly every time, and wrong in the way that is easiest to accept by mistake. The
row is planned, amber, and says it will not be served until it has an address. Four characters of
typing is the cheapest part of the job.

A MAC already in the plan selects the row that has it and changes nothing on that row.

## The scan results tab

The bottom pane is two tabs, and the split is the point. **Live requests** are devices *asking* for
an address. **Scan results** are devices that already *have* one and answered when asked who they
are. A device in the second list does not need BOOTP at all, so mixing the two would be mixing two
different facts about two different kinds of device.

`ScanCommand` needs an adapter that can source a probe - the same `NicInfo.CanServe` test serve mode
uses, because a scan transmits too. One broadcast on UDP/44818 goes out, replies are collected for
two seconds, and every scan appends an `EventCategory.Scan` row whether or not it found anything:
it put packets on what might be a plant network, and that belongs in the record.

**Add to plan works here too, and it fills the address in.** That is not the tool guessing - it is
the address the device itself reported, read off the wire, which is a different thing from the
"lowest free address" suggestion the log-row path refuses to make. It also makes the common job one
gesture: a device that came up on somebody else's DHCP server is scanned, added, and Set static
writes the address it already has into its flash.

The mask is filled only when the device answered from inside the selected adapter's own subnet,
because that is the one case where the adapter's mask is evidence rather than a guess. A device
reached through a router may have any prefix at all, and an invented mask is wrong in the way that
is hardest to see - the device comes up, mostly works, and cannot reach half the plant.

A row that cannot be planned says why, in Core's words (`DiscoveredDevice.PlanningObstacle`): its
MAC is not in the ARP cache, or the entry that exists was learned on a different adapter, or the
cache could not be read at all. The plan is keyed on MAC, and a row keyed on the wrong one is a
device that is never served for a reason nobody can see.

## Serve mode

Two deliberate actions, never one. The adapter has to be selected *and* able to source a reply
(`NicInfo.CanServe` - up, addressed, not APIPA), and then serving has to be armed. Watch mode
listens on every adapter on purpose, because a request arriving on the port nobody selected is the
answer to "why is nothing happening"; Serve mode is pinned to the one adapter that was chosen.

Replies default to `ReplySendMode.PerSocketBind` here, which is *not* Core's default. Phase 0 only
proved `IP_UNICAST_IF` against a listener on the same machine, so the app picks the mechanism
whose routing decision is unambiguous until hardware says otherwise. The Listener menu has a
toggle so the two can be compared on a bench with a real panel - which is the only place that
question can be settled.

## Set static

The button beside the plan, and the reason the tool is worth carrying to a panel. An address served
over BOOTP lasts until the next power cycle, because the device is still a BOOTP client; this writes
it in, turns the protocol off, and reads it back off the device before saying it worked.

`DeviceGridViewModel.SetStaticCommand` builds a `StaticIpRequest` from the selected row and hands it
to `NetControl.Core.Commissioning.StaticIpCommissioner`. Three rules it inherits from Core, all
visible in the UI:

- **One device, the one selected.** The request carries a single address and there is no path that
  broadcasts.
- **`Verified` comes only from a readback.** It is the one thing in this app allowed to set
  `DeviceState.Verified`, because a CIP success means the request was accepted, not that the
  configuration persisted.
- **"Allow reset if needed" is a separate tick and defaults to off.** A device that will not apply
  a configuration without a reset is reported unverified rather than quietly restarted.

Progress arrives on a background thread and is marshalled by `MainViewModel` into the live log, the
same way the DHCP server's events are. Every step is also appended to the project's `Event` table
under `EventCategory.Cip` as it happens, not summarised at the end - if the connection drops part
way through, what was already sent is all anyone has to go on.

## Not here yet

- **CSV import and export**, with the whole-file validation pass in front of it. That is where a
  plan gets enough structure - a scheme, ranges, a panel it belongs to - to be worth validating
  addresses against.
- **The plan checked against a scan.** The scan tab finds what is out there and reports two devices
  answering on one address, but nothing yet compares the addresses you *planned* against the
  addresses that are *live*. That is the check worth having, and it is a small step from here.
- **A unicast sweep from the UI.** `IdentityScanner` takes an explicit, paced list of addresses -
  which is how a device carrying `IgnoresBroadcastDiscovery` gets found - but there is no control
  for it yet, so the button sends one broadcast.
