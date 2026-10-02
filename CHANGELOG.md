# Changelog

Newest first. The release workflow copies a version's section onto its GitHub release page, so
write it for the person deciding whether to install the update. The heading has to start
`## <version>` for that to find it.

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
