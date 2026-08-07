# Controls Network Toolkit — Build Plan

**Target:** Windows-only desktop tool, C# / .NET 10 LTS + WPF. Internal use, no installer/licensing burden.
**Phase 1 goal:** a BOOTP/DHCP commissioning tool that is boringly reliable where the Rockwell one is not.

> This is the overview. Detailed work breakdown, data model, state machine, and milestones are in
> [ROADMAP.md](ROADMAP.md). Working conventions and protocol gotchas are in [CLAUDE.md](CLAUDE.md).

---

## 1. Why the Rockwell tool frustrates people

Worth being precise about this, because each failure mode maps to a design decision:

| Symptom | Likely cause | Our fix |
|---|---|---|
| "No requests appear" even though the device is blinking | Bound to the wrong adapter (VPN, Hyper-V vSwitch, docking-station NIC) | Bind `0.0.0.0:67` once, use `IP_PKTINFO` to learn which NIC each packet *actually* arrived on. Show that per packet. |
| Tool starts but silently never receives | UDP 67 already held by VMware/VirtualBox DHCP, another BootP tool, or a WSL/Hyper-V service | Detect the conflict at startup and **name the owning process**. Never fail silently. |
| "Disable BOOTP/DHCP" reports success, device reverts on power cycle | Config-control write not committed, or not verified | Write, then **read back** and confirm; optionally power-cycle-verify before marking done. |
| Relation list lost / corrupted | Fragile `.bpc` file, single-file, no history | SQLite project file, append-only event log, CSV import/export. |
| No idea what's already on the network | Tool only listens; it never asks | EtherNet/IP `ListIdentity` broadcast — enumerate every already-configured device with product name, revision, serial. |
| UI freezes during assignment | Blocking socket work on the UI thread | Everything async; engine is a headless library the UI merely observes. |

The last row matters architecturally: **the network engine must have zero UI dependencies**, so the same code drives the GUI, the CLI, and the tests.

---

## 2. Solution layout

```
BootP.DHCP.sln
├─ src/
│  ├─ NetControl.Core/            net10.0  — no UI references, ever
│  │   ├─ Interfaces/      NIC enumeration, binding, link/health state
│  │   ├─ Dhcp/            BOOTP + DHCP packet codec, server engine, lease/offer policy
│  │   ├─ Cip/             EtherNet/IP encapsulation, CIP objects, session mgmt
│  │   ├─ Discovery/       ListIdentity, ARP table, ICMP sweep, TCP probe
│  │   ├─ Commissioning/   per-device state machine, bulk runner
│  │   └─ Persistence/     SQLite project store + append-only event log
│  ├─ NetControl.App/             WPF, MVVM (CommunityToolkit.Mvvm)
│  ├─ NetControl.Cli/             headless: `ctk commission plan.csv --nic "Intel I219"`
│  └─ NetControl.DeviceSim/       fake EtherNet/IP device — dev without hardware
└─ tests/
   └─ NetControl.Tests/           xUnit; codec round-trips + sim-driven integration
```

`NetControl.DeviceSim` is not optional polish — it is what lets you write and regression-test the whole thing at a desk instead of on a running line.

**Dependencies (deliberately few):** `CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite`, `Serilog`, `System.CommandLine`, `xUnit`. No npcap in Phase 1 — plain UDP sockets are enough, and avoiding a driver dependency keeps this deployable on locked-down plant laptops.

---

## 3. Protocol specifics (the parts worth getting right up front)

### 3.1 BOOTP / DHCP server

- **No admin rights needed.** Windows does not reserve ports below 1024 the way Unix does, so binding UDP 67 works as a standard user. Verify this in Phase 0 — it's a real usability win if it holds.
- **Receive:** one socket on `IPAddress.Any:67`, `EnableBroadcast = true`, `SocketOptionName.PacketInformation = true`, read with `ReceiveMessageFrom` → `IPPacketInformation.Interface` gives the arrival interface index. This is the single most important line in the project.
- **Reply:** the device has no IP yet, so the reply goes to `255.255.255.255:68` out a socket bound to the chosen NIC's own address — or unicast to `yiaddr` only when the broadcast flag is clear.
- **Handle both dialects.** A device in BOOTP mode sends plain RFC 951 `BOOTREQUEST`. A device in DHCP mode wants a full DORA exchange (RFC 2131) with option 53, plus 54 (server id), 51 (lease), 1 (mask), 3 (gateway). Rockwell adapters ship DHCP-enabled from the factory more often than people expect — supporting only BOOTP is a common cause of "it just won't show up."
- **Firewall:** inbound UDP 67 will trigger a Windows Firewall prompt. Add a rule on first run and tell the user plainly if it's blocked.

