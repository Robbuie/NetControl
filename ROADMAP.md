# Implementation Roadmap

Detail behind the phases in [PLAN.md](PLAN.md). Conventions and protocol gotchas live in
[CLAUDE.md](CLAUDE.md).

Estimates assume part-time work around a real job. The sequencing matters more than the numbers -
each phase produces something usable on its own, so the work can stop at any boundary and still
leave you better off than the Rockwell tool.

---

## Phase 0 - De-risk  *(complete - 2026-08-06)*

Two console spikes. Both compiled and ran against `NetControl.DeviceSim`. Answers are written up
in `FINDINGS.md`; nothing in this roadmap was invalidated. See `spikes/README.md`.

**Exit criteria - all four must be answered in writing:**

1. Does binding UDP/67 work without administrator rights?
2. Does `IPPacketInformation.Interface` correctly identify the physical adapter?
3. Which send mode (`IP_UNICAST_IF` vs. per-socket bind) actually reaches a device?
4. Does the CIP attr 3 -> attr 5 -> readback sequence stick on your hardware, and which devices
   need a reset or power cycle?

Answers 3 and 4 change Phase 1 and Phase 2 designs respectively. Do not start Phase 1 until 1-3
are known.

All four came back the way the plan assumed. Two are only proved against a simulator on one
machine, and both need re-confirming on hardware before the fallbacks they justify get deleted:

- **Q3** - `IP_UNICAST_IF` steered the broadcast to a listener on the *same machine*. Whether a
  remote device on the wire receives it is still unproven, so `ReplySendMode.PerSocketBind` stays.
- **Q4** - the CIP write sequence stuck against the simulator, which can only reproduce quirks
  already known. It cannot discover a new one, and it does not power-cycle.

---

## Phase 1 - MVP replacement  *(in progress)*

Status: 1.1, 1.2 and 1.3 are done - written, compiled, unit-tested, and exercised end to end by
wire tests that run the real server on a real socket. 1.4 is done and the real registry is packed.
1.5 is part done: the shell, the interface bar and the live request log are written and unit
tested; the device grid and CSV import/export are not.

**Definition of done:** you commission a real panel with this instead of the Rockwell tool, and
you do not reach for the Rockwell tool once.

### 1.1 `NetControl.Core.Interfaces`  [done]

| Type | Responsibility |
|---|---|
| `NicInfo` | Immutable snapshot: index, name, description, IPv4/mask, link state, speed, APIPA flag |
| `NicMonitor` | Live inventory. Subscribes to `NetworkChange.NetworkAddressChanged` / `NetworkAvailabilityChanged`, raises `NicsChanged`. Short-TTL cache underneath |
| `PortConflictDetector` | `GetExtendedUdpTable` P/Invoke -> who owns UDP/67, by PID and process name |
| `FirewallCheck` | Detects whether an inbound UDP/67 rule exists for this executable; offers to create it |

The firewall check is worth building properly. "Nothing is arriving" has exactly four common
causes - wrong NIC, port conflict, firewall, device not requesting - and the tool should be able
to rule out three of them without the user guessing.

### 1.2 `NetControl.Core.Dhcp`  [done]

Promote the spike codec, then add what a spike skips:

- `BootpPacket` - codec, round-trip tested against captured real-world frames
- `DhcpServer` - owns the socket, raises `RequestReceived` / `ReplySent` / `ServerFault`
- `IAssignmentPolicy` - decides what to answer with. Phase 1 has one implementation,
  `StaticMapPolicy` (answer only known MACs). Keeping this an interface is what makes a pool-based
  policy addable later without touching the server.
- Duplicate/retransmit suppression keyed on `(xid, chaddr)` with a short window - devices
  retransmit aggressively and the log becomes unreadable without it
- Structured `DhcpEvent` records for the log, not formatted strings

