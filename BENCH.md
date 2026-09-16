# Bench session

One adapter, one laptop, one afternoon. Everything in this repository has been proved against a
simulator written from the same specification as the client, which means it can show that nothing
has regressed and can never discover a new way for hardware to misbehave.

**Nothing in this project has ever touched a real device.** No device has asked this app for an
address, no reply has left an adapter, and no configuration has been written to anything with a
part number. Closing that is worth more than any feature currently on the roadmap.

Work through it in order. Each run is designed so that if it fails, the previous run has already
ruled out most of the reasons why.

---

## Kit

- A laptop with a real Ethernet port. Not a dock, not a USB adapter, if that can be avoided -
  and if it cannot, write down which it was.
- **One EtherNet/IP adapter.** A PowerFlex drive, a 1734-AENT point I/O adapter, an ENBT module -
  anything that boots into BOOTP. Note the exact model and firmware revision.
- A dumb unmanaged switch, or a single crossover-capable cable. A managed switch with IGMP snooping
  or storm control is a variable nobody needs today.
- Wireshark.
- A way to power-cycle the adapter without unplugging the network. Run 4 depends on it.

Run 6 is a different article entirely - a FANUC robot doing an image backup - and carries its own
kit list and its own safety note. Read that section before planning the day around it, because it
is the only run here that cannot be done on an isolated bench.

## Safety

> **This must not be on a plant network.** Serve mode is a DHCP server. A rogue DHCP server on a
> production network is a genuinely serious incident, and this one has never been run against real
> hardware. Isolated switch, nothing else plugged in.

Also: check nobody has left a device on the address you plan to hand out.

---

## Before you start

Do all of this before the adapter is powered, and tick them off. Half of these are the reasons a
first bench session ends with "nothing arrives" and no idea which thing was wrong.

