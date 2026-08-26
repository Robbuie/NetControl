# Phase 0 Spikes

Two throwaway console apps that answer the risky questions before any real code gets written.
Both are also usable as stopgap tools right now.

**Prerequisite:** .NET 10 SDK. `winget install Microsoft.DotNet.SDK.10`

> Neither of these has been compiled yet - there was no .NET SDK available where they were
> written. Expect to fix a typo or two on first build. The protocol logic is the part that
> matters; the compiler will find anything else fast.

---

## Spike 1 - `bootp-spike`

Answers: *can we reliably tell which NIC a request arrived on, and can we tell the user why nothing is arriving?*

```powershell
cd spikes\Spike1.BootpListen
dotnet run -- --list
```

`--list` prints every IPv4 interface with its index, address, link state, and speed - then tells
you **which process owns UDP/67**, by name and PID. That alone diagnoses a good share of
"the tool sees nothing" cases.

Watch mode. Logs every request with its true arrival NIC, sends nothing:

```powershell
dotnet run -- --nic "I219"
```

Serve mode. Only the MACs you name get answered:

```powershell
dotnet run -- --nic "I219" --serve 00:00:BC:2E:69:6F=192.168.1.51 --mask 255.255.255.0
dotnet run -- --nic "I219" --csv panel-A.csv
```

CSV columns: `mac,ip,mask,gateway,hostname` (header row optional, `#` comments allowed).

**If the device never sees the reply,** re-run with `--send-mode persocket`. Pinning a limited
broadcast to one interface is the one thing this spike is genuinely testing - `IP_UNICAST_IF` is
the tidier mechanism, the per-socket bind is the reliable fallback. Which one wins on your
hardware is a finding worth writing down.

### What to record
- Did binding UDP/67 work **without** administrator rights?
- Did `IPPacketInformation.Interface` match the adapter you actually plugged into?
- Which `--send-mode` reached the device?
- Did Windows Firewall prompt, and did blocking it produce silence?

---

## Spike 2 - `cip-spike`

Answers: *can we set a static IP over EtherNet/IP and prove it took?* This is the replacement
for the Rockwell tool's "Disable BOOTP/DHCP" button.

```powershell
cd spikes\Spike2.CipStaticIp

dotnet run -- discover 192.168.1.10     # ListIdentity broadcast; pass your NIC's IP
dotnet run -- read 192.168.1.51         # read-only dump: identity, MAC, TCP/IP object
dotnet run -- set 192.168.1.51          # keep the address, switch method to static
dotnet run -- set 192.168.1.51 --ip 10.10.20.51 --mask 255.255.255.0 --gw 10.10.20.1
```

**Always run `read` first.** It reports the Configuration Capability bits, which tell you up
front whether the device will accept a write at all - if the address is pinned by rotary
switches on the module, no amount of CIP will move it, and `read` says so instead of failing
mysteriously.

`set` does three things in order, because order matters on real hardware:

1. Configuration Control (attr 3) -> Static
2. Interface Configuration (attr 5) -> the addresses
3. Reconnect, read both back, and compare against what was asked for

It only prints VERIFIED if step 3 agrees. Anything else is reported as a mismatch with the
specific field that disagreed.

### What to record
- Which devices need attr 3 set before attr 5, and which do not care
- Which need an Identity reset (`--reset`) or a full power cycle before it sticks
- Any device returning status `0x0E` (not settable) or `0x0C` (state conflict), and what fixed it

Those findings become the per-device quirk table in Phase 2.

---

## Known gaps

Deliberate, since these are spikes:

- No retransmission or duplicate-request suppression in the BOOTP server
- No lease tracking - assignments are static mappings only
- OUI table is a hand-written handful of vendors, not the full IEEE registry
- `set` writes name servers as 0.0.0.0 and an empty domain name
- No logging to file; console only
