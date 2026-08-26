# Working plan: the device grid, and discovery

A working document for the next chunk of work. It expands ROADMAP.md section 1.5 and pulls one
piece forward from Phase 4. Fold the settled parts back into ROADMAP.md and delete this file when
the work is done - three planning documents is already one too many.

## The goal, stated plainly

Two things have to become true:

1. **You can build a plan without typing a MAC.** Today there is no way to enter a device at all,
   so the plan is always empty and Serve mode would answer nobody. The app is a watcher.
2. **You can find out what is on the network**, both the devices asking for an address and the
   devices that already have one.

## Two kinds of discovery, and why the difference matters

This is the thing to be clear about before writing anything, because the two answer different
questions and only one of them is nearly built.

| | Passive (the log) | Active (ListIdentity) |
|---|---|---|
| Finds | Devices *asking* for an address | Devices that already *have* one |
| Sends | Nothing | A broadcast on UDP/44818 |
| Gives you | MAC, arrival NIC, vendor from OUI | IP, product name, serial, revision, vendor ID |
| Gives you a MAC? | Yes, directly | **No - see below** |
| Status | Built. Needs one double-click | Not started |

**What Rockwell's tool does is the passive one.** Its Request History is a list of MACs that sent
a BOOTP request; you double-click one to move it into the Relation List. It does not scan. The
live log already does that job and does more of it - arrival NIC, the full IEEE registry rather
than a short vendor list, retransmits collapsed, planned distinguished from stranger. The only
missing piece is the double-click.

**The active scan does not return a MAC, and the plan is keyed on MAC.** The ListIdentity reply
carries a `sockaddr_in`, not a hardware address. So turning a scan result into a plan row needs
the ARP table read straight afterwards - the reply itself has just populated our ARP cache, so
the mapping is sitting there for free. That makes the ARP read a *dependency* of the active scan
rather than an alternative to it.

**Be honest about what the active scan is for in Phase 1.** A device that answers ListIdentity
already has an address and does not need BOOTP. So its value is not populating the plan, it is:

- **Verification.** After serving, confirm the device actually came up at the planned address.
  This is the cheap version of the readback that Phase 2 does properly over CIP.
- **Conflict detection.** Something is already sitting on the address you planned.
- **Inventory.** What is on this segment at all.

That is worth having. It is just not the thing that stops you typing MACs.

---

## Part A - the plan grid (finishes 1.5)

Each step leaves the tool usable. After A4 you can commission a panel with it, which is M1.

### A1. `ProjectStore.SaveAs(path)` - **done**

The app starts on `CreateInMemory` so the event log records from the first second. There is
currently no way to turn that into a file, which makes the grid half a feature.

- ~~`VACUUM INTO 'path'` is the whole implementation.~~ **It is not.** Through
  Microsoft.Data.Sqlite, against a `Mode=Memory;Cache=Shared` connection, `VACUUM INTO` returned
  no error and wrote a file with no rows in it - and since the migrator then rebuilt the schema on
  open, the result looked like a perfectly ordinary new project. Three tests caught it; nothing
  else would have. `SqliteConnection.BackupDatabase` is used instead, and the row counts are
  checked afterwards. Plain `sqlite3` does the vacuum correctly from `:memory:`, bound path and
  all, so the fault is in the provider rather than in SQLite.
- Refuse if the target exists rather than replacing it, and let the caller confirm and delete.
- Take the store's lock but do not open a transaction.
- After the copy the app reopens the new path, so later writes land in the file. Reopening means
  the same rule as `OpenProject`: the listener must be stopped first.

Worth noticing and worth telling the user: because SQLite writes immediately, **there is no such
thing as unsaved work in this app, only an unsaved location.** No dirty flag, no "save before
exit" dialog. Say so in the UI somewhere.

*Tests:* an in-memory store with devices and events survives SaveAs and reopen; schema version and
the append-only triggers survive; an existing target file is refused rather than silently replaced.

### A2. New / Open / Save As in the app - **done**

`OpenProject` already exists and already refuses while the listener runs. Add New and Save As on
the same rule. Show the project name and path in the status bar - it is there now but says
"Untitled" forever.

### A3. `DeviceGridViewModel` and `DeviceRowViewModel` - **done**

