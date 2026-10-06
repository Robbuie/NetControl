# Changelog

Newest first. The release workflow copies a version's section onto its GitHub release page, so
write it for the person deciding whether to install the update. The heading has to start
`## <version>` for that to find it.

## 0.9.2 - a user guide in the Help menu

- **A user guide, in the Help menu.** Help > User guide, or F1 anywhere in the main window, opens
  the full guide: a quick start for commissioning a panel, every tab and menu, the device quirks,
  TFTP backup troubleshooting, settings and a troubleshooting list. It has a contents list and a
  search box, follows the current theme, and works with no network - it is the repository's README,
  built into the exe.

## 0.9.1 - fixes NetControl not opening after install

- **0.9.0 did not start on a machine that had not built it.** Opening it did nothing: no window, no
  error. The exe was missing five native DLLs that WPF needs to draw its first window, because the
  publish left them as loose files beside the exe and the installer and release only carry the exe.
  They are now bundled inside `NetControl.exe` and unpacked on first run. Nothing else changed.
- **If you installed 0.9.0, install this one by hand.** A copy that cannot start cannot check for its
  own update. Run `NetControl-Setup-0.9.1.exe` over the top; your projects and settings are kept.
- The build now refuses to publish if anything other than the exe is left in the output folder, so
  this cannot ship silently again.

## 0.9.0 - verified services, device quirks, boot options, Modbus, PROFINET, passive listening and a new look

Project files written by this version are schema version 3 (project settings, for the DHCP boot
options). Older builds will refuse to open them and say to update; this build opens every older file
and upgrades it.

- **The service check proves the protocol.** An open port only proves something accepted a
  connection. Each known port is now asked one read-only question only the real protocol can
  answer - EtherNet/IP identity, Modbus device identification, an S7 connection request, the MELSEC
  CPU model (port 5007, new to the list), an OPC UA hello, an HTTP HEAD, the SSH/FTP/Telnet
  greeting - and the list says **verified** with what the device said, or **open, unproven** in
  amber when something else is squatting on the port.
- **Device quirks.** A new **Quirks** column on the plan, and **Device quirks...** on a row's
  right-click menu. Set static fills it in by itself from what the device actually did - an address
  pinned by switches, a write held until a reset, a write that said yes and was not kept, a
  connection dropped mid-write - and you can tick or untick any of them, each with a line saying what
  NetControl does about it. "Slow to answer" gives that device longer timeouts; "ignores broadcast
  discovery" makes every scan also ask its planned address directly. Re-importing a CSV keeps them.
- **DHCP boot options.** Listener > DHCP boot options: next server, TFTP server name, boot file,
  domain name and DNS servers for every reply this project serves - in the header fields always, and
  as options 66, 67, 15 and 6 to a DHCP client that asks for them. Blank by default, and blank means
  replies are exactly what they were.
- **Modbus tab.** Read holding registers, input registers, coils or discrete inputs from one device,
  shown as unsigned, signed, hex, binary and 32-bit float and integer in both word orders, with both
  address numberings (0 and 40001). **Poll every second** to watch a value change. Reads only - there
  is no Modbus write anywhere in NetControl. Right-click a plan row > Read Modbus registers.
- **Passive tab.** Listens on the selected adapter and lists every device it hears - by ARP,
  BOOTP/DHCP, LLDP, PROFINET and EtherNet/IP traffic - **without sending a single frame**. Devices
  asking for an address and getting none are highlighted. Needs Npcap (below).
- **PROFINET tab.** DCP Identify finds every PROFINET device with its station name, type and
  address; then set one device's **station name** or **address**, or **flash its LED** to find it in
  the panel. Every change is confirmed naming the device, sent to that one device only, read back,
  and recorded. Needs Npcap.
- **Npcap is optional.** The Passive and PROFINET tabs need the free Npcap driver (npcap.com, or
  install Wireshark, which includes it). Without it those two tabs say so and link to it; everything
  else in NetControl works exactly as before and never looks for it.
- **A new look, matching File Manager.** NetControl's own title bar with the menus in it, the
  working areas as rounded cards, and the theme **follows Windows' light and dark mode** - switching
  live when Windows does - with a light and a dark theme of your choice (View > Appearance). An
  existing appearance setting is kept as it was; following is the default for a new install.

## 0.8.0 - diagnostics, Set static on all, scan history and the commissioning report

Project files written by this version are schema version 2 (the scan history). Older builds will
refuse to open them and say to update; this build opens every older file and upgrades it.

- **Ping.** One device from the new Diagnostics tab, or **Ping plan** on the grid, which pings every
  planned address a few at a time and fills in a new **Reach** column. Silence is amber, never red:
  plenty of drives and I/O adapters ignore ping.
