# Phase 0 Findings

Empirical results from the spikes. These drive Phase 1 design decisions, so anything recorded here
should be something that was actually observed, not assumed.

Test machine: Windows, .NET SDK 10.0.302, 12 non-loopback adapters (VMware x2, VirtualBox,
OpenVPN TAP x2, Bluetooth PAN, Wi-Fi Direct x2, Hyper-V x3, Intel I219-V).

## Status: Phase 0 complete - 2026-08-06

| | Question | Result |
|---|---|---|
| Q1 | Admin rights to bind UDP/67 | **Not required** |
| Q2 | `IP_PKTINFO` identifies arrival NIC | **Works** |
| Q3 | `IP_UNICAST_IF` pins a broadcast reply | **Works** (same-machine caveat) |
| Q4 | CIP attr 3 -> attr 5 -> readback | **Works** vs. simulator |

No design assumption in ROADMAP.md was invalidated. Phase 1 can proceed as written.

**The one caveat carried forward:** everything above was proved against a simulator on a single
machine. Real hardware can still surprise us on Q3 (does a remote device actually receive the
pinned broadcast?) and Q4 (does the configuration survive a power cycle?). Keep the per-socket
send fallback until a real adapter says otherwise.

---

## Q1 - Does binding UDP/67 require administrator rights?

**No.** 2026-08-06.

```
ok   Bound to 0.0.0.0:67   (process elevated: False)
ok   Bind succeeded WITHOUT admin rights - Windows does not reserve low ports.
```

Unlike Unix, Windows does not reserve ports below 1024. The BOOTP/DHCP server needs no elevation.

**Consequence for Phase 1:** ship without an elevation manifest. The tool stays a copy-and-run
executable, which is the difference between using it on a plant laptop and raising a ticket with
IT. Guard this - if a later feature needs admin (npcap for PROFINET DCP, Phase 5), it must be an
optional component that does not drag elevation onto the core tool.

The Windows Firewall prompt for inbound UDP/67 is a separate matter and still needs handling in
Phase 1.

---

## Q2 - Does `IPPacketInformation.Interface` correctly identify the arrival adapter?

**Confirmed working.** 2026-08-06.

```
21:07:18.048  BOOTP REQUEST  mac=00:00:BC:5E:11:01  xid=0xFC5D1465
              via [18] vEthernet (Wifi Switch)  dst=255.255.255.255
```

Corroborated by the reply arriving at the simulator sourced from `192.168.50.172:67`, which is
interface 18's own address - so the interface the stack reported on receive is the same one the
reply actually left by.

**Consequence for Phase 1:** the `IP_PKTINFO` approach is sound. Bind `0.0.0.0:67` once and
attribute each datagram to its arrival interface, rather than binding per-adapter. This is the
mechanism that fixes the "wrong NIC" class of failure.

---

## Q3 - Which send mode reaches the device?

**`IP_UNICAST_IF` works.** 2026-08-06. The default (`--send-mode unicastif`) delivered the reply;
the per-socket fallback was not needed.

This was the single biggest open question in the design - Microsoft's documentation is not explicit
about whether `IP_UNICAST_IF` governs a limited broadcast, and it fails silently when it does not.
It does.

**Caveat, and it matters:** this was a same-machine test. The broadcast left via interface 18 and
was picked up by a local listener. It is strong evidence but not proof that a *remote* device on
the wire receives it. Re-confirm against real hardware before deleting the per-socket fallback.

**Consequence for Phase 1:** build on `IP_UNICAST_IF` (one socket, set the option per reply). Keep
the per-socket path as a fallback until hardware confirms it is unnecessary.

---

## Q4 - Does the CIP attr 3 -> attr 5 -> readback sequence stick?

**Sequence confirmed against the simulator.** 2026-08-06. Hardware still outstanding.

The full exchange ran clean: capability read, attr 3 -> Static, attr 5 -> addresses, then a fresh
session to read both back.

```
Configuration Capability : BootpClient, DnsClient, DhcpClient, ConfigurationSettable
[1/3] Configuration Control (attr 3) -> Static      accepted
[2/3] Interface Configuration (attr 5)              accepted
[3/3] Verifying
      device reports: ip=127.0.0.1  mask=255.255.255.0  gw=0.0.0.0
      config method : Static
MISMATCH - mask: wanted 255.255.0.0, got 255.255.255.0
```