The open question above - row, column, or panel underneath - was settled as **in the row**: a
coloured dot column, the row's own background, and the explanation on the row's tooltip. A panel
underneath would make somebody match a message to a line by eye. Two levels, not one: red means
nothing was stored, amber means it stored and will not be served.


The plan, over `ProjectStore.Devices`. WPF's `DataGrid` is in PresentationFramework, so no new
package.

- Columns: MAC, planned IP, mask, gateway, host name, panel ref, role, vendor, notes, state.
- Inline edit, writing through `Devices.Upsert` and then `MainViewModel.ReloadPlan` so the policy
  the receive loop reads is updated in the same gesture.
- Per-row validation shown in the row, reusing the problem strings `DeviceRecord.TryToAssignment`
  already produces. Do not invent a second set of messages.
- **State column** is the useful one: Planned / Seen / Served / Verified, derived from the log and
  the `Assignment` table. It is also the seam Phase 2's commissioning state machine plugs into, so
  give it its own type now rather than a string.

### A4. Plan a device from a log row - **done**

Double-click a log row, or "Add to plan" on its context menu, and the device is planned with its
MAC and with the vendor resolved at that moment through `IOuiLookup` and stored - never looked up
on read, because the registry moves and a project file has to read the same way in six months. A
MAC already in the plan selects the row that has it and touches nothing on it; `Device.Mac` is
UNIQUE, so the alternative is an exception where the user expected to be shown where the device
already is.

**The address is not filled in, and the tool does not offer one.** This was built the other way
first - lowest free address on the selected adapter's subnet, network and broadcast and the
laptop's own address all skipped - and it was wrong. An address comes off a drawing: drives at .51,
remote I/O at .21, the HMI where the HMI always goes. The lowest free address is .1, which on a
real panel is the gateway. A suggestion that is wrong nearly every time is worse than none, because
being nearly plausible is exactly what gets it accepted by mistake. Typing four characters is the
cheapest part of commissioning a panel.

The one thing that survived from that attempt is `NetControl.Core/Ipv4Subnet.cs`: a checked address
and mask, which now owns the contiguous-mask test `DeviceAssignment` had a private copy of and the
byte-wise subnet comparison behind `IsOnSameSubnetAs`. It is deliberately small - `TryCreate`,
`IsContiguousMask`, `Contains`, `PrefixLength`, `Network` - and should gain a member only when
something calls it. There is nothing in it that chooses an address.

**Checking an address against what is already out there belongs with the scan, not with the plan.**
A duplicate check that only knows the plan gives false confidence: it clears an address that a
switch, an HMI or last year's untracked device is already sitting on, because none of those were
ever typed into it. See B1-B3. The exception would be a plan with enough structure to validate
against - a scheme, ranges, a panel a device belongs to - which does not exist yet and is a
question for A5, where CSV import forces the shape of a plan to be decided anyway.

### A5. CSV import and export - **done**

- `NetControl.Core/Plan/PlanCsv`, a hand-rolled RFC 4180 reader and writer. Roughly a hundred
  lines, and it keeps the dependency list short. Quoted fields and embedded commas are not
  optional: `Notes` will contain both.
- Columns: `Mac, Ip, Mask, Gateway, HostName, PanelRef, Role, Notes`. **Vendor is deliberately not
  an import column** - it is resolved from the MAC at import and stored.
- `PlanValidation` runs over the whole file before anything is written: duplicate MAC, duplicate
  IP, malformed MAC/IP/mask, non-contiguous mask, IP equal to the network or broadcast address,
  IP equal to the adapter's own address, IP outside the adapter's subnet. Report every problem
  with its row number and import nothing. Half a panel configured is worse than none.
- ROADMAP puts validated CSV import in Phase 3. It is the same code either way, so build the
  validation in from the start rather than writing it twice.

### A6. Live editing while serving - decided

`StaticMapPolicy` is a `ConcurrentDictionary` and its doc comment already says it expects a UI
thread to mutate it while the receive loop reads. So:

- **Adding while serving is allowed and is the normal workflow** - you watch a device ask, you
  plan it, it gets served on its next retransmit. That loop is the point of the log.
- **Changing the address of a device that already has an `Assignment` row this session warns
  first.** The device will get a NAK on its next request and may need a power cycle. Name that in
  the warning rather than letting it be discovered on the panel.