**Explicitly out of scope for Phase 1:** address pools, lease expiry, conflict detection via ARP
probe. Static mappings only. Commissioning is not a general-purpose DHCP server workload and
pretending otherwise adds risk for no benefit.

### 1.3 `NetControl.Core.Persistence`  [done]

SQLite, one file per project.

```sql
CREATE TABLE Project (
    Id            INTEGER PRIMARY KEY,
    Name          TEXT NOT NULL,
    SchemaVersion INTEGER NOT NULL,
    CreatedUtc    TEXT NOT NULL
);

CREATE TABLE Device (
    Id          INTEGER PRIMARY KEY,
    Mac         TEXT NOT NULL UNIQUE,     -- AA:BB:CC:DD:EE:FF
    PlannedIp   TEXT,
    PlannedMask TEXT,
    PlannedGw   TEXT,
    HostName    TEXT,
    PanelRef    TEXT,                     -- panel / tag / drawing reference
    Role        TEXT,                     -- free text: "PowerFlex 525 conveyor 3"
    Vendor      TEXT,                     -- resolved from OUI at import time
    QuirkFlags  INTEGER NOT NULL DEFAULT 0,
    Notes       TEXT
);

CREATE TABLE Assignment (
    Id         INTEGER PRIMARY KEY,
    DeviceId   INTEGER NOT NULL REFERENCES Device(Id),
    ServedIp   TEXT NOT NULL,
    NicIndex   INTEGER NOT NULL,
    NicName    TEXT NOT NULL,
    ServedUtc  TEXT NOT NULL
);

-- Append-only. Never UPDATE or DELETE from this table.
CREATE TABLE Event (
    Id        INTEGER PRIMARY KEY,
    Utc       TEXT NOT NULL,
    Severity  TEXT NOT NULL,              -- info | warn | error
    Category  TEXT NOT NULL,              -- dhcp | cip | scan | app
    DeviceId  INTEGER REFERENCES Device(Id),
    Target    TEXT,                       -- MAC or IP the event concerns
    Message   TEXT NOT NULL,
    Detail    TEXT                        -- JSON: raw bytes, status codes, etc.
);

CREATE INDEX IX_Event_Utc ON Event(Utc);
CREATE INDEX IX_Event_Device ON Event(DeviceId);
```

`SchemaVersion` from day one. Migrating a project file you created six months ago on a customer
site is not a hypothetical.

**As built.** The tables are as above. Four things were added that the schema alone did not say:

- `PRAGMA user_version` is the authoritative migration marker; `Project.SchemaVersion` mirrors it
  so the version is visible to anyone who opens the file in a SQL browser. `SchemaMigrations` is an
  ordered list of steps, appended to and never edited, and a file from a *newer* build is refused
  outright (`SchemaVersionException`) rather than downgraded.
- The append-only rule on `Event` is enforced by `BEFORE UPDATE` / `BEFORE DELETE` triggers, not by
  convention. Nothing in the API can trip them; they exist to catch a future mistake, including one
  made from a SQL console.
- `ProjectStore.CreateInMemory` gives the app a working event log before anyone has saved a project.
  The first ten minutes are when the "why is nothing arriving" events happen.
- `DhcpEventRecorder` subscribes to `DhcpServer` and writes the `Event` and `Assignment` rows. It
  runs on the receive loop, so a failed INSERT is counted and reported through `RecordingFailed` -
  it can never stop the tool answering a device.

Journal mode is left at SQLite's default rather than WAL: a project file gets copied to a USB stick,
and a copy that silently lost its last commits with a left-behind `-wal` sidecar would be a bad
failure. There is one connection behind one lock, so WAL would buy concurrency nobody needs.

### 1.4 OUI database  [done]

Bundle the IEEE OUI registry as a compressed embedded resource with a converter to a compact
binary format. Refreshing it is a chore you run, not a code change and not a build step.

The four registry files are 5.6 MB of CSV. Packed, the whole thing is 58,134 assignments in
604 KB, binary searched in place with no per-lookup allocation.