- [ ] **Give the laptop's NIC a static address on the target subnet** - say `192.168.1.10 /
      255.255.255.0`. Not optional, and not only for BOOTP: after Set static writes `192.168.1.51`
      into the device, the readback opens a TCP connection to it, and a laptop with no route to
      that subnet will report `Unverified` when the write actually worked perfectly.
- [ ] **Stop other DHCP servers on the laptop.** The Hyper-V Default Switch's ICS service and
      VMware's `vmnetdhcp` both sit on UDP/67. The app grades these rather than blocking on them,
      but a second DHCP server answering your device first is not a fair test.
- [ ] **Firewall.** `netcontrol.exe` needs an inbound rule for UDP/67. The interface bar reports
      honestly whether one exists; if it says there is none, add one before blaming the wire.
- [ ] **Adapter switches to the software-configurable position** - usually `999`, or all zeros,
      module dependent. If they are set to a fixed address, Configuration Capability comes back with
      `ConfigurationSettable` clear and Set static will refuse to write anything. That is correct
      behaviour and worth seeing once, but not what you came to test.
- [ ] **Start Wireshark** on that adapter with capture filter `port 67 or port 68 or port 44818`,
      and leave it running for the whole session. The captures are the most valuable thing you will
      take away.

---

## Run 1 - Watch. Does a request arrive at all?

Watch mode transmits nothing. It is the safest thing this tool does and the only run where a
mistake costs nothing.

```powershell
dotnet run --project src/NetControl.App
```

Select the adapter, press **Watch**, then power the device on.

**Expect:** rows in the live request log within a few seconds - `BOOTP REQUEST`, the device's MAC,
its vendor resolved from the OUI table, and the adapter it arrived on. Retransmits collapse into an
`x4` counter rather than filling the log.

| If | Then |
|---|---|
| Nothing at all | Check the interface bar first - it exists to answer this. Grey is not green: a check that could not run is not a check that passed. |
| Rows arrive on a different adapter | The bar says so in its own amber line. This is the single most useful thing the tool can tell you. |
| Vendor says "unknown vendor" | Note the MAC. Either the OUI table needs refreshing, or the address is locally administered. |
| Wireshark sees the request, the app does not | The interesting failure. Capture it and note the firewall state. |

- [ ] A real BOOTP request appeared in the log
- [ ] The vendor resolved correctly for a MAC you can check against the label
- [ ] **Saved the pcap.** `tests/NetControl.Tests/Frames.cs` currently holds frames built to match
      what a Rockwell adapter *should* emit. Replace them with what one *does*.

---

## Run 2 - Serve. Does the device take the address?

Add the device to the plan by double-clicking its row in the log - the MAC and the vendor come
across, and you type the planned IP and mask. The row is amber until it has both, then goes green
when it is complete and servable.

- [ ] The double-click planned the device without you retyping the MAC off the log

Tick **Arm serve**, press **Serve**, and power-cycle the device.

**Expect:** a `BOOTP REPLY` row, an `Assignment` row in the project, and the device reachable at
the planned address.

- [ ] The device came up at the planned address (`ping` it, then `arp -a`)
- [ ] The reply is in the pcap, padded to at least 300 octets
- [ ] The grid row moved to `Served`

**Then the question Phase 0 could not answer.** The Listener menu has a *Reply by per-socket bind*
toggle. It defaults on, because `IP_UNICAST_IF` was only ever proved against a listener on the same
machine and it fails silently when it does not work.

- [ ] Turn the toggle **off**, power-cycle the device, and confirm it still gets its address.

That single observation settles a design question that has been open since Phase 0. If it works,
`ReplySendMode.UnicastInterfaceOption` can become the default and the per-socket fallback can go.
If it does not, write that down - it is just as valuable, and much harder to discover later.

---

## Run 3 - Set static. Does BOOTP actually turn off?

The half of the job BOOTP does not do. Select the row, leave **Allow reset if needed** unticked,
press **Set static**.

**Expect:** the live log narrating each step - capability, attribute 3, attribute 5, then the
readback - and the row moving to `Verified`.

| Outcome | What it means |
|---|---|
| `Verified` | Written and read back. Go to Run 4, which is the one that counts. |
| `NotSettable` | The address is pinned by switches. **Nothing was written.** Check the module. |
| `Refused` | The device said no and the log says which no. Record the CIP status. |
| `Mismatch` | The device returned success and is holding something else. **This is the failure the Rockwell tool does not catch.** Capture everything. |
| `Unverified` | Writes accepted, device did not come back. Nine times in ten this is the laptop having no route to the new subnet - check the first item in the setup list. |

- [ ] Recorded which outcome, and the exact CIP status if it refused
- [ ] The whole exchange is in the pcap

---

## Run 4 - Power cycle. The only test that matters.

Everything above can pass on a device that wrote the configuration into RAM and will forget it.
**No amount of simulator work substitutes for this step**, and it is the reason the bench session
exists.

Pull the power. Wait ten seconds. Restore it. Leave the app in **Watch** mode.

| What happens | What it means |
|---|---|
| Device comes up silently at its address | **Correct.** The configuration is in flash and BOOTP is genuinely off. This is the whole product working. |
| Device sends a BOOTP request again | The write did not persist, or attribute 3 did not stick. Either way the tool reported `Verified` and was wrong - which is a bug in the verification, and the most important thing you could find today. |
| Device comes up at the old address | Same, and capture it. |

- [ ] Recorded the result
- [ ] If it did not persist, opened an issue and stopped trusting `Verified` until it is fixed

---

## Run 5 - if there is time

- [ ] **A device that is not in the plan.** Power on something else while serving. It must appear
      in the log as a stranger, and it must not be answered.
- [ ] **Unplug the adapter mid-session** and plug it back in. The selection should survive, grey
      out, and start working again with no click and no restart.
- [ ] **Set static on a device with its switches set to a fixed address**, to see `NotSettable`
      against real hardware and confirm nothing is written.

---

## Run 6 - the image backup, end to end

**A different article, a different risk, and the only run that exercises both halves of this tool at
once.** A FANUC image backup over Ethernet is BOOTP and *then* TFTP: the controller is restarted
into its boot monitor, brings its port up with no address, broadcasts a BOOTP request, takes the
address it is offered, and only then sends a TFTP write request for the image. Runs 1-5 prove the
first half against an EtherNet/IP adapter. This run proves it against the client that actually
matters here, and puts the second half on a wire for the first time.

It is also the run that may show the TFTP work was not needed. If the backup fails in the BOOTP
half, that is a good outcome and the tool is already built for it.

### Kit and prerequisites

- **One FANUC robot that can be taken out of service for an hour.** An image backup takes 10-30
  minutes on its own and the controller is down for all of it - that is true of any image backup,
  not something this run adds.
- The exact controller and software: R-30iA, R-30iB and R-30iB Plus differ in the boot monitor, and
  PaintTool's version matters. **Write it down.** Everything below is understood from the field
  rather than from a manual anybody here has read.
- Whatever restarts that controller into its boot monitor. Reported to be **F1+F5 held at power-up**;
  unconfirmed for your controllers, so find out before the robot is down.
- **The laptop on the same segment as the robot, and NOT the backup server.** If tftpd32/tftpd64 is
  serving your DHCP, it already holds UDP/67, and NetControl will refuse to bind alongside it rather
  than let Windows deliver requests to whichever socket it chooses. That refusal is correct - see
  Run 6a.
- Wireshark, capture filter `port 67 or port 68 or port 69`.
- **Whoever owns the backup server**, or their permission, before Run 6b stops it.
- **The spike published, and its firewall rule added, before the day.** `pwsh
  tools/publish-tftp-spike.ps1` produces an exe at a path that stays put and prints the elevated
  `netsh` line for it; a rule naming a `bin\Debug` path that the next build replaces is not a rule.
  Prove it with `--port 6969` in one window and `--send 127.0.0.1 --port 6969` in another - neither
  needs UDP/69, so nothing is disturbed, and an argument typo is found here rather than with a
  controller sitting in its boot monitor.

### Safety - this one is different

> **Run 6b stops the plant's TFTP server and deliberately fails a backup.** Not a side effect: the
> watch cannot record what a controller asks for without holding UDP/69, and the real server cannot
> hold it at the same time. Agree that with whoever owns the server before the robot is down, and
> agree when it goes back.
>
> **The TFTP watch refuses every transfer it sees.** Left armed and forgotten, it is a backup server
> that fails every backup silently - the same class of mistake as leaving Serve mode running, and it
> deserves the same care. Stop it and restart the real server before you leave.
>
> The BOOTP half of Run 6a transmits nothing at all and can be run on a live segment. **Run 6b
> cannot.** Nothing else in this file involves production equipment; this does.

---

### Run 6p - Ask the real TFTP server a question, from a laptop on its network

**No robot, no outage, nobody's permission but the server owner's, and it can be done this
afternoon.** Everything else in Run 6 waits for a controller to be free; this one does not, and it
answers four of the catalogue's failure modes on its own.

The laptop needs to be on the same network as the TFTP server - not on it, beside it, which is the
controller's vantage rather than the server's, and the whole point.

```powershell
# Once, so the exe stays where the firewall rule points.
pwsh tools/publish-tftp-spike.ps1