- **A CSV import that replaces the whole plan requires the listener to be stopped.** Swapping
  every mapping under a running server is not an edit, it is a different job.

---

## Part B - active EtherNet/IP discovery

Pulled forward from Phase 4, and it needs a slice of 2.1. Keep it behind Part A: the tool has to
be able to commission a panel before it grows a scanner. Scope drift into a network scanner before
Phase 1 ships is the top entry in your own risk register, and this is exactly that shape - so the
mitigation is sequencing, not avoidance.

### B1. `NetControl.Core/Enip` - ListIdentity - **done**

`ListIdentityReply`, `IdentityScanner` behind `IIdentityScanner`, `ScanOptions`, `ScanReport`. Two
things came out different from the design below and both are worth keeping:

- **Dedupe is by identity, not by address.** The design said "dedupe by IP". That would collapse
  the one fault a scan is uniquely able to find - two devices answering on the same address - into
  a single tidy row. The key is the answering endpoint plus vendor, product code and serial, so a
  device that answers both the broadcast and a unicast probe still collapses, and two devices never
  do. `DiscoveryResult.ContestedAddresses` is what falls out of that.
- **The window starts when the last probe is away**, not when the scan began, or a slow sweep
  spends its listening time still sending.

Also: a scan with nothing to probe transmits nothing rather than quietly falling back to a
broadcast, a unicast sweep refuses a broadcast or multicast target, and an ICMP port-unreachable is
counted as `Refused` rather than swallowed - "something is at that address and it is not
EtherNet/IP" is exactly what a conflict check wants to say.

<details>
<summary>Original design</summary>


Less work than "Phase 2" implies. ListIdentity is the one EtherNet/IP operation that needs no TCP
session and no CIP routing: a 24-byte encapsulation header, command `0x0063`, no data, over UDP.

- Broadcast from the selected adapter, collect replies for a bounded window of two or three
  seconds, dedupe by IP.
- **Promote, do not rewrite.** `spikes/Spike2.CipStaticIp/Enip.cs` and
  `src/NetControl.DeviceSim/CipCodec.cs` already encode and decode this shape.
- The reply parse is where the endianness gotcha bites: CPF item `0x000C` carries a `sockaddr_in`
  that is **big-endian sitting inside an otherwise little-endian structure**. Convert deliberately.
- **`NetControl.DeviceSim` already answers ListIdentity**, so the whole feature is testable at a
  desk with no hardware. That is the reason this is affordable now.

</details>

### B2. Vendor ID is a different table from the OUI - **decided: show the product name**

The Identity object returns an ODVA vendor ID - a small integer, `1` being Rockwell - which has
nothing to do with the IEEE OUI table already embedded. Decide one of:

- ship a short table of the vendor IDs that actually turn up in a panel, or
- show the number and the product name, and let the product name do the work.

The second is honest and costs nothing. Prefer it until somebody is annoyed by it.

### B3. Resolve the MAC from the ARP table - **done**

`ArpTable` behind `IArpLookup`, and `DeviceDiscovery` doing the join. `GetIpNetTable` rather than
`GetIpNetTable2`: the newer row embeds a `SOCKADDR_INET` union and a `NET_LUID`, so hand-marshalling
it means hand-computing alignment, and getting that wrong reads plausible garbage rather than
failing. The older row is a flat 24 bytes, IPv4-only, and follows the same shape as the
`GetExtendedUdpTable` call already in `Interfaces/`.

**Entries are filtered by interface index, and a MAC learned on a different adapter is not used.**
An engineering laptop holds ARP entries for a dozen adapters and a VPN can easily carry one for the
same address as the port in your hand; a plan row keyed on the wrong MAC is a device that is never
served for a reason nobody can see. `MacSource` keeps "not in the cache", "on another adapter" and
"the cache could not be read" apart, and `DiscoveredDevice.PlanningObstacle` words each of them.

<details>
<summary>Original design</summary>


Straight after a scan, read `GetIpNetTable` and map each replying IP to its hardware address. Same
P/Invoke pattern as the existing `GetExtendedUdpTable` in `PortConflictDetector`, so it follows a
path already taken. Without this, a scan result cannot become a plan row at all.

Fallback for a device that does not appear in ARP: read Ethernet Link object `0xF6` attribute 3.
That needs a TCP session, so it is Phase 2 work - note it and move on.

</details>

