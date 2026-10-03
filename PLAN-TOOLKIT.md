# Part F - the toolkit: diagnostics, bulk work, and the report

A working document in the shape of [PLAN-NEXT.md](PLAN-NEXT.md) and [PLAN-TFTP.md](PLAN-TFTP.md).
Fold the settled parts back into ROADMAP.md and CLAUDE.md when they are done.

**Status: written in a session with no .NET SDK, and not yet compiled.** Same method as before -
the decision tables were ported to Python and run before they were trusted - but the first
`dotnet build` is the real check. See *What to look at first* at the bottom.

## Why now, and the order

Phase 1 is functionally complete and has never been in front of a device. The roadmap's rule is
that Phase 4 waits for a commissioned panel, and that rule is still right about *scope drift*. What
changed is the request: the tool is going to be judged on site against the Rockwell tool **and**
against the half-dozen other utilities people carry for the moment the Rockwell tool cannot answer
"why". Everything here is either a read, or the existing write run more than once.

The order, as asked: functionality and diagnostics first, the commissioning report last.

| | | Transmits | Writes to a device |
|---|---|---|---|
| F1 | Ping - one device, or every planned address | ICMP echo, unicast | No |
| F2 | Device diagnostics over CIP - link, duplex, error counters, faults, ACD | CIP Get_Attribute_Single, one device | **No** |
| F3 | Service check - which TCP services a device answers on | TCP connect, one at a time | No |
| F4 | Set static across the plan | The existing commissioning sequence, once per device | Yes - the existing write |
| F5 | Scan history and what changed since the last scan | Nothing new | No |
| F6 | Subnet calculator, and this PC's own adapter error counters | Nothing | No |
| F7 | The commissioning report | Nothing | No |

## Safety - the rules these have to keep

The hard rules in CLAUDE.md all still apply. What they mean for each piece:

- **Every probe is unicast to an address somebody named or planned.** Ping, the CIP read and the
  service check each refuse 255.255.255.255, 0.0.0.0, multicast, and a directed broadcast when the
  mask is known. `UnicastTarget` is the one place that rule lives for all three.
- **The CIP diagnostics read is read-only, and it is read-only by construction.** It sends
  Get_Attribute_Single and nothing else. In particular it never sends Get_and_Clear (0x4C) on the
  Ethernet Link counters: clearing them is a write, and it destroys the history the next person
  needs. "Since the counters were last cleared" is reported honestly instead, and a second read
  gives the rate.
- **Gentle by default.** Ping is at most four in flight with a pause between batches; the service
  check is one connection at a time with a pause between ports. Thin industrial TCP stacks are the
  reason, and the numbers are options rather than constants so a bench can try others.
- **Every transmission writes one event row**, same as the scan (the B4 rule): what was asked, of
  whom, and what came back. A read is not a state change, but it is packets on somebody's plant
  network, and the record is where "who was poking at the drives on Tuesday" gets answered.
- **Bulk Set static is the existing sequence, sequential, behind a confirmation that lists every
  device by name and address.** Each device is still written unicast at its own planned address,
  and a stop request takes effect *between* devices, never half way through one - a device left
  with attribute 3 written and attribute 5 not is the worst place to stop.

---

## F1 - Ping

`NetControl.Core/Reachability/`:

- `IPinger` + `IcmpPinger` over `System.Net.NetworkInformation.Ping`. No admin needed and no new
  dependency. Behind an interface so the sweep and the view model are testable without ICMP.
- `PingOutcome` - replied (with round trip and TTL), timed out, unreachable, or failed. **Timed out
  and unreachable are different sentences**: "no route" is the laptop's problem, "no answer" may be
  the device's.
- `PingSweep` - a list of targets, checked through `UnicastTarget`, a bounded number in flight,
  `Progress` per address, and a `PingSweepResult` whose `Summary` is the event row's message.

In the app: **Ping plan** on the grid toolbar pings every planned address and fills a new
**Reach** column; the Diagnostics tab pings one address. Neither changes `DeviceState` - an echo
reply proves something answers at that address, not that it is the device the plan meant, which is
the question the scan and the CIP read answer.

## F2 - Device diagnostics over CIP

`NetControl.Core/Cip/EthernetLink.cs` beside `TcpIpInterface.cs`: typed reads of class 0xF6 -
speed (attribute 1, **in Mb/s**, not b/s), interface flags (2), MAC (3), interface counters (4),
media counters (5) - per instance, because a device with an embedded switch has a link object per
port. Instances are read upward from 1 until the device says there is no such instance.