**The `LiesAboutWriteSuccess` case behaved correctly** - the device returned CIP success and
discarded the write, and the readback caught it. This is the regression test that matters most:
it is the exact failure the Rockwell tool does not catch. If it ever reports VERIFIED, the
verification logic has broken.

Session handling, EPATH encoding, CPF framing, and the little-endian CIP address encoding all
round-tripped correctly between the independently written client and server.

**Still outstanding:** real hardware. The simulator only reproduces quirks already known and
written down, so it cannot discover a new one.

**The `HardwarePinnedAddress` case also behaved correctly.** Capability came back `0x27`
(BootpClient, DnsClient, DhcpClient, HardwareConfigurable) with `ConfigurationSettable` clear.
`set` read attribute 2, stopped, and closed the session - the server log confirms **no write was
ever sent**:

```
21:21:43.784    CIP service 0x0E on class 0xF5, instance 1, attribute 2
21:21:43.788    UnRegisterSession
```

Checking capability before attempting a write is therefore worth keeping as a hard rule in Phase 2.
It converts "the write mysteriously failed" into "the address is pinned by the switches on the
module", which is a different quality of error message.

`read` also exercised the full object graph cleanly: Identity `Get_Attribute_All`, Ethernet Link
attribute 3 (MAC), and TCP/IP attributes 1, 2, 3 and 5.

Remaining local cases:

```powershell
# expect VERIFIED - the spike sets attr 3 first, so the refusal never triggers.
# Reversing the order in Set() should produce status 0x0C. That is the point of the test.
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk RejectConfigWhileDynamic --method bootp

# expect VERIFIED only after --reset
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk RequiresResetToApply
```

### Simulator fidelity gap

TCP/IP attribute 1 (Interface Status) is currently a crude `Static ? 1 : 2`. The CIP spec defines
the low nibble as a configuration-status enumeration, and real devices also carry the mcast-pending
bit. Nothing in Phases 1-3 reads it, but do not treat the simulator as authoritative for that
attribute.

---

## Incidental findings

**A stock Windows dev machine already has a DHCP server on port 67.**

```
svchost  pid 5048  172.22.160.1:67
```

That is the Hyper-V Default Switch. Bound to a specific address rather than `0.0.0.0`, so it
competes only for requests on its own subnet - but it is exactly the class of thing that makes the
Rockwell tool sit silently. The conflict detector earned its place on the first run.

Phase 1 should distinguish a wildcard bind from an address-specific one, because the severity is
completely different. Implemented in the spike.

**The tool detected its own sibling process.** A leftover `bootp-spike` from a previous terminal
showed up as a conflict on `0.0.0.0:67`. Fixed by excluding the current PID and labelling
same-named processes explicitly. Worth keeping in the product - a stale instance holding the port
is a real failure mode, and the message should name it as such rather than leaving the user to
wonder.

**Windows chose interface 18 for a 255.255.255.255 broadcast** out of 12 candidates. Which adapter
Windows picks for a limited broadcast is not obvious and not something to rely on - another
argument for pinning the interface explicitly rather than letting the stack decide.

**A listener that fails to bind must take the whole process down with it.** The simulator's TCP
listener threw on `Start()` when a previous instance still held 44818. `Task.WhenAll` kept waiting
on the UDP loop, which was still healthy, so the process sat there answering discovery while
refusing every connection - and the only visible symptom was one missing line of startup output.

Fixed by failing fast and saying which port and why. The general rule for Phase 1: **a component
that cannot do its job must say so loudly and stop, not degrade into a half-working state.** That
is the entire complaint about the tool this replaces.

**Adapter inventory is the product's first job.** Twelve interfaces, of which exactly one is a real
NIC and it was `Down` with an APIPA address. Nothing about that is unusual for an engineering
laptop, and picking the right adapter out of that list by guesswork is precisely the problem the
tool exists to solve.

---

## Carried into Phase 1 - one deliberate divergence from the spike

`Spike1` padded every reply to 536 bytes (236-byte header plus a 300-byte options area). That is
not what RFC 1542 asks for: section 2.1 specifies a 300-octet minimum *message* length, which is the
236-byte header plus the 64-byte vendor field. `NetControl.Core.Dhcp.BootpPacket` now pads to 300.

Nothing observed in Phase 0 depended on the difference - the simulator accepts either - but it
means the exact frame length that Q3 was proved with is not the frame length the engine sends.
Worth knowing if a real adapter ever ignores a reply.

The knob is `BootpPacket.MinimumMessageLength`. If a device turns out to want more, 548 is the
value to try: an RFC 2131 client must accept a 312-octet options field.
