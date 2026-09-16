# Spikes

Console apps that go where the GUI cannot yet. Spikes 1 and 2 are the Phase 0 throwaways that
answered the risky questions before any real code got written, and both are still usable as stopgap
tools. Spike 3 is a different animal: it is a door into shipping code, not a rehearsal for it.

None of them is in `NetControl.sln`.

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

---

## Spike 3 - `tftp-spike`

Answers: *what does a FANUC controller actually ask a TFTP server for?*

**Not a Phase 0 throwaway.** `NetControl.Core.Tftp` is written, compiled and tested, and
`NetControl.App` has no TFTP surface yet - so this is the only way to put that engine in front of a
robot. It is the one spike that carries a `ProjectReference` to `NetControl.Core`, deliberately: a
second copy of the TFTP codec is the single thing that would make the exercise worthless, because
then what you observed would not be what the product does.

```powershell
# What the interface bar would say, then stop. Safe anywhere, transmits nothing.
dotnet run --project spikes/Spike3.TftpWatch -- --list

# Watch UDP/69. Records every request and refuses it. Transfers nothing, ever.
dotnet run --project spikes/Spike3.TftpWatch

# On one adapter, and grade the server's root folder while you are there.
dotnet run --project spikes/Spike3.TftpWatch -- --nic 12 --root "C:\TFTP-Root"

# Somewhere other than 69, to try it without taking the port the real server needs.
dotnet run --project spikes/Spike3.TftpWatch -- --port 6969
```

Each request prints the filename **exactly as sent**, the transfer mode, the options offered, the
source port and what the watch did about it - plus a `NOTE` line for anything that would go wrong
later, such as a netascii transfer of a binary image.

### Keeping it

The output of a real session is three short strings, printed once, into a console window that gets
ended with Ctrl+C by somebody who has a robot to put back. **A scrollback buffer is not a record**,
so a session in a plant gets both of these:

```powershell
dotnet run --project spikes/Spike3.TftpWatch -- --log bench-6b.txt --project bench-6b.netcproj
```

- `--log` mirrors everything printed to a file, flushed line by line and appended rather than
  overwritten. It is a copy of what a person read, headed with the machine, the account and the
  arguments - because "writable by me" is not "writable by the service account", and six weeks later
  nobody remembers which laptop it was.
- `--project` records into a NetControl project file through `TftpEventRecorder`, which is the same
  class the app will use: one append-only row per request, refusal and fault, and the database
  itself refuses an `UPDATE`. That is the record. It **refuses to start** if the file cannot be
  opened, because a watch running without the record you asked for looks afterwards exactly like a
  watch that saw nothing.
- `--record-retransmits` adds a row per repeat as well. Off by default - identical rows bury the
  first one, and the first one carries the filename - and worth turning on for exactly one question,
  which is how this controller retries.

Started with neither, it says so before it binds.

### The one thing that cannot be worked out from a specification

The refusal leaves the listening socket, so it arrives from port 69 rather than from a fresh
transfer identifier. That is what servers do for an immediate rejection and **no FANUC has been seen
to accept it**. If one ignores the refusal and keeps retransmitting until it times out:

```powershell
dotnet run --project spikes/Spike3.TftpWatch -- --refuse-from-ephemeral --log bench-6b.txt
```

The fresh socket binds the address the request was delivered to, not `0.0.0.0`, since an answer from
an address the client never wrote to is discarded as an unknown transfer id and looks exactly like
silence. Which of the two a controller accepts is a one-line observation that costs an hour of robot
downtime, and both are reachable from the same visit on purpose. The log row names the port each
refusal went out of, so the observation can still be read months later.

### Getting the packets to arrive at all

A firewall rule names an executable, and under `dotnet run` that executable sits in `bin\Debug` and
is rebuilt by the next run. Publish one that stays put:

```powershell
pwsh tools/publish-tftp-spike.ps1
```

It runs the tests, publishes a self-contained single exe to `artifacts/tftp-spike`, and then asks
the exe itself for its firewall rule. `--firewall` prints that same elevated `netsh` line - built by
`FirewallCheck.BuildAddRuleCommand`, the same code the interface bar reads - and the line that
removes it again afterwards.

### Asking, rather than watching

`--send` makes the spike a client as well, which is how you exercise any of this without a robot:

```powershell
# Two windows. Neither needs UDP/69, so nothing is disturbed.
dotnet run --project spikes/Spike3.TftpWatch -- --port 6969
dotnet run --project spikes/Spike3.TftpWatch -- --send 127.0.0.1 --port 6969

# Against a real server: the first half of PLAN-TFTP.md part E4.
dotnet run --project spikes/Spike3.TftpWatch -- --send 192.0.2.10 --blksize 1468 --tsize 134217728

# From a laptop with a dozen adapters, leaving by the one on the robot network.
dotnet run --project spikes/Spike3.TftpWatch -- --send 192.0.2.10 --from 192.0.2.55 --log probe.txt
```

**`--from` is worth being explicit about.** A plant laptop carries VMware, Hyper-V, VirtualBox and a
docking NIC, and without it the route table alone decides which adapter the probe leaves by. A probe
that went out of a virtual switch and a probe that reached nothing look identical from here.

It sends **one** write request and no file data at all, then reports which address and port
answered. A different *port* is correct - that is TFTP moving the transfer, and the step a stateful
firewall breaks. A different *address* gets a warning, because a correct client discards that as an
unknown transfer id and the transfer stalls with no error anywhere. It also reports which options
survived, and explains any error in `TftpErrorText`'s words.

It aborts the transfer afterwards, but a server that accepted the request has generally created the
file already - so `--send` can leave an empty `netcontrol-probe.tmp` behind, and **`--file` pointed
at an existing name can truncate it.** It warns before sending when you override the default.

> **Watching takes UDP/69, which means the real TFTP server has to be stopped.** It refuses every
> transfer it sees, so left running it is a backup server that fails every backup. Start the real
> one again before you leave. `BENCH.md` Run 6b is the procedure.

The thing to bring back is three strings: the filename, the mode, and the options. They close three
open questions in `PLAN-TFTP.md` at once, and no manual and no server log will give you them.