`NetControl.Core/DeviceHealth/`:

- `DeviceHealthReader` - one session to one device, then: Identity (vendor, product, revision,
  serial, **status word**), TCP/IP Interface (status, method, configuration, host name), and every
  Ethernet Link instance. Each read stands alone: a device that refuses attribute 5 still gets its
  link speed reported.
- `DeviceHealthReport` - what came back, plus `Findings`.
- `LinkAssessment` - **a pure function** from readings to findings, so the rules are tested without
  a socket. The rules, each worded as a cause and an action:

| Seen | Finding |
|---|---|
| Local hardware fault flag | Error - the port reports its own hardware fault |
| Link up, half duplex | Warn - on a switched network almost always a duplex mismatch |
| Late collisions, or any collision while full duplex | Warn - the duplex mismatch signature |
| Auto-negotiation failed (status 1 or 2) | Warn - running on a default; check the switch port |
| Forced speed/duplex (status 4) | Info - the switch port must be forced to match |
| FCS or alignment errors | Warn - cabling, connectors, noise, or duplex |
| Frame too long | Warn - usually VLAN tags or jumbo frames reaching a device that cannot take them |
| In discards | Warn - the device is dropping frames it received; often multicast flooding without IGMP snooping |
| 10 Mb/s | Warn - a damaged pair or a far end that cannot do better |
| Manual setting requires reset | Info - a change is waiting for a reset |
| TCP/IP status: configuration pending | Warn - a written configuration has not taken effect yet |
| TCP/IP status: address conflict detected (ACD) | Error - another device is on this address |
| Identity: major or minor fault bits | Warn / Error, with the extended status in words |

With **two** reads of the same device, counters are compared and the findings say "N in the last
M seconds" - which is the difference between "this happened once since power-up" and "this is
happening now". The first read is never graded as worse than Warn on counters alone.

The simulator learns the Ethernet Link counters, a second port, the Identity status word and the
TCP/IP status bits, plus a `Quirk.DuplexMismatch` that reports half duplex with late collisions.
Its attribute 1 said 100,000,000; the spec unit is Mb/s, so that is corrected to 100.

## F3 - Service check

`NetControl.Core/Reachability/ServiceProbe.cs`. One address, a short list of ports that mean
something on a plant floor - FTP, SSH, Telnet, HTTP, ISO-TSAP (S7), HTTPS, Modbus/TCP, OPC UA,
GE SRTP, DNP3, EtherNet/IP - connected **one at a time**, closed immediately, nothing sent.
Three answers, kept apart: *open*, *refused* (something is there and said no), *no answer*
(filtered, or nothing there). A refused port is evidence the host is up; a page of "no answer" is
not evidence of anything, and the summary says so.

The Diagnostics tab gets an **Open web page** button when 80 or 443 answered, because the device's
own diagnostics page is very often the next place anybody goes.

## F4 - Set static across the plan

`NetControl.Core/Commissioning/BulkCommissioner.cs` - the existing `StaticIpCommissioner`, run over a
list of requests, one at a time, raising `DeviceStarting` / `DeviceFinished`, and stopping only
between devices when `RequestStop` has been called. The CLI the roadmap describes will use the same
class.

In the grid: **Set static on all ready**, which takes every row that is servable, saved, and not
already Verified. The confirmation names each one. Each row goes through exactly the path the single
Set static uses - the same request, the same `Apply`, the same event rows attributed to the right
device - so there is still only one way a row becomes Verified.

## F5 - Scan history

Schema version 2 adds two tables, both append-only by trigger like `Event`:

```sql
CREATE TABLE ScanRun (Id, Utc, NicName, NicAddress, NicMask, Answered);
CREATE TABLE ScanSighting (Id, ScanRunId, Address, Mac, VendorId, DeviceType, ProductCode,
                           Revision, Serial, ProductName);
```

`NetControl.Core/Discovery/InventoryDiff` compares a scan with the previous one **of the same
subnet** - a scan of a different segment is not a "before". Identity is vendor + product code +
serial, the same key the scan dedupes on, so a device that moved address is one device that moved
rather than one that vanished and one that appeared. Findings:

- **Moved** - same device, different address. Warn: something re-addressed it.
- **Replaced** - same address, same product, different serial. Info: a module was swapped.
- **Different device at an address** - same address, different product. Warn.
- **Firmware changed** - same device, different revision. Info, but worth knowing on a line that
  was validated against a revision.
