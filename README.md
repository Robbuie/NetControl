# NetControl

A Windows tool for putting IP addresses onto industrial equipment, and for finding out what is
already there. It replaces the Rockwell BOOTP/DHCP tool.

It talks to live plant networks. **Read [the safety rules](CLAUDE.md#safety---this-touches-live-plant-networks)
before changing anything that sends a packet.**

## What it does

| | |
|---|---|
| **Watch** | Binds UDP/67 and shows every BOOTP/DHCP request arriving, with the adapter it arrived on, the vendor from the full IEEE registry, and retransmits collapsed into a counter. Transmits nothing. |
| **Serve** | Answers *only* the MACs in the plan, and only after being explicitly armed. |
| **Set static** | Writes the address into the device over CIP and turns BOOTP/DHCP off - then reconnects and reads it back. A CIP success means the request was accepted, not that it persisted. |
| **Enable BOOTP/DHCP** | The reverse, for handing a device back to a plant DHCP server. |
| **Scan** | One ListIdentity broadcast: what is on the segment, what it is, and its MAC via the ARP table. |
| **Compare** | What the scan found against what the plan says - including the address you planned that something else is already sitting on. |
| **TFTP backup** | Watches UDP/69 while a robot runs an image backup, records the file it asks for exactly as sent, and says which step the backup stopped at - the address request, the address, the file request, or the server. Can receive the backup itself, to prove the robot and network are fine, and can probe the real server with a test file read back and compared. |
| **Set static on all** | The same Set static over every ready row of the plan, one device at a time, after a confirmation that names each one. Stop lands between devices, never half way through one. |
| **Ping** | One device, or every planned address at once into a Reach column. |
| **Device diagnostics** | Reads a device's own account of itself over CIP - fault bits, address conflict detection, a configuration waiting for a reset, and every port's speed, duplex and error counters - and names the likely cause: a duplex mismatch, a damaged cable, multicast flooding. Read-only: it never clears a counter. Read twice to see which are still moving. |
| **Service check** | Which of a short list of TCP services a device answers on - web page, Modbus, S7, MELSEC, OPC UA, EtherNet/IP - one connection at a time, and on each open port one read-only question that proves the protocol is really there. |
| **Device quirks** | What each device is known to do wrong, learned from Set static or ticked by hand - and what NetControl does differently because of it. |
| **Boot options** | Next server, TFTP server, boot file, domain and DNS for every reply a project serves. |
| **Modbus** | Read registers, inputs and coils from one device, decoded every common way. Reads only. |
| **Passive** | Lists every device heard on the wire without sending anything. Needs the free Npcap driver. |
| **PROFINET** | DCP identify, then set one device's station name or address - confirmed, read back and recorded - or flash its LED. Needs Npcap. |
| **Scan history** | Every scan is kept, and the next scan of the same subnet says what changed: a device that moved, a module swapped, firmware changed, something new, something silent. |
| **Subnet calculator** | Network, broadcast, host range and whether another address is on it - opening on the selected adapter's own subnet. And this PC's own adapter error counters, because a bad patch lead looks like a device problem from everywhere else. |
| **Report** | The whole commissioning record as one HTML page for the customer: every device, what it was served, every write and its readback, the scans, and the full event log. Prints to PDF. |
| **Record** | Every state-changing operation, in an append-only SQLite table inside the project file. |

## The parts that are different from the tool it replaces

- **Nothing is reported as done until it has been read back off the device.** Hardware exists that
  returns CIP success and discards the write; a status code is not evidence.
- **Configuration Capability is read first.** A device whose address is pinned by switches is told
  about with *nothing written to it at all*, rather than after four failed attempts.
- **"Nothing is arriving" has an answer.** The interface bar grades the adapter, the UDP/67 port
  owner and the firewall before you press anything, and never shows green over a check it could
  not run.
- **The project file is a commissioning record.** Append-only at the database level, not just in
  the API, and it names the build that wrote it.
- **The tool never invents an address.** An address comes off a drawing. A scanned device carries
  the address it reported, and a log row carries none.

## Getting it

**[Download the latest release.](https://github.com/Robbuie/NetControl/releases/latest)** Two ways,
same build, neither needing administrator rights, a driver, or a .NET runtime installed first:

| | |
|---|---|
| `NetControl-Setup-<version>.exe` | Installs per-user into `%LOCALAPPDATA%\Programs\NetControl`. Start menu entry, uninstall, and an exe at a path that stays put - which is what a firewall rule wants. |
| `NetControl.exe` | The same tool as one loose self-contained file. Copy it anywhere and run it. |

The loose file is for a laptop somebody was handed that morning; the installer is for one they use
every week. **Both update themselves** - the installed copy by running the new installer silently,
the portable one by swapping its own executable - and neither runs anything it has not checked
against the checksum published with it. [DEPLOY.md](DEPLOY.md) covers the choice, how updating works
and how to turn it off.

Building it yourself:

```powershell
dotnet build
dotnet test
dotnet run --project src/NetControl.App

pwsh tools/publish.ps1              # -> artifacts/NetControl-<version>/NetControl.exe
pwsh tools/publish.ps1 -Installer   # -> dist_installer/NetControl-Setup-<version>.exe as well
```

Cutting a release is a tag push - see [RELEASING.md](RELEASING.md).

## Layout

`src/NetControl.Core` is the engine and **must not reference any UI assembly** - the same code path
has to drive the GUI, the CLI and the tests. `src/NetControl.App` is the WPF front end,
`src/NetControl.DeviceSim` is a fake EtherNet/IP adapter that makes all of the CIP work testable at
a desk, and `tests/NetControl.Tests` is where the claims above are checked.

[CLAUDE.md](CLAUDE.md) is the working document: the layering rule, the protocol gotchas that have
already cost time, what is proven by a test and what is only "seen working once". Read the
**"What is actually proven, and what is not"** section before trusting anything here in front of
customer equipment.

## Status

Phase 1 is feature-complete and **has never been in front of a real device.** Everything is
exercised against `NetControl.DeviceSim` and hand-built frames. `BENCH.md` is the run sheet for the
session that changes that, and it is worth more than any remaining code.

Version 1.0.0 is reserved for the build that commissions a real panel without the Rockwell tool.

## Licence

Copyright reserved - see [LICENSE](LICENSE). The repository is public so the tool can be read,
audited and downloaded; it is not open source, and nothing here may be copied into another product.

The code is built from public specifications only: RFC 951, RFC 1542, RFC 2131/2132, RFC 1350 and
ODVA's published CIP and EtherNet/IP documentation. Nothing here is derived from decompiling the
Rockwell tool or from Wireshark's GPL dissector - which is what makes publishing it possible at all.
