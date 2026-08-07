# CLAUDE.md

Guidance for working in this repository.

## What this is

A Windows network tool for controls engineers. It replaces the Rockwell BOOTP/DHCP tool with
something reliable, then grows into a broader network toolkit (scanning, discovery, diagnostics).

It talks to live industrial equipment. Read the Safety section before writing anything that
sends packets.

## Stack

- .NET 10 (LTS), C# 13, `net10.0` — Windows only
- WPF + MVVM via CommunityToolkit.Mvvm for the GUI
- SQLite via Microsoft.Data.Sqlite for project files
- xUnit for tests
- No npcap dependency in Phases 1–3; plain UDP/TCP sockets only. Adding a driver dependency
  makes deployment on locked-down plant laptops much harder, so it stays out until PROFINET
  DCP genuinely requires it.

Keep dependencies few. Every package added is a package someone has to justify to plant IT.

## Layout

```
NetControl.sln                solution — spikes are deliberately NOT in it
src/NetControl.Core/          network engine — MUST NOT reference any UI assembly
    Interfaces/               NIC inventory, UDP port ownership, firewall state
    Dhcp/                     BOOTP/DHCP codec, server engine, assignment policy
src/NetControl.App/           WPF front end                        (not started)
src/NetControl.Cli/           headless commissioning               (not started)
src/NetControl.DeviceSim/     fake EtherNet/IP device for hardware-free development
tests/NetControl.Tests/       xUnit
spikes/                       Phase 0 throwaways; not part of the solution build
_trash/                       things the agent sandbox could not delete — safe to remove
```

**The layering rule is the important one.** `NetControl.Core` must stay UI-free so the same code path
drives the GUI, the CLI, and the tests. If something in Core needs to report progress, it raises
an event or returns a stream — it does not know a window exists.

## Commands

```powershell
dotnet build
dotnet test
dotnet run --project src/NetControl.App
dotnet run --project src/NetControl.Cli -- commission plan.csv --nic "I219"

# spikes are standalone
dotnet run --project spikes/Spike1.BootpListen -- --list
dotnet run --project spikes/Spike2.CipStaticIp -- read 192.168.1.51
```

## Safety — this touches live plant networks

These are hard rules, not preferences:

- **Never write to a device that was not explicitly named by the user.** Discovery is broadcast;
  configuration is always unicast to one address the user typed or selected.
- **Never broadcast a write.** No "configure everything you found."
- **Never send a CIP reset, forward-open, or firmware operation as a side effect** of a read or
  scan operation.
- **Scanning defaults must be gentle.** Industrial devices have thin TCP stacks and some fall
  over under aggressive port scans. Default to low concurrency and a conservative rate; make
  anything faster opt-in and clearly labelled.
- **Serve mode must be explicit.** The BOOTP/DHCP server only answers MACs the user has listed.
  A rogue DHCP server on a plant network is a genuinely serious incident.
- **Every state-changing operation gets logged** to the append-only event log with timestamp,
  target, what was sent, and what came back.

## Protocol gotchas

Hard-won, easy to get wrong, expensive to debug:

- **Endianness flips between layers.** BOOTP/DHCP headers are big-endian. EtherNet/IP
  encapsulation and CIP are little-endian — *including IP addresses carried inside CIP
  attributes*, which is the reverse of `IPAddress.GetAddressBytes()`. The `ListIdentity` reply
  contains a `sockaddr_in` that is big-endian sitting inside an otherwise little-endian
  structure. Convert deliberately; never memcpy an address.
- **`IP_PKTINFO` is how you know the arrival NIC.** Bind `0.0.0.0:67`, set
  `SocketOptionName.PacketInformation`, read with `ReceiveMessageFromAsync`, use
  `IPPacketInformation.Interface`. Do not infer the interface from the bind address.
- **`IP_UNICAST_IF` (option 31) takes the interface index in network byte order** for IPv4.
  Getting that wrong fails silently.
- **BOOTP replies are limited broadcasts.** The client has no address yet, so there is nothing
  to unicast to and no ARP entry to use.
- **Pad BOOTP replies to a 300-octet minimum *message* length** (RFC 1542 §2.1 — the 236-byte
  header plus a 64-byte vendor/options area). Some older adapters drop short replies.
  `BootpPacket.MinimumMessageLength` is that 300. If a device ever needs more, the other value
  worth trying is 548, since an RFC 2131 client must accept a 312-octet options field.
