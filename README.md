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

```powershell
dotnet build
dotnet test
dotnet run --project src/NetControl.App
```

The thing that goes on a plant laptop is one self-contained exe with no installer, no admin rights
and no driver:

```powershell
pwsh tools/publish.ps1              # -> artifacts/NetControl-<version>/NetControl.exe
```

See [DEPLOY.md](DEPLOY.md) for what to do with it, and for the optional update check.

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

Not yet decided - see the note at the top of [DEPLOY.md](DEPLOY.md). The code is built from public
specifications only: RFC 951, RFC 1542, RFC 2131/2132 and ODVA's published CIP and EtherNet/IP
documentation. Nothing here is derived from decompiling the Rockwell tool or from Wireshark's
GPL dissector.