The `0xF6` attribute 3 fallback is still not built. `EnipSession` and `CipClass.EthernetLink` now
exist, so it is cheap - but it opens a TCP connection to a device as a side effect of *looking*,
which deserves to be a deliberate action on one selected row rather than something a scan does to
everything it finds.

### B4. Safety rules for the scan - **done**

All five: the button is the only caller and there is no timer, one broadcast plus a paced opt-in
sweep, ListIdentity and nothing else, an `EventCategory.Scan` row on every scan whether or not it
found anything, and `ScanOptions.SendMode` reusing the DHCP server's setting and its open question.

The scan button needs an adapter that passes `NicInfo.CanServe`, the same test serve mode applies,
because a scan transmits too.

A scan transmits, so it obeys the same discipline as Serve mode:

- Explicit and opt-in. One button, clearly labelled, never automatic and never on a timer.
- Bounded and gentle. One broadcast, plus an optional low-rate unicast sweep for devices carrying
  the `IgnoresBroadcastDiscovery` quirk that already exists in `DeviceQuirks`.
- **Never send anything except ListIdentity.** No forward-open, no reset, no attribute write as a
  side effect of discovery.
- Every scan writes an `Event` row: it put packets on a plant network, so it belongs in the record.
- Reuse the reply send mode setting the DHCP server uses. It is the same unresolved question about
  which mechanism reaches a real device.

### B5. Passive and active in one place - **done**

Two tabs in the bottom pane, both feeding `PlanDevice`. One difference from the design: a scan
result carries the address the device reported into the plan row, where a log row leaves the cell
empty. Those are not inconsistent - the log-row rule is against *suggesting* an address nobody
stated, and here the device stated it. The mask only comes across when the device answered from
inside the selected adapter's subnet.

<details>
<summary>Original design</summary>


Both feed the same "add to plan" action, and the grid does not care which found the device. Keep
them visibly distinct in the UI, though - "this device asked for an address" and "this device
answered a scan" are different facts, and a row that conflates them is a row that misleads.

</details>

---

---

## Part C - set static, and disable BOOTP/DHCP

**This is the half of the job that BOOTP alone does not do, and it outranks Part B.** Handing a
device an address is temporary: it comes up on that address and then asks again after the next
power cycle, because it is still in BOOTP/DHCP mode. What makes the Rockwell tool worth reaching
for is the second step - writing the address into the device and turning the protocol off, so the
address is the device's own. A tool that only serves addresses is a tool somebody has to use twice.

The mechanism is CIP, not BOOTP, and it is already proved. `spikes/Spike2.CipStaticIp` ran the
whole exchange against `NetControl.DeviceSim` in Phase 0 and `FINDINGS.md` records it running
clean: capability read, attribute 3 to Static, attribute 5 to the addresses, then a fresh read.

### C1. Promote the spike into `NetControl.Core`

`spikes/Spike2.CipStaticIp/Enip.cs` and `Cip.cs` become `NetControl.Core/Enip` and `/Cip`:
encapsulation header, RegisterSession, SendRRData, Get and Set Attribute Single. Shares the
ListIdentity codec with B1, so whichever is written first should leave the other a header parser.

The endianness rule from CLAUDE.md applies at every boundary and is the thing most likely to be got
wrong: EtherNet/IP and CIP are little-endian *including IP addresses inside CIP attributes*, which
is the reverse of `IPAddress.GetAddressBytes()`. Convert deliberately; never memcpy an address.

### C2. The commissioner

Ordered, and the order is not negotiable - several adapters reject attribute 5 while still in
BOOTP/DHCP mode:

1. **Read `0xF5` attribute 2, Configuration Capability, first.** If `ConfigurationSettable` is
   clear the address is pinned by hardware switches and no write will ever work. Say that, and
   stop - it is a five-second answer to a problem that otherwise costs an afternoon.
2. **Attribute 3, Configuration Control, to Static.** This is the "disable BOOTP/DHCP" step.
3. **Attribute 5, Interface Configuration** - IP, mask, gateway.
4. **Reconnect and re-read.** A CIP success status means the request was accepted, not that the
   configuration persisted. Nothing reports success until it has been read back off the device.

`DeviceQuirks.RequiresResetToApply` already exists for the adapters that need a power cycle in
between; the readback is where that is discovered, and the quirk is what stops it being
rediscovered every time that hardware appears.