### 3.2 Setting a static IP over EtherNet/IP (CIP)

This replaces "Disable BOOTP/DHCP." Sequence over TCP 44818:

1. `RegisterSession` (encapsulation cmd `0x0065`) → session handle.
2. `SendRRData` (`0x006F`), CPF = Null Address Item (`0x0000`) + Unconnected Data Item (`0x00B2`).
3. **TCP/IP Interface Object, class `0xF5`, instance 1:**
   - Attribute **3** = Configuration Control → `0` = use stored static config (`1` = BOOTP, `2` = DHCP).
   - Attribute **5** = Interface Configuration struct: IP, mask, gateway, name server 1, name server 2, domain name. All little-endian.
   - Attribute 1 = Status, attribute 2 = Configuration Capability — read these *first* to know what the device will actually accept.
4. **Ethernet Link Object, class `0xF6`, attribute 3** = MAC address — use it to confirm you configured the device you think you did.
5. **Read back** attributes 3 and 5, then re-confirm with a `ListIdentity`. Only then mark the device green.
6. Some devices need an Identity Object (`0x01`) service `0x05` reset, or a power cycle, before the config sticks. Track that as a per-device quirk flag.

Order matters on many devices: set attribute 3 before attribute 5.

### 3.3 Discovery

`ListIdentity` (encapsulation cmd `0x0063`) broadcast to UDP 44818 returns vendor ID, device type, product code, revision, serial number, product name, and IP for every EtherNet/IP device on the segment. Cheap to implement, and it's the feature that makes this tool obviously better rather than merely more reliable.

### 3.4 Sourcing note

Build from public specs — RFC 951, RFC 1542, RFC 2131/2132, and ODVA's published CIP/EtherNet/IP documentation. Wireshark's ENIP dissector is a good *reference for understanding* the wire format, but it is GPL, so read it for comprehension and write your own codec. Don't decompile the Rockwell tool.

---

## 4. Data model

Project file = one SQLite `.db`.

- `Device` — mac, planned ip/mask/gw, hostname, panel/tag reference, vendor (offline IEEE OUI lookup), role, notes, quirk flags
- `Assignment` — what was actually served, when, over which NIC
- `Event` — append-only: every packet decision, every CIP write, every verification result, timestamped

That event log is a free deliverable: it's a commissioning record you can hand to a customer or use to prove what happened during a startup.

---

## 5. Phases

**Phase 0 — De-risk (1–2 weeks).** Two throwaway console spikes, no UI:
1. Receive a real BOOTP request from real hardware and correctly identify the arrival NIC. Confirm whether admin rights are needed for port 67.
2. Write CIP `0xF5` attr 3 + attr 5 to one adapter and read it back.

If both work, everything downstream is ordinary application development. If either surprises you, the plan changes — so do these first.

**Phase 1 — MVP replacement (4–6 weeks).** WPF shell; explicit NIC picker with live link/IP/port-67-conflict status; live request log of unassigned MACs; manual assignment; relation list persisted to SQLite; CSV import/export. Ship it and use it on a real job.

**Phase 2 — Static IP + verification (3–4 weeks).** CIP client, static-IP write with readback, per-device state machine (`Discovered → Served → Configured → Verified`), device quirk handling, `NetControl.DeviceSim` built out.

**Phase 3 — Bulk & CLI (2–3 weeks).** CSV plan → commission a whole panel in one pass, with per-device progress and a pass/fail report. `NetControl.Cli` for scripted/repeat builds.

**Phase 4 — Scanning (later).** ICMP sweep, ARP table read, TCP port probe, `ListIdentity` enumeration, topology view via LLDP/CDP where switches expose it.

**Phase 5 — Broader toolkit (later).** Wireless survey, interface statistics, packet capture (npcap, optional install), subnet calculator, cable/link diagnostics, Modbus TCP and PROFINET DCP discovery.

---

## 6. Open questions for later

- **Multi-homed laptops:** serve on multiple NICs at once, or force a single active interface? Serving on several is more capable but much easier to get wrong.
- **PROFINET DCP:** the equivalent Siemens-side workflow. It *does* need raw Ethernet frames and therefore npcap + admin — that's why it's Phase 5, not Phase 1.
- ~~**Naming.**~~ Settled at the start of Phase 1: the product is **NetControl**, namespaces
  `NetControl.Core` / `.App` / `.Cli` / `.DeviceSim`. The repository folder is still `BootP.DHCP`;
  renaming it is cosmetic and can wait.

---

## 7. Immediate next step

Phase 0, spike 1: a ~150-line console app that binds UDP 67, logs every BOOTP/DHCP packet with its arrival NIC, and decodes the header. It answers the riskiest question in the project in an afternoon.