# Then, from the laptop, at the server:
artifacts\tftp-spike\tftp-spike.exe --send 10.0.0.5 --from 10.0.0.55 --log probe.txt
artifacts\tftp-spike\tftp-spike.exe --send 10.0.0.5 --from 10.0.0.55 --blksize 1468 --tsize 134217728 --log probe.txt
```

It sends **one** write request and no file data at all. A server that accepts it usually creates the
name first, so this leaves an empty `netcontrol-probe.tmp` in the server's root - agree that with
whoever owns it, and do not point `--file` at anything that already exists.

| What it reports | What it settles |
|---|---|
| Which **address and port** answered | A different port is correct and is TFTP moving the transfer. A different *address* is a multi-homed server whose answers a correct client discards as an unknown transfer id - invisible from every log there is |
| Nothing answered at all | The request or the reply is being dropped. The reply comes back from a fresh port, so a firewall or NAT that does not track TFTP eats it while the request itself arrived |
| Which options were **granted** versus offered | Whether `blksize` survives to this server at all, which decides everything in the rollover arithmetic |
| An error code, in `TftpErrorText`'s words | Whether the root is writable by the account the service runs as, and whether overwrite is allowed - the two settings that differ per server product and refuse the most writes |

- [ ] Recorded the address and port the answer came from
- [ ] Recorded which options were granted, and which were quietly dropped
- [ ] Recorded whether the write was accepted or refused, and the exact error if refused
- [ ] Told the server's owner about the file left behind, and where

**Do this before scheduling 6b.** If the server refuses a write from the laptop, no controller was
ever going to get one either, and the robot never needed to come down.

---

### Run 6a - Watch the BOOTP half of a *successful* backup

Costs nothing, disrupts nothing, transmits nothing, and gets the single most valuable capture in
this document. Leave the real DHCP and TFTP servers exactly as they are.

Select the adapter, press **Watch**, restart the controller into its boot monitor and start a normal
image backup.

**Expect:** a `BOOTP REQUEST` row with the robot's MAC and its vendor from the OUI table, then the
backup running to completion as usual, because the real server answered it and NetControl said
nothing.

| If | Then |
|---|---|
| A request arrives and the backup completes | The BOOTP half is healthy. The failure you are chasing is downstream, and Run 6b is where to look. |
| A request arrives and the backup does **not** proceed | The controller was offered an address and did not take it. Every cause is already in CLAUDE.md's protocol gotchas - short reply, wrong send mode, wrong adapter. **This is a BOOTP problem and Part E is not what fixes it.** |
| The request repeats with the same `xid`, collapsed into a counter | Same finding, and the counter is the evidence. |
| Nothing arrives at all | Check the interface bar before anything else, then whether the controller reached its boot monitor at all. |
| NetControl refuses to bind UDP/67 | Expected if the backup server is on this laptop. Move NetControl to another machine on the segment; do not stop the DHCP server for 6a. |

- [ ] A real FANUC BOOTP request appeared in the log
- [ ] **Saved the pcap.** This is a real BOOTP client that is not a Rockwell adapter, and `Frames.cs`
      has never held one.
- [ ] Recorded whether the backup then completed normally

### Run 6b - Watch the TFTP half. What does the controller actually ask for?

The run this whole feature exists for, and the one that costs a backup.

Stop the real TFTP server, then:

```powershell
# Check what you are walking into first - adapters, who owns UDP/69, the firewall, the root folder.
artifacts\tftp-spike\tftp-spike.exe --list --root "C:\TFTP-Root"