Two things came out differently from the line above, both deliberately:

- **All four registries, not just MA-L.** IEEE also sells 28-bit (MA-M) and 36-bit (MA-S, plus the
  retired IAB) blocks, carved out of 24-bit ranges that `oui.csv` lists as belonging to *IEEE
  Registration Authority*. A 3-byte-prefix table would therefore name every small vendor "IEEE
  Registration Authority" rather than leaving them unknown - confidently wrong, on exactly the
  unfamiliar gateway a commissioning engineer most needs identified. `OuiDatabase` keeps three
  sorted tables and searches 36, then 28, then 24.
- **The converter is a C# console project, not a build-time step.** `tools/NetControl.OuiPacker`
  is in the solution so it can be read and stepped through, but nothing in `src/` references it
  and it never runs during a build: a clean build that depends on reaching IEEE is a build that
  fails on a locked-down laptop. The writer itself lives in `NetControl.Core` beside the reader,
  so the packed format is described in one place and the tests can round-trip it in memory.

### 1.5 `NetControl.App` - the UI  [regions 1 and 2 done; region 3 to do]

Three regions, in priority order:

1. **Interface bar (always visible).** Selected adapter, its IP, link state, UDP/67 status,
   firewall status. Green when everything needed for a request to arrive is true. This bar is the
   single biggest usability win over the existing tool - it answers "why is nothing happening"
   before the user has to ask.  **[done]**
2. **Live request log.** Every BOOTP/DHCP request as it lands, with arrival NIC, MAC, vendor from
   OUI, and whether it matched a planned device. Unknown MACs are visually distinct.  **[done]**
3. **Device grid.** The plan: MAC, planned IP, state, panel ref, notes. Drag a MAC from the log
   into the grid to plan it. Inline edit. CSV import/export.  **[todo]**

MVVM throughout; the view models observe `NetControl.Core` events and never touch a socket.

**As built, for the two regions that exist.** Five things were decided that the description above
did not settle:

- **Grey is not green.** `ReadinessState` is ordered so `Unknown` sorts *worse* than `Ready`,
  which lets the overall grade be a plain maximum and still never report a check it could not run
  as good news. A firewall that could not be read is not a firewall that is open, and this is the
  only rule in the bar that is worth a unit test on its own.
- **The bar grades, it does not summarise.** A wildcard bind by VMware and the Hyper-V Default
  Switch holding its own address are both "port in use". `PortConflictSeverity` is carried
  straight through to the colour, because showing them identically is what trains people to
  ignore the warning.
- **The adapter selection outlives the adapter.** `AdapterOption` is keyed on interface index, so
  unplugging a USB adapter greys the row rather than silently clearing the selection, and
  replugging it makes the tool work again with no click and no restart.
- **Watch listens everywhere; Serve is pinned to one adapter.** A request landing on the port
  nobody selected is the answer to "why is nothing happening", so Watch mode sets no interface
  filter and the log marks the mismatch. Serve mode sets the filter, because the tool must never
  answer a device on a port nobody chose. Arming Serve needs a second, deliberate action on top of
  selecting an adapter that can actually source a reply.
- **The app overrides Core's default send mode.** `ReplySendMode.PerSocketBind` here, against
  Core's `UnicastInterfaceOption`, because Phase 0 only proved `IP_UNICAST_IF` against a listener
  on the same machine. There is a menu toggle so the two can be compared on a bench, which is
  where Q3 finally gets answered.

Testability cost one thing worth recording: `NetControl.Tests` had to move to `net10.0-windows`
with `UseWPF`, because a `net10.0` project cannot reference the WPF app. No test opens a window -
the view models carry no WPF types and marshal through `IUiDispatcher` - but the test project is
now Windows-only, which the rest of the repo already was in practice.

**Acceptance tests for Phase 1**

None of these has been run on hardware yet. The first three are covered by view model tests
against the real event types, which is as close as a desk gets; the fourth needs the device grid.