- **New** - not seen before. Info.
- **Not answering** - seen last time, silent now. Info, and worded as silence rather than absence,
  for the reason `PlanConformance` already gives: it may be powered down.

A file from this build opened in an older one is refused by the existing `SchemaVersionException`
path, with its existing "update the tool" remediation. That is the cost of the table, and it is the
reason the change waited until there was a feature worth a schema step.

## F6 - Subnet calculator and this PC's adapter

- `NetControl.Core/SubnetCalculation` - from "192.168.1.51/24", "192.168.1.51 255.255.255.0" or a
  bare address plus a mask: network, broadcast, first and last host, host count, mask, wildcard,
  and whether a second address is on the same subnet. `Ipv4Subnet` gains the members this calls and
  nothing else.
- `NicStatistics` - the selected adapter's own received-with-errors, discarded and sent-with-errors
  counters, read twice to give a rate. A CRC storm on the laptop's own cable looks exactly like a
  device problem from everywhere else in the tool.

## F7 - The commissioning report

Last, as asked. A single self-contained HTML file built from the project file alone - the plan
with each device's state and the evidence for it, what was served and when, every write and its
readback, the diagnostics and scan findings, and the event log in full - which prints to PDF from
any browser. It reads the append-only record and nothing else, so it can be regenerated from a
project file on a different machine months later and say the same thing.

## What to look at first

- Build. Then the four new tests files; then open the window.
- The Reach column, the Diagnostics tab and the bulk confirmation have never been on screen.
- **`EthernetLink` has never read a real device.** Every test runs against `NetControl.DeviceSim`,
  which was extended from the same reading of the spec - so a shared misreading of the counter
  layout passes both sides. The byte-level tests assert literal offsets for that reason, but the
  first read of a real adapter is the check.

---

# Part G - the second batch (0.9.0)

Asked for after 0.8.0: items 1 and 3-7 from the gap list, every one with a screen, no command line,
nothing that costs money (so no code signing). Built as one batch so it is one CI round.

| | What | Where |
|---|---|---|
| G1 | **Protocol-verified service check.** One read-only handshake per known port - EtherNet/IP ListIdentity, Modbus 43/14, S7 COTP CR, MELSEC 3E Read CPU model, OPC UA Hello, HTTP HEAD, FTP/SSH/Telnet banners. "verified" or "open, unproven". | `Core/Reachability/*Handshake.cs`, Diagnostics tab |
| G2 | **Device quirk table.** Learned from Set static outcomes (evidence only, never a timeout), editable per row, each flag saying what the tool does about it. | `DeviceQuirkCatalog`, `QuirkLearning`, Quirks column, `QuirksWindow` |
| G3 | **DHCP boot options.** Next server, TFTP server, boot file, domain, DNS - per project, schema v3. | `Core/Dhcp/BootOptions.cs`, `Persistence/ProjectSettings.cs`, `BootOptionsWindow` |
| G4 | **Passive inventory.** Listen-only; ARP, BOOTP/DHCP, LLDP, PROFINET, EtherNet/IP. Optional Npcap. | `Core/Capture`, `Core/Passive`, Passive tab |
| G5 | **Modbus register read** (reads only) and **PROFINET DCP** identify / set name / set IP / flash, each Set confirmed and read back. DCP needs Npcap. | `Core/Modbus`, `Core/Profinet`, Modbus and PROFINET tabs |
| G6 | **File Manager look.** Own title bar with the menu in it, rounded cards, theme following Windows light/dark. | `Appearance/Controls.xaml` (ChromeWindow, Card), `SystemTheme`, `Theme.Effective` |

Decisions worth keeping:

- **Npcap is loaded by path, lazily, through `NativeLibrary`** - not a `DllImport` and not a package.
  A machine without it never looks for it; the two tabs that need it say what to install.
- **A protocol exception or refusal still proves the protocol.** Modbus "illegal function", an S7
  disconnect request and an OPC UA ERR are all answers only the real stack gives.
- **Slowness is never learned.** A timeout is what a wrong VLAN looks like too. Only flags the device
  itself demonstrated are learned; the rest are ticked by a person, and every change is a record row.
- **A DCP Set is sent once.** No retry on a write; the readback decides.
- **Boot options go in the header always, in DHCP options only when asked for** (RFC 2131), so a
  small adapter is never handed options it did not expect.
