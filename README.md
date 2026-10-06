# NetControl

A Windows tool for putting IP addresses onto industrial equipment, and for finding out what is
already there. It replaces the Rockwell BOOTP/DHCP tool, and adds the things that tool never had:
writing the address in and reading it back, scanning and comparing against the plan, device
diagnostics, robot TFTP backup troubleshooting, Modbus reads, PROFINET naming and a commissioning
record you can hand to the customer.

This guide is also inside the application: **Help > User guide**, or press **F1**.

## Contents

- [Safety first](#safety-first)
- [Installing and updating](#installing-and-updating)
- [Quick start: commissioning a panel](#quick-start-commissioning-a-panel)
- [The main window](#the-main-window)
- [The interface bar](#the-interface-bar)
- [Projects and the plan](#projects-and-the-plan)
- [Watching and serving BOOTP/DHCP](#watching-and-serving-bootpdhcp)
- [Set static and Enable BOOTP/DHCP](#set-static-and-enable-bootpdhcp)
- [Device quirks](#device-quirks)
- [Scanning the network](#scanning-the-network)
- [Diagnostics](#diagnostics)
- [TFTP robot backups](#tftp-robot-backups)
- [Modbus](#modbus)
- [Passive listening](#passive-listening)
- [PROFINET](#profinet)
- [Subnet calculator](#subnet-calculator)
- [The commissioning report and the record](#the-commissioning-report-and-the-record)
- [Appearance](#appearance)
- [Menus and shortcuts](#menus-and-shortcuts)
- [Settings and where files go](#settings-and-where-files-go)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Licence](#licence)

## Safety first

NetControl talks to live plant networks. It is built so that nothing reaches a device unless you
asked for it by name, but you are still the one pressing the button. The rules it follows:

- **It never writes to a device you did not pick.** Discovery is broadcast; every write is sent to
  one address you typed or selected. There is no "configure everything you found".
- **Serving addresses takes two deliberate actions.** Serve mode only ever answers MACs that are in
  your plan, and only after **Arm serve** is ticked. A rogue DHCP server on a plant network is a
  serious incident; NetControl will not become one by accident.
- **Nothing is reported as done until it has been read back off the device.** Some hardware says
  "success" and throws the write away. NetControl only says **Verified** after reading the address
  back.
- **Reads stay reads.** Diagnostics never clear a counter, Modbus has no write function at all,
  the service check only asks identity-style questions, and Passive listening sends nothing.
- **Probes go to one host.** Ping, the service check and the diagnostics read refuse broadcast,
  multicast and network addresses before sending anything.
- **TFTP is careful with the server.** The probe only writes the test file you name, needs its tick
  on every run, and never deletes anything. Receiving a backup never writes outside the folder you
  chose.
- **Everything that puts a packet on the network is recorded** in the project file, with the time,
  the target, what was sent and what came back.

Version 1.0.0 is reserved for the build that commissions a real panel without the Rockwell tool.
Until then, treat results on customer equipment with the care any new tool deserves.

## Installing and updating

**[Download the latest release.](https://github.com/Robbuie/NetControl/releases/latest)** Each
release comes two ways. Both are the same build, and neither needs administrator rights, a driver
or a .NET runtime installed first.

| Download | Use it for |
|---|---|
| `NetControl-Setup-<version>.exe` | A machine you use every week. Installs for your user only into `%LOCALAPPDATA%\Programs\NetControl`, with a Start menu entry and an uninstaller. |
| `NetControl.exe` | A laptop you have for the afternoon, or a machine where nothing may be installed. Copy it anywhere and run it. |

The installed copy is the better choice if you can: a Windows Firewall rule names an executable,
and an installed exe stays at the same path.

**Windows SmartScreen.** The exe is not code-signed, so the first time you run it Windows may say
it "protected your PC". Choose **More info > Run anyway**. On a locked-down laptop it may be
blocked outright, which is a conversation with plant IT.

**Updates.** At start-up NetControl asks GitHub, once, whether there is a newer release. If there
is, a line appears in the status bar naming the version - click it to install. If the check fails
(most plant networks have no route out) nothing is shown; **Help > Check for updates** always gives
an answer.

- An **installed** copy downloads the new installer and runs it silently, then restarts.
- A **portable** copy downloads the new `NetControl.exe`, swaps itself for it and restarts. The old
  one is kept beside it as `NetControl.exe.superseded` until the next start, in case you want it
  back.
- Every download is checked against the SHA256 published with it. A file that does not match is
  deleted, never run.
- An update is refused while the listener is running or a device is being commissioned, and the
  dialog says which.

To turn the update check off on a machine, see [Settings and where files go](#settings-and-where-files-go).

## Quick start: commissioning a panel

The common job: a panel full of new EtherNet/IP devices, all asking for an address.

1. **Patch the laptop into the panel's switch** and give the laptop's adapter a static address on
   the panel's subnet.
2. **Pick that adapter** in the interface bar at the top of the window. Wait for the light to go
   green, or read what it says is wrong.
3. **Press Watch.** Every BOOTP/DHCP request appears in **Live requests** at the bottom, with its MAC
   and vendor. Nothing is transmitted.
4. **Double-click a request** (or right-click > **Add to plan**). The device lands in the plan with
   its MAC and vendor filled in. **Type its address** off the drawing, plus mask, gateway and a role
   if you like. Or import the whole plan from a CSV with **File > Import plan from CSV**.
5. **Tick Arm serve, then press Serve.** Each planned device gets its address on its next request.
   Its state moves from Planned to Seen to Served.
6. **Select a row and press Set static.** The address is written into the device, BOOTP/DHCP is
   turned off, and it is read back. When the readback agrees, the row says **Verified** and the
   address survives a power cycle. **Set static on all** does every ready row, one at a time.
7. **Press Scan** to confirm what is on the segment and compare it against the plan.
8. **File > Save as** to keep the project, and **File > Export commissioning report** for the
   customer.

## The main window

From top to bottom:

| Area | What it is |
|---|---|
| **Title bar** | The menus: File, Listener, View, Tools and Help. |
| **Interface bar** | The adapter you are working on, whether it is ready, and the Watch, Serve and Stop buttons. See [The interface bar](#the-interface-bar). |
| **Status line** | The project file name (or "not saved to a file yet"), counts of planned devices, requests, served and unknown, the running version, and an update notice when there is one. |
| **Plan** | The devices you intend to commission, and the buttons that act on them: Add device, Remove, Set static, Enable BOOTP/DHCP, Set static on all, Ping plan and Diagnose. |
| **Tabs** | Live requests, Scan results, TFTP backup, Diagnostics, Modbus, Passive and PROFINET. |

Almost everything has a tooltip. Hover over a button or a column header if you are not sure what it
will do.

## The interface bar

When "nothing is arriving", the cause is usually the adapter, another program holding the port, or
the firewall. The interface bar checks all three before you press anything, so you do not have to
guess.

- **Adapter.** Pick the one patched into the panel - NetControl never picks for you. Each entry
  shows its address, link state and description. The bar warns if the adapter has no link, no IPv4
  address, or only an APIPA (169.254.x.x) address. If a USB adapter is unplugged, the choice is
  kept and greyed; plug it back in and it carries on by itself.
- **UDP/67.** Whether another program (another BOOTP tool, a DHCP server, VMware, Hyper-V) already
  holds the BOOTP/DHCP port, and how much that matters.
- **Firewall.** Whether Windows Firewall will let the requests in. If it will not, the bar shows the
  exact `netsh` command to add a rule.
- **Listener.** Whether NetControl is listening, and in which mode.

The light is green only when every check passed. **Grey means "not checked"**, never "fine". Amber
is a warning, red is blocked. **Listener > Re-check port and firewall** runs the checks again.

If a request arrives on a different adapter from the one you picked, the bar says so - which is
usually the answer to "why is nothing happening".

## Projects and the plan

### Project files

A project is one file (`.netcproj`) holding the plan, everything that was served, every write and
readback, the scans and a full event log. There is **no Save button**: changes are written as you
type. A new project lives only in memory until you give it a location with **File > Save as**; the
status line says "not saved to a file yet" until then.

| File menu | |
|---|---|
| **New project** | Starts an empty project. |
| **Open project...** | Opens a `.netcproj`. Files from older versions are upgraded; a file from a newer version is refused with a note to update. |
| **Save as...** | Copies the whole project - plan, assignments and event log - to a file and keeps working there. Never overwrites an existing file. |
| **Import plan from CSV...** | Checks the whole file first. If anything is wrong, nothing is imported and every problem is listed with its line number. |
| **Export plan to CSV...** | The plan only. What was served and verified stays in the project's record. |
| **Export commissioning report...** | See [The commissioning report and the record](#the-commissioning-report-and-the-record). |

### The plan grid

One row per device. Edit cells directly; a mistake is shown in words beside the row while it is
still on screen, for example `'192.168.1.999' is not an IPv4 IP address`.

| Column | Meaning |
|---|---|
| **MAC** | The device's hardware address - the key the plan is matched on. |
| **Planned IP** | The address it should have. NetControl never invents one: it comes off your drawing. |
| **Subnet mask / Gateway** | Sent with the address. Leave the gateway blank if there is none. |
| **Host name, Panel, Role** | Your names for it. The Device column in the log uses Role, then Host name, then Panel. |
| **Vendor** | Looked up from the IEEE registry when the MAC is entered. |
| **State** | Planned, Seen (asked for an address), Served (an address was sent), or Verified (read back off the device). |
| **Reach** | Filled in by **Ping plan**. Silence is amber, not red - plenty of drives and I/O adapters ignore ping. |
| **Quirks** | What this device is known to do wrong. See [Device quirks](#device-quirks). |
| **Notes** | Anything else. |

A **red** row means the value could not be stored. An **amber** row stored fine but will not be
served yet - usually a MAC with no address so far. That is a normal state while you work, not an
error.

Right-click a row for **Device quirks...**, **Diagnose this device** and **Read Modbus registers**.

### CSV plans

The header row names the columns, in any order. NetControl writes
`Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes`, and also accepts common alternatives such as
`MAC Address`, `IP Address`, `Subnet Mask`, `Default Gateway`, `Host`, `Panel`, `Tag`, `Location`,
`Description` and `Comment`. A `Vendor` column is ignored. Fields containing commas can be quoted in
the usual way.

```
Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes
00:1D:9C:12:34:56,192.168.1.21,255.255.255.0,192.168.1.1,io-01,CP-3,Remote I/O,
00:1D:9C:AB:CD:EF,192.168.1.51,255.255.255.0,192.168.1.1,vfd-01,CP-3,Conveyor drive,"drive, panel 3"
```

Re-importing a CSV keeps each device's quirks.

## Watching and serving BOOTP/DHCP

### Watch

**Watch** listens on UDP/67 on every adapter and shows each request in **Live requests**: time,
message type, MAC, the device's name if it is in the plan, vendor, the adapter it arrived on, a
**Repeats** counter (retransmits are collapsed into one row) and what happened. **Watch transmits
nothing.**

- Tick **Only devices that are not in the plan** to see just the strangers.
- **Double-click** a row, or right-click > **Add to plan**, to plan that device. If it is already in
  the plan, its row is selected instead.
- **Clear** empties the list.

### Serve

**Serve** answers requests - but only from MACs in the plan that have a complete address, and only on
the adapter you selected. Tick **Arm serve** first; the adapter has to be up and hold a real address.
You can keep editing the plan while serving: plan a device you just saw ask, and it is served on
its next retry.

**Stop** stops either mode.

An address served over BOOTP/DHCP only lasts until the device is power cycled, because the device
is still a BOOTP client. To make it permanent, use [Set static](#set-static-and-enable-bootpdhcp).

### Listener menu

| | |
|---|---|
| **Watch / Serve / Stop** | Same as the buttons. |
| **Re-check port and firewall** | Runs the interface bar checks again. |
| **DHCP boot options...** | Next server, TFTP server name, boot file, domain name and DNS servers, sent in every reply this project serves (as header fields, and as DHCP options 66, 67, 15 and 6 to clients that ask). Blank by default, and blank means replies are unchanged. |
| **Reply by per-socket bind** | How replies are sent out of the chosen adapter. Leave it ticked unless you are comparing the two methods on a bench. |

## Set static and Enable BOOTP/DHCP

### Set static

Select a plan row and press **Set static**. Over EtherNet/IP (CIP) NetControl:

1. Reads the device's Configuration Capability. If its address is set by rotary or DIP switches, it
   says so and **writes nothing**.
2. Sets Configuration Control to static (BOOTP/DHCP off).
3. Writes the planned address, mask and gateway.
4. Reconnects and **reads the configuration back**.

Only when the readback agrees does the row say **Verified**. Every step is shown in the live log and
written to the project as it happens.

**Allow reset if needed** is off by default. Some devices keep using the old address until they are
reset. Left off, such a device is reported as unverified instead of being restarted behind your
back; tick it to let NetControl reset that one device.

### Set static on all

Runs Set static on every complete row that is not already Verified, **one device at a time**, after a
confirmation that lists each device. **Stop** lets the device in progress finish and starts no more.

### Enable BOOTP/DHCP

The reverse, for handing a device back to a plant DHCP server. **Enable BOOTP/DHCP > Enable BOOTP on
the selected device** (or DHCP) writes Configuration Control only, behind a confirmation. The device
keeps its current address until its next power cycle, then asks for one again.

Verified devices are remembered: reopening the project shows them as Verified, unless the planned
address has been changed since.

## Device quirks

Some devices misbehave in known ways. The **Quirks** column shows them in short form, and
right-click > **Device quirks...** lets you tick or untick each one. Set static fills most of them in
by itself from what the device actually did.

| Quirk | What NetControl does about it |
|---|---|
| **Address pinned by switches** | Says so before anyone presses Set static, and refuses before writing. |
| **Needs a reset to apply** | Set static plans for a reset, and performs one only when "Allow reset" is ticked. |
| **Says yes and discards the write** | Informs only - the readback already catches it every time. |
| **Slow to answer** | Set static waits three times as long to connect and twice as long for the readback. |
| **Drops the connection on a write** | Informs only - a lost connection after writing is reported unverified, never done. |
| **Rejects an address while in BOOTP/DHCP** | Informs only - Set static always switches to static first. |
| **Ignores broadcast discovery** | Every scan also asks this device's planned address directly, so it is not missed. |

## Scanning the network

The **Scan results** tab shows devices that already **have** an address - the opposite of Live
requests, which shows devices **asking** for one.

**Scan** sends one EtherNet/IP ListIdentity broadcast (UDP/44818) from the selected adapter and
collects the replies for two seconds: address, product, revision, serial, MAC (from the ARP table)
and vendor. Nothing is written to anything.

- **The plan and the segment disagree** appears when the scan contradicts the plan: a planned
  address that something else is already sitting on, two devices answering on one planned address,
  or a planned device answering from a different address than the one planned for it.
- **Double-click** a result, or right-click > **Add to plan**, to plan it **at the address it already
  holds** - handy for writing a DHCP-served address in permanently with Set static.
- Right-click > **Diagnose this device** opens it on the Diagnostics tab.
- If a row cannot be planned, the Note column says why (for example, its MAC is not in the ARP
  table).

**Scan history.** Every scan is kept in the project. The next scan of the same subnet lists what
changed: a device that moved address, a module that was swapped, firmware that changed, something
new and something that has gone quiet.

## Diagnostics

Open the **Diagnostics** tab with **Diagnose** on the plan, right-click > **Diagnose this device**,
**Tools > Diagnostics...**, or by typing an address into **Device**. Nothing is sent until you press
a button.

| Button | What it does |
|---|---|
| **Ping** | ICMP echo, up to two attempts. Silence is not proof the device is off. |
| **Read device diagnostics** | Reads the device's own account of itself over EtherNet/IP: identity, fault bits, address conflict detection, whether a written address is waiting for a reset, and every port's speed, duplex and error counters. **Read-only - it never clears a counter.** |
| **Check services** | Tries a short list of TCP ports one at a time and, on each open one, asks one read-only question only the real protocol can answer. |
| **Open web page** | The device's own web page, when it has one - often where its detailed diagnostics live. |
| **This PC's adapter counters** | Your laptop's own error and discard counters. A bad patch lead on the laptop looks like a device fault from everywhere else. |

**Read twice.** After the first read, a second one reports which counters are **still moving**, not
just what has happened since power-up. NetControl names the likely cause: half duplex with late
collisions is a duplex mismatch, FCS errors point at a cable, discards point at multicast flooding.

**Services checked:** FTP (21), SSH (22), Telnet (23), HTTP (80), S7 / ISO-TSAP (102), HTTPS (443),
Modbus/TCP (502), OPC UA (4840), MELSEC MC (5007), GE SRTP (18245), DNP3 (20000) and EtherNet/IP
(44818). A port shows **verified** with what the device said, or **open, unproven** in amber when
something accepted the connection but did not answer like the real protocol.

**Ping plan** (on the plan, or **Tools > Ping the plan**) pings every planned address a few at a time
and fills in the **Reach** column. An answer proves something is at that address - not that it is the
device you planned.

## TFTP robot backups

For robot image backups that "never start". A FANUC backup over Ethernet is an address request
followed by a file request, and when it fails the pendant cannot tell you which half broke.
NetControl can.

### Watch a backup

1. **Stop the real TFTP server** on this PC if there is one - NetControl will not share UDP/69 with
   it.
2. Tick **This PC is the backup server** if you are on the server itself; leave it clear on a laptop
   plugged into the robot network. On the server, set **Backup folder** to the TFTP root and press
   **Check folder** - it confirms the folder exists, is writable and has room.
3. Tick **Arm**, press **Watch TFTP**, and start the backup on the pendant.

Every file request is listed with the **filename exactly as the controller sent it**, the transfer
mode, where it came from, which adapter it arrived on and its options. By default each request is
refused on purpose, so nothing is received. A **netascii** request is flagged, because an image sent
that way arrives corrupted.

Together with the BOOTP listener at the top of the window (press **Watch** there too), the tab grades
four steps and names **the earliest one that did not happen**, with what to do about it:

1. Asked for an address
2. Got an address
3. Asked for the file
4. Sent the file

Nothing turns green until it has actually been seen. **Re-check** tests who holds UDP/69, the
firewall and the backup folder again. **Clear** starts the verdict over for the next attempt.

### Receive the backup

Tick **Receive the backup** before pressing Watch TFTP and NetControl accepts the backup into the
backup folder. If the backup works into NetControl but fails against the real server, the robot and
the network are fine and **the server's settings are the cause** - the verdict says so.

It never writes outside the folder, refuses a filename the folder cannot hold (a missing subfolder,
a name Windows would change) in the words a real server would need, and will not replace an
existing file unless **Allow overwrite** is ticked.

### Probe a server

**Probe a server** writes a test file to the real TFTP server from this PC and, with **Read it back**
ticked, reads it back and compares. Fill in the **Server** address (as set on the controller), a
**Test file** name (never the name of a real backup) and a **Size**. Tick **Write this file to that
server - it is left there** and press **Run probe**.

It reports whether the server accepts writes, which address it answers from, which options it
grants, and whether the transfer's own ports get through the firewall. The default 40 MB is past
the 32 MB a 512-byte block counter can reach, so one run also shows whether the server survives the
block counter rolling over - which corrupts large images. The tick is needed for every run, and
nothing is ever deleted.

The tab remembers the backup folder, the server setting, the overwrite setting and the probe's
server address.

## Modbus

Read **holding registers, input registers, coils or discrete inputs** from one device. Right-click a
plan row > **Read Modbus registers**, or type the address.

- **Device, Port** (502), **Unit** (1 for most devices on Ethernet; behind a gateway, the serial
  address; 255 asks the gateway itself).
- **Start** is zero-based as sent on the wire: holding register 40001 is start 0. Results show both
  numberings.
- **Count** up to 125 registers or 2000 bits.
- **Read** once, or **Poll every second** to watch a value change, then **Stop**.

Each register is shown as unsigned, signed, hex, binary, and as 32-bit float, Int32 and UInt32
together with the next register. **32-bit order** switches between HighFirst (ABCD, the usual) and
LowFirst (CDAB, "word swapped") without reading again.

**There is no Modbus write anywhere in NetControl.**

## Passive listening

The **Passive** tab lists every device heard on the selected adapter **without sending a single
frame** - by its ARP, BOOTP/DHCP, LLDP, PROFINET and EtherNet/IP traffic. Devices asking for an
address and getting none are highlighted.

Press **Start listening**. **Promiscuous** also picks up frames addressed to other devices; untick it
if listening will not start (some Wi-Fi adapters refuse it). Columns show MAC, vendor, address, name,
what it was heard by, VLAN, frame count and detail.

Needs the free **Npcap** driver - see below.

## PROFINET

Press **Identify**. One DCP Identify goes out from the selected adapter, and every PROFINET device
answers with its station name, type, address, mask, gateway and vendor. Nothing is changed. Then
select a device:

| Control | What it does |
|---|---|
| **Set name** | The station name, as the controller's project names it (lowercase letters, digits, `-` and `.`). |
| **Set address** | Address, mask and gateway (blank for none). |
| **Keep after a power cycle** | Leave ticked for a permanent change; untick for a quick bench test. |
| **Flash LED** | Makes the device blink so someone at the panel can find it. Stores nothing. |
| **Read again** | Reads its name and address as the device holds them now. |

Every change is confirmed naming the device, sent to that one device only, read back and recorded.

### Npcap

The Passive and PROFINET tabs need **Npcap**, a free packet-capture driver
([npcap.com](https://npcap.com), or install Wireshark, which includes it). Without it those two tabs
say so, with a **Get Npcap** button and **Check again**. Everything else in NetControl works without
it.

## Subnet calculator

**Tools > Subnet calculator** opens on the selected adapter's own subnet. Type an address with a
prefix or mask (`192.168.1.51/24` or `192.168.1.51 255.255.255.0`) to see network, mask, host range,
usable host count, broadcast and wildcard. **Is this on it?** checks whether another address - a
device, a gateway - is on that subnet.

## The commissioning report and the record

**File > Export commissioning report** writes the whole job as one HTML page for the customer: every
device, what it was served, every write and its readback, the scans and the full event log. Open it
in any browser and print to PDF.

**The record** is an append-only event log inside the project file. Every operation that changes a
device, and every read that put packets on the network, is a row - with the time, target, what was
sent and what came back. Nothing in NetControl can edit or delete those rows, and each project file
names the NetControl build that opened it.

If the status line shows **Event log incomplete - a row failed to write**, the listener kept working
but the record has a gap. The detail is in the diagnostic log.

## Appearance

**View > Appearance...** sets three things independently, previewed live as you choose:

- **Theme** - eleven, shared with File Manager, Redline PDF and File Compare, including Control room
  (a grey ISA-101-style theme where colour only appears on a fault). By default NetControl
  **follows Windows' light and dark mode**, with a light and a dark theme of your choice.
- **Accent** - the highlight colour.
- **Density** - how much room the chrome takes.

OK saves; Cancel puts back what was there.

## Menus and shortcuts

| Menu | Items |
|---|---|
| **File** | New project, Open project, Save as, Import / Export plan CSV, Export commissioning report, Exit |
| **Listener** | Watch, Serve, Stop, Re-check port and firewall, DHCP boot options, Reply by per-socket bind |
| **View** | Appearance |
| **Tools** | Subnet calculator, Ping the plan, Diagnostics |
| **Help** | User guide (F1), Check for updates, Releases page, Open the diagnostic log folder, About NetControl |

**F1** opens this guide from anywhere in the main window. In the guide, type in the search box to
narrow the contents to the sections that mention a word.

## Settings and where files go

NetControl keeps its own things in `%LOCALAPPDATA%\NetControl\`, never beside the exe:

| | |
|---|---|
| `logs\netcontrol-<date>.log` | The diagnostic log - why the application misbehaved. Rolls daily and at 4 MB, keeps ten files. **Help > Open the diagnostic log folder.** |
| `settings.json` | Optional, absent by default. |
| `updates\` | A downloaded update, briefly. Cleared at the next start. |

Project files are wherever you saved them. The diagnostic log is **not** the commissioning record -
that is inside the project file.

`settings.json` can hold:

```json
{
  "checkForUpdates": false,
  "updateManifestUrl": "https://intranet.example/tools/netcontrol/version.json"
}
```

- `checkForUpdates: false` stops NetControl making any outbound request at all.
- `updateManifestUrl` points the update check at an intranet mirror for sites whose laptops cannot
  reach github.com. A web address or a `file://` path on a share both work.

The uninstaller leaves `settings.json` in place on purpose, so a reinstall keeps the site's choice.

## Troubleshooting

**Nothing appears in Live requests.** Read the interface bar first - it is usually the answer. Check
the right adapter is selected and has link; check UDP/67 is not held by another program (close the
Rockwell tool, a DHCP server or a virtual machine's network service); check the firewall, and run
the `netsh` line the bar shows if it is blocking. If the bar says requests are arriving on a
different adapter, select that one or move the patch lead.

**Serve is greyed out.** Arm serve needs an adapter that is up and holds a real IPv4 address (not
APIPA). Give the laptop a static address on the panel's subnet.

**A device asks but is never answered.** Its row is amber: it needs a complete MAC and address. Only
planned devices are ever served.

**Set static says unverified.** Read the log line: the address may be pinned by switches, the device
may need a reset (tick **Allow reset if needed** and try again), or it may have said yes and kept its
old address. Check the Quirks column.

**A device does not show up in a scan.** Some devices ignore broadcast discovery - tick that quirk
on its row and the next scan asks its planned address directly. A device behind a router will not
answer a broadcast at all.

**Passive or PROFINET says Npcap is missing.** Install Npcap from npcap.com, then press **Check
again**.

**The TFTP watch will not start.** Something else holds UDP/69 - usually the real TFTP server. Stop
it while you watch, and start it again afterwards.

**Windows protected your PC.** The exe is unsigned. **More info > Run anyway.**

**NetControl 0.9.0 does not open after installing.** Install 0.9.1 or later by hand over the top;
projects and settings are kept.

**Something else went wrong.** **Help > Open the diagnostic log folder** and send the latest log,
together with the version shown in the status bar and in **Help > About NetControl**.

## Building from source

```powershell
dotnet build
dotnet test
dotnet run --project src/NetControl.App

pwsh tools/publish.ps1              # -> artifacts/NetControl-<version>/NetControl.exe
pwsh tools/publish.ps1 -Installer   # -> dist_installer/NetControl-Setup-<version>.exe as well
```

Cutting a release is a tag push - see [RELEASING.md](RELEASING.md). [DEPLOY.md](DEPLOY.md) covers
installing, updating and mirroring builds in more depth.

`src/NetControl.Core` is the engine and must not reference any UI assembly. `src/NetControl.App` is
the WPF front end, `src/NetControl.DeviceSim` is a fake EtherNet/IP adapter that makes the CIP work
testable at a desk, and `tests/NetControl.Tests` is where the claims above are checked.
[CLAUDE.md](CLAUDE.md) is the working document: the layering rule, the protocol gotchas, and what is
proven by a test versus only seen working once. Read its "What is actually proven, and what is not"
section before trusting anything here in front of customer equipment.

This README is compiled into the application and shown by **Help > User guide**, so editing it
updates the in-app help at the next build.

## Licence

Copyright reserved - see [LICENSE](LICENSE). The repository is public so the tool can be read,
audited and downloaded; it is not open source, and nothing here may be copied into another product.

The code is built from public specifications only: RFC 951, RFC 1542, RFC 2131/2132, RFC 1350 and
ODVA's published CIP and EtherNet/IP documentation. Nothing here is derived from decompiling the
Rockwell tool or from Wireshark's GPL dissector.