- Start with the wrong NIC selected -> the interface bar says so before any packet arrives.
  *Half-covered.* The bar cannot know an adapter is the wrong one before anything arrives, so it
  does the two things it honestly can: it grades the selected adapter's link and address up front,
  and the moment a request lands anywhere else it names that adapter in its own line. Nothing is
  ever auto-selected, so "the wrong NIC" can only be a choice somebody made.
- Start with VMware's DHCP running -> the tool names `vmnetdhcp.exe` and refuses to pretend it is
  fine. *Covered by a view model test with a synthetic report;* the refusal itself is Core's
  `RefuseOnSeriousPortConflict`, which the app leaves on.
- Unplug and replug the cable mid-session -> the interface bar tracks it without a restart.
  *Covered*, including the harder case where Windows removes the adapter entirely rather than
  just dropping the link.
- Serve 12 devices from a CSV -> all 12 land, and the event log reconstructs exactly what
  happened. *Blocked on the device grid and the CSV importer.*

---

## Phase 2 - Static IP and verification  *(3-4 weeks)*

### 2.1 `NetControl.Core.Cip`

Promote the spike client, then harden:

- `EnipSession` with proper timeout, cancellation, and a session keepalive
- Connection reuse - opening a TCP session per attribute read is wasteful when configuring a rack
- `TcpIpInterfaceObject` / `EthernetLinkObject` / `IdentityObject` typed wrappers over raw attribute access
- Full CIP general-status table with *actionable* text, not just the spec wording

### 2.2 Commissioning state machine

One instance per device. This is the core abstraction of the whole product.

```
Planned --BOOTP request seen---> Discovered
Discovered --reply sent-------> Served
Served --ICMP/ListIdentity OK--> Online
Online --CIP attr3+attr5 OK----> Configured
Configured --readback matches--> Verified OK
                    |
                    \-- any step fails ---> Failed(reason, remediation)
```

Rules:

- Every transition writes an `Event` row.
- `Failed` always carries a suggested remediation, never a bare error code.
- Retries are bounded and explicit; the UI shows attempt counts rather than spinning silently.
- A device may legitimately need a power cycle between `Configured` and `Verified` - the state
  machine waits and prompts rather than declaring failure.

### 2.3 `NetControl.DeviceSim`

A fake EtherNet/IP device: answers `ListIdentity`, serves the Identity / TCP-IP / Ethernet Link
objects, optionally emits BOOTP requests on startup. Configurable quirks:

- rejects attr 5 while in BOOTP mode
- requires a reset before config persists
- reports `ConfigurationSettable` clear (hardware-pinned address)
- responds slowly, or drops the connection mid-write

Every quirk found on real hardware gets added here with a regression test. This is how the tool
stays reliable as device coverage grows - otherwise every fix risks breaking a device you no
longer have on the bench.

### 2.4 Quirk table

`QuirkFlags` on `Device`, populated from observed behaviour and seeded with known
vendor/product-code combinations. Drives the commissioner's sequencing decisions.

---

## Phase 3 - Bulk and CLI  *(2-3 weeks)*

- CSV plan import with validation *before* anything is sent: duplicate MACs, duplicate IPs,
  addresses outside the NIC's subnet, malformed masks. Fail the whole plan up front rather than
  half-configuring a panel.
- `BulkCommissioner` - runs the Phase 2 state machine across N devices with bounded concurrency.
  Default concurrency stays low; industrial stacks are thin.
- Progress UI: per-device state, elapsed, attempt count.
- Commissioning report - the `Event` log rendered to PDF or HTML, suitable for handing to a
  customer as a startup record.
- `NetControl.Cli`: `netcontrol commission plan.csv --nic "I219" --report out.html`, non-zero exit on any device
  not reaching `Verified`.

The CLI is what makes repeat builds of the same machine trivial, and it is how the whole pipeline
gets tested in CI against `NetControl.DeviceSim`.

