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

## What to bring back

In rough order of value:

1. **The pcaps.** Especially a real BOOTP request and a real reply. They go into `Frames.cs` and
   they are worth more than anything else in that project.
2. **The answer to Run 4.** Flash or RAM.
3. **The answer to the reply-send-mode question** in Run 2.
4. **Any quirk.** If the device misbehaved in a way that is not already in `DeviceQuirks`, add a
   flag there, add the matching scenario to `NetControl.DeviceSim`, and add a test. That is what
   stops the fix silently regressing once the hardware is back in the panel.
5. **The project file.** It is the commissioning record, and every step of Run 3 is in it.
6. **Model, firmware revision, and the OUI vendor string** the tool showed, so the note is about a
   specific piece of hardware rather than about "an adapter".

Then update the "what is actually proven" section in `CLAUDE.md`. Move what held up into the proved
list, and be precise about what still has not been - the gap between those two is where the
remaining risk lives, and it is only useful while it stays honest.