- **Device diagnostics over EtherNet/IP.** Pick a row and press **Diagnose**, then **Read device
  diagnostics**. It reads the device's fault bits, its address-conflict detection, whether a written
  address is still waiting for a reset, and every port's speed, duplex and error counters - and says
  what they mean: half duplex and late collisions are a duplex mismatch, FCS errors are a cable,
  discards are multicast flooding. Read twice and it reports what is still counting rather than what
  happened once since power-up. It only reads, and it never clears a counter.
- **Service check.** Which of a short list of TCP services a device answers on - web page, Modbus,
  S7, OPC UA, EtherNet/IP and a few more - one connection at a time with nothing sent, and an **Open
  web page** button when it has one.
- **Set static on all.** Every complete row that is not yet Verified, one device at a time, after a
  confirmation that lists each one. **Stop** finishes the device in progress and starts no more.
- **Verified is remembered.** Reopening a project shows the devices that were verified, read back
  out of the record - unless the planned address has been changed since.
- **What changed since the last scan.** Every scan is kept in the project, and the next scan of the
  same subnet lists what moved, what was replaced, what has new firmware, what is new and what has
  gone quiet.
- **Tools > Subnet calculator**, opening on the selected adapter's own subnet, and **This PC's adapter
  counters** on the Diagnostics tab - a bad patch lead on the laptop looks like a device problem from
  everywhere else.
- **File > Export commissioning report.** The whole record as one HTML page to hand over at the end
  of a job, which prints to PDF from any browser.

## 0.7.0 - receive the backup, and probe the server

- **Receive the backup.** Tick "Receive the backup" before pressing Watch TFTP and NetControl
  accepts the robot's backup into the backup folder instead of refusing it. If the backup lands here
  and fails against the real server, the robot and the network are fine and the server's settings
  are the cause - the verdict says so. It never writes outside the folder, refuses a name the folder
  cannot hold (a missing subfolder, a name Windows would change) in the words a real server would
  need, and refuses to replace an existing file unless "Allow overwrite" is ticked.
- **Probe a server.** Write a test file to the real TFTP server from this PC and read it back. It
  reports whether the server accepts writes, which address it answers from, which options it grants,
  whether the transfer's own ports get through the firewall, and - at the default 40 MB - whether the
  server survives the block counter rolling over, which corrupts large images. It writes only the
  file named, needs ticking for every run, and never deletes anything.
- **The TFTP tab remembers** the backup folder, whether this PC is the backup server, the overwrite
  setting and the probe's server address.

## 0.6.0 - the TFTP backup tab

For the robot image backups that "never start". A FANUC backup over Ethernet is an address request
followed by a file request, and when it fails the pendant cannot say which half broke. This can.

- **A TFTP backup tab.** Stop the real TFTP server, tick Arm, press Watch TFTP, and run the backup.
  Every file request is listed with the filename exactly as the controller sent it, the mode, where
  it came from and which adapter it arrived on. The request is refused on purpose, so nothing is
  received.
- **Which step it stopped at, in one sentence.** Together with the BOOTP listener at the top of the
  window, the tab grades the four steps - asked for an address, got one, asked for the file, sent the
  file - and names the earliest one that did not happen, with what to do about it. Nothing turns
  green until it has actually been seen.
- **Checks for this PC.** Who holds UDP/69, whether the firewall lets it in, and - on the backup
  server - whether the TFTP root folder exists, is writable and has room. Tick "This PC is the
  backup server" on the server; leave it clear on a laptop on the robot network.
- A netascii request is flagged, because an image sent that way arrives corrupted.

## 0.5.0 - first release

The first build published as a release. It has not yet been in front of a real device - see
"What is actually proven" in CLAUDE.md before using it on customer equipment.

- **Watch**: binds UDP/67 and shows every BOOTP/DHCP request with the adapter it arrived on, the
  vendor from the full IEEE registry, and retransmits collapsed into a counter. Transmits nothing.
- **Serve**: answers only the MACs in the plan, and only after being explicitly armed.
- **Set static**: writes the planned address into one device over CIP and turns BOOTP/DHCP off,
  then reconnects and reads it back. Nothing is reported as done until the readback agrees.
- **Enable BOOTP/DHCP**: the reverse, for handing a device back to a plant DHCP server, behind a
  confirmation.
- **Scan**: one ListIdentity broadcast from the selected adapter, with each device's MAC from the
  ARP table, and a comparison of what answered against the plan.
- **Plans**: edit in the grid, add a device from a log row or a scan result, import and export CSV.
- **Project files**: one SQLite file per project, with an append-only record of everything sent to
  a device.
- **Interface bar**: grades the adapter, the UDP/67 port owner and the firewall before anything is
  pressed.
- **Appearance**: eleven themes, six accents and three densities, shared with Redline PDF, File
  Manager and File Compare - including File Manager's six newer themes, among them Control
  room, a grey ISA-101-style theme where colour only appears on a fault.
- **Install and update**: a per-user installer (no administrator rights) and a portable single exe,
  both on every release. Both check GitHub for a newer release at start-up and update themselves,
  refusing any download whose SHA256 does not match. `"checkForUpdates": false` in settings.json
  turns the check off.