# Then watch. Records every request and refuses it; transfers nothing, ever.
artifacts\tftp-spike\tftp-spike.exe --log run6b.txt --project run6b.netcproj
```

**Both switches, every time.** The deliverable of this run is three short strings printed once, in a
plant, by somebody with a robot to put back: `--log` keeps a transcript flushed line by line, and
`--project` writes the append-only rows that can be read back months later. Started with neither,
the spike says so before it binds - and a console window that gets closed is how an hour of robot
downtime becomes nothing at all.

**The app has no TFTP tab yet**, so `tftp-spike` is the whole surface for this run - it is a door
into the same `TftpWatchServer` the app will use, so what it prints is what the app will report
once that tab exists. `spikes/README.md` has the rest of its switches.

**Leave the app running in Watch mode on UDP/67 at the same time.** Different port, no conflict, and
it costs nothing - 6a captured the BOOTP half of a backup that worked, and this is the only chance
to capture the BOOTP half of one that did not. If the two halves ever disagree, that is the finding.

Restart the controller into its boot monitor and start a backup.

**Expect:** one `WRQ` row naming the filename **exactly as the controller sent it**, the transfer
mode, the options offered, and the source port. Then a refusal, and a backup that fails.

| What the row says | What it means |
|---|---|
| A filename you did not expect - a drive prefix, a leading separator, a path with folders in it | **The likeliest answer to the whole problem.** TFTP cannot create directories, so every folder in that path has to exist under the server's root already. Compare it against the root by hand. |
| Mode `netascii` rather than `octet` | The image would arrive corrupted with nothing reporting an error. Worth knowing even though it is unlikely. |
| Options offered - `blksize`, `tsize`, `timeout` | **Settles an open question in PLAN-TFTP.md.** If `blksize` is offered, a large image can cross without 512-octet blocks; if it is not, the transfer is capped at 33,553,920 bytes before the counter wraps. |
| No options at all | RFC 1350 defaults. Note it - it decides how E4's probe should be configured. |
| No WRQ arrives at all, though 6a showed the BOOTP request | The controller took an address and never asked for the transfer, or the request never reached this machine. Check the firewall on UDP/69 and the route. |

- [ ] Recorded the filename **verbatim**, separators and all
- [ ] Recorded the transfer mode and every option offered
- [ ] **Saved the pcap.** `TftpFrames.cs` holds frames built to match what a client *should* emit.
      Replace them with what a FANUC *does*.

**Then the question the watch cannot answer about itself.** The refusal leaves NetControl's
listening socket, so its source port is 69 rather than a fresh transfer identifier. That is what
servers do for an immediate rejection and it is unproven against this controller.

- [ ] Did the controller **stop** on receiving the refusal, or keep retransmitting until it timed
      out? It is not discoverable any other way, and you are not getting a second hour - so if it
      retransmits, try the other behaviour before the controller goes back:

```powershell
artifacts\tftp-spike\tftp-spike.exe --refuse-from-ephemeral --log run6b.txt --project run6b.netcproj
```

      That answers from a fresh socket carrying a transfer identifier of its own, bound to the
      address the request arrived on. Every log row names the port its refusal left from, so the
      two attempts stay tellable apart afterwards.

- [ ] Recorded which of the two the controller accepted. If it is the ephemeral one,
      `TftpRefusalSource.TemporarySocket` becomes the default and the switch goes.

- [ ] **Stopped the watch and restarted the real TFTP server**, and confirmed a normal backup runs
      again before leaving. The spike refuses every transfer it sees; left running, it is a backup
      server that fails everything silently.

### Run 6c - the diagnostic run, when it next fails for real

Not a proving run. This is how the tool gets used once it works, and it is worth rehearsing once so
nobody is reading instructions during a breakdown.

When a backup fails on a shift, run 6a's passive watch in the app - it transmits nothing and needs
nobody's permission. Then:

| The BOOTP request | Says |
|---|---|
| Never arrives | The controller is not asking. Wrong port, wrong VLAN, link down, or it never reached the boot monitor. Nothing to do with TFTP. |
| Arrives, and the backup proceeds | The first half is fine. The problem is in the TFTP half, and 6b is the follow-up - scheduled, with the server owner's agreement, not during the breakdown. |
| Arrives and repeats, and the backup stalls | The controller is ignoring the offer it is being given. A BOOTP problem, in the half this tool already knows most about. |

That table is the feature. One passive observation splits the problem in half, which is the thing
nobody can do today.

---

## What to bring back

In rough order of value:

1. **The pcaps.** Especially a real BOOTP request and a real reply, which go into `Frames.cs` and
   are worth more than anything else in that project - and, from Run 6, a real FANUC BOOTP request
   and a real TFTP write request, which go into `Frames.cs` and `TftpFrames.cs`. Both files
   currently hold frames built to match what a client *should* emit.
2. **The answer to Run 4.** Flash or RAM.
3. **The answer to the reply-send-mode question** in Run 2.
4. **Any quirk.** If the device misbehaved in a way that is not already in `DeviceQuirks`, add a
   flag there, add the matching scenario to `NetControl.DeviceSim`, and add a test. That is what
   stops the fix silently regressing once the hardware is back in the panel.
5. **The project file.** It is the commissioning record, and every step of Run 3 is in it.
6. **Model, firmware revision, and the OUI vendor string** the tool showed, so the note is about a
   specific piece of hardware rather than about "an adapter".
7. **From Run 6: the filename, the mode and the options** a FANUC asks for, recorded verbatim. Three
   short strings that close three open questions in `PLAN-TFTP.md` at once, and that no manual and
   no server log will give you. **The transcript and the project file**, which is what "recorded"
   means here - `--log run6b.txt --project run6b.netcproj`. A window that got closed is not a
   record, and nobody is coming back for a second hour to redo it.
8. **Whether the controller accepted the refusal or retransmitted through it.** One observation, and
   the only way to settle how the watch should send it.

If Run 6 happened, `PLAN-TFTP.md`'s open questions are the other thing to revisit - most of them are
written specifically so that one image backup answers them.

Then update the "what is actually proven" section in `CLAUDE.md`. Move what held up into the proved
list, and be precise about what still has not been - the gap between those two is where the
remaining risk lives, and it is only useful while it stays honest.
