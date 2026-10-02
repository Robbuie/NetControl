# Changelog

Newest first. The release workflow copies a version's section onto its GitHub release page, so
write it for the person deciding whether to install the update. The heading has to start
`## <version>` for that to find it.

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