### C3. Safety

Same discipline as Serve mode, and for the same reason - this writes to live equipment:

- **Unicast to one address the user typed or selected. Never a broadcast, never "configure
  everything you found".**
- Never a CIP reset, forward-open or firmware operation as a side effect.
- Every step in the append-only event log with timestamp, target, what was sent and what came back.
  A commissioning record whose most consequential operation is missing is not a record.
- Changing the address of a device that already has an `Assignment` row warns first (A6).

### C4. Putting a device back on DHCP - **done**

The same write with attribute 3 set the other way, and it turned out Core had already been built
for it - `StaticIpRequest.Method`, the skip of attribute 5, `VerifyAddress` and the wording in
`Compare` were all there and had never been called. What was missing was any way to ask.

Settled the way this said: a separate, explicitly labelled action, and in fact two of them -
**Enable BOOTP** and **Enable DHCP** on their own menu, behind a confirmation naming the
consequence. No dropdown, and nothing that starts on the wrong value.

Two things came out of building it that were not in the design:

- **`StaticIpRequest.HandBack` is the only way to construct one**, and it refuses Static. The
  difference between the two operations was one enum member in an object initialiser, which is far
  too quiet for the one that stops a device owning its address. `ToInterfaceConfig()` now throws for
  anything but Static, so a future change that stopped skipping attribute 5 fails in a test rather
  than writing 0.0.0.0 into a live device's mask.
- **A verified hand-back moves the row backwards**, to Served / Seen / Planned. `Verified` means
  "read back holding the address the plan gave it", and a device that has just been told to ask for
  one is not that. A plan that goes on claiming it is is a plan that lies to whoever opens the file
  next. The event is graded Warn rather than Info for the same reason: six months later the question
  is "when did this device stop being static", and that line should not have to be picked out of a
  hundred routine ones.

---

## Sequencing

| | | Leaves you with |
|---|---|---|
| A1, A2 | SaveAs, New/Open | Projects that persist - **done** |
| A3 | The grid | A plan you can type - **done** |
| A4 | Plan from a log row | MACs you never type - **done** |
| C1-C3 | Static IP over CIP | **The whole job rather than half of it - M1** - done |
| B1-B5 | ListIdentity + ARP, and the tab | Verification, conflict detection, inventory - **done** |
| A5 | CSV | Bulk plans, and the last Phase 1 acceptance test - **the only part left** |

M1 moved. Commissioning a panel means the devices keep their addresses afterwards, and until Part C
exists this tool does the first half and leaves the second to the tool it replaces.

## Open questions

- ~~**Does the scan belong in Phase 1's definition of done?**~~ Moot: Part B shipped anyway.
  Phase 1 is still done when a real panel is commissioned without the Rockwell tool.
- ~~**Does a plan have a structure, or is it just a list of rows?**~~ **Settled: a flat list of
  rows, validated per row.** No scheme, no ranges. A scheme would let CSV import check a file
  against intent, but the conflict that actually bites is with equipment nobody wrote down, and
  the only honest check for that is the live segment - which is what the scan now does.
- ~~**ODVA vendor ID table**~~ Settled in B2: show the product name, ship no second vendor table.
- ~~**Grid validation UX**~~ Settled in A3: in the row.
- ~~**How does the plan get compared against a scan?**~~ **Done.** `NetControl.Core/Discovery/
  PlanConformance` joins the plan to a `DiscoveryResult` and words each finding once, in Core.
  Two things were decided while building it and are worth keeping:

  - **Silence is not evidence.** A planned device that did not answer produces no finding at all. It
    may be powered down, behind a switch the scan did not reach, or not built yet, and a list that
    reported every absence as a problem is a list people learn to skip. Findings are only ever about
    something that answered.
  - **Both facts about a row are reported, not the worse one.** "Your device is not where you meant
    it to be" and "something else is where you meant it to be" have different fixes, and on a
    half-commissioned panel they are usually both true. Collapsing them would hide whichever one
    somebody was about to act on.

## Still true, and still the highest-value thing

No device has ever asked this app for an address. A Watch-mode bench session needs none of the
above, and a pcap from it is worth more than anything in `tests/NetControl.Tests/Frames.cs`, which
holds frames I built to match what a Rockwell adapter *should* emit rather than what one does.