- **CIP attribute order matters.** Set TCP/IP Interface Object (`0xF5`) attribute 3
  (Configuration Control) to Static *before* writing attribute 5 (Interface Configuration).
  Several adapters reject attribute 5 while still in BOOTP/DHCP mode.
- **Always read back after a write.** A CIP success status means the request was accepted, not
  that the configuration persisted. Reconnect and re-read before reporting success.
- **Check Configuration Capability (`0xF5` attr 2) first.** If `ConfigurationSettable` is clear,
  the address is pinned by hardware switches and no write will ever work.

## C# specifics that have already bitten us

- **`Span<T>` locals are illegal in `async` methods** (CS4013). Keep packet encode/decode in
  plain static helpers and call them from the async method. Passing `x.AsSpan()` as an argument
  is fine; declaring `var s = x.AsSpan();` inside an async method is not.
- **Do not call `NetworkInterface.GetAllNetworkInterfaces()` per packet.** It costs
  milliseconds. Use a short-TTL cache — adapters do come and go, so a permanent cache is wrong too.
- **CA1416 does not flow through lambdas.** An `if (OperatingSystem.IsWindows())` guard does not
  cover a LINQ lambda inside the block. Put Windows-only work in a method carrying
  `[SupportedOSPlatform("windows")]` and call that from the guard.
- **`Directory.Build.props` is imported before the project body**, so it cannot see properties the
  `.csproj` sets — a condition on one silently tests an empty string. Anything reacting to a
  project-level flag goes in `Directory.Build.targets`, which imports last.
- Prefer `BinaryPrimitives.Read/Write*Endian` over `BitConverter`. It states the byte order at
  the call site, which is exactly what this codebase needs.
- **`TreatWarningsAsErrors` is scoped in `Directory.Build.targets`** to projects that set neither
  `IsSpike` nor `IsTestProject`. An xUnit analyser suggestion should not be able to break a build.
- **A cref to a type you have no `using` for is a phantom dependency.** Doc comments do not count
  as usage for unused-using analysis, so either fully qualify the cref or accept the warning.
  `NicInfo` fully qualifies `System.Net.Sockets.IPPacketInformation` for this reason.

## Conventions

- Nullable reference types on, warnings as errors in `src/`.
- `NetControl.Core` throws typed exceptions (`EnipException`, etc.); the UI catches and presents.
- Error messages name the likely cause and the next action. "Bind failed" is useless;
  "UDP/67 is held by vmnetdhcp.exe (pid 4312) — stop that service and retry" is the product.
- Comments explain *why*, especially for protocol quirks. The what is already in the code.
- One class per file in `src/`; spikes may be looser.

## Licensing

Build from public specs: RFC 951, RFC 1542, RFC 2131/2132, and ODVA's published CIP and
EtherNet/IP documentation.

- **Do not decompile or reverse-engineer the Rockwell tool.**
- Wireshark's ENIP dissector is useful for *understanding* the wire format, but it is GPL.
  Read it to learn; do not copy code from it into this repository.

## Current state

**Phase 0 is closed.** All four questions are answered in `FINDINGS.md`; both spikes compiled and
ran against `NetControl.DeviceSim`. The one caveat carried forward is that everything was proved
on a single machine — Q3 (does a *remote* device receive an `IP_UNICAST_IF`-pinned broadcast?) and
Q4 (does the configuration survive a power cycle?) still want real hardware.

**Phase 1 is in progress.** Done so far:

- `NetControl.Core/Interfaces` — `NicInfo`, `NicMonitor`, `PortConflictDetector`, `FirewallCheck`
- `NetControl.Core/Dhcp` — `BootpPacket` codec, `DhcpServer`, `IAssignmentPolicy` +
  `StaticMapPolicy`, `RetransmitFilter`, structured event records
- `NetControl.Tests` — codec round-trips, reply construction, policy refusals, MAC parsing

Still to do in Phase 1: §1.3 SQLite persistence, §1.4 the OUI database, §1.5 the WPF shell.

**None of the Phase 1 code has been compiled** — it was written without a .NET SDK to hand, same
as the spikes were. Expect small build fixes on the first `dotnet build`.

- `spikes/README.md` — what each spike proved.
- `src/NetControl.DeviceSim/README.md` — how to exercise both spikes without hardware, including
  the quirk scenarios.

`NetControl.DeviceSim` arrived earlier than the roadmap called for because it makes the spikes testable
at a desk. It is the regression harness for everything after: when a real device misbehaves in a
new way, add a `Quirk` for it and a test, so the fix cannot silently regress once that hardware is
back in the panel.