---

## Phase 4 - Scanning and discovery  *(4-6 weeks)*

- `ListIdentity` enumeration as a first-class view, not just a spike command
- ICMP sweep with adaptive rate limiting
- ARP table read (`GetIpNetTable`) - free, instant, and reveals devices that ignore ping
- TCP probe of the ports that matter here: 44818 (EtherNet/IP), 502 (Modbus), 102 (S7),
  80/443, 22, 23
- Passive listen mode - watch broadcast traffic and build an inventory without sending anything.
  Safest possible option on a running line, and worth defaulting to.
- LLDP/CDP capture for topology, where switches emit it
- Inventory diff: "what changed on this network since last week"

The diff is the feature worth building the rest of Phase 4 for. Everything else is table stakes;
knowing that a device appeared or an IP moved is what saves a call-out.

---

## Phase 5 - Broader toolkit  *(open-ended)*

Roughly in order of expected value:

1. Subnet calculator and IP plan validator
2. Interface statistics and error counters - CRC errors on a link explain a lot of intermittent faults
3. Modbus TCP discovery and register read
4. PROFINET DCP discovery and Set Name/IP (**needs npcap + admin** - raw Ethernet frames, not IP)
5. Packet capture with EtherNet/IP-aware decoding, optional npcap install
6. Wireless survey
7. Cable/link diagnostics via CIP Ethernet Link diagnostic counters

PROFINET is the one that changes deployment: it requires a driver install and elevation. Keep it
behind an optional component so the core tool stays a copy-and-run executable.

---

## Cross-cutting

**Testing.** Codec round-trip tests on captured frames are the highest-value tests here - protocol
bugs are the ones that cost hours on site. Integration tests run the CLI against `NetControl.DeviceSim`
in CI. UI logic lives in view models and is testable without a window.

**Error messages are a feature.** Every user-visible failure names the likely cause and the next
action. This is most of the difference between this tool and the one it replaces.

**Packaging.** Single-file self-contained `dotnet publish`. No installer, no admin, no driver
(through Phase 3). Copy the exe to a USB stick and it runs on a plant laptop.

**Logging.** Serilog to a rolling file alongside the SQLite event log. The event log is the
commissioning record; the Serilog file is for diagnosing the tool itself.

---

## Risk register

| Risk | Impact | Mitigation |
|---|---|---|
| Broadcast reply cannot be pinned to one NIC on Windows | High - core feature | Phase 0 spike tests both mechanisms before any design commits |
| Device quirks are broader than expected | Medium - schedule | `DeviceSim` + quirk table make each new quirk additive, not structural |
| Plant IT blocks an unsigned executable | Medium - deployment | No installer or driver needed; code signing is cheap to add later if it becomes an issue |
| Scope drift into a general network scanner before Phase 1 ships | High - nothing ships | Phases 1-3 are the product; Phase 4+ starts only after a real panel is commissioned with it |
| Two half-finished tools instead of one working one | High | Keep using the Rockwell tool until Phase 1 passes its acceptance tests. Do not migrate early |

The scope-drift risk is the real one. Network scanning is more fun to build than a reliable
BOOTP server, and the BOOTP server is what actually solves the problem in front of you.

---

## Suggested milestones

| Milestone | Contents | Rough timing |
|---|---|---|
| **M0** | Both spikes compiled and run against real hardware; four questions answered | Week 1-2 |
| **M1** | Phase 1 - usable BOOTP/DHCP replacement, used on a real job | Week 6-8 |
| **M2** | Phase 2 - static IP with verified readback | Week 10-12 |
| **M3** | Phase 3 - bulk commissioning + CLI + report | Week 13-15 |
| **M4** | Phase 4 - scanning and inventory diff | Week 20+ |

**Ship M1 before starting M2.** A tool that reliably does one thing is worth more than a
half-finished tool that does four.
