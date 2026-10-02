# Part E - the image backup, end to end

A working document, in the shape of [PLAN-NEXT.md](PLAN-NEXT.md). It pulls one item out of
ROADMAP.md Phase 5 and argues - at the bottom, honestly - about whether it should be pulled at all.

**Status: E2 is done, E1's Core half with it, and E3's Observe mode - written, compiled, and green
at 571 tests.**
They were written in a session with no SDK, so the codec was ported to Python and run against 81
cases first, which is the technique that found the stray-quote bug in `CsvFile.Parse`. It found one
thing here too, and it was a claim in this document rather than a line of code: see *What the port
corrected* below. On first compile the whole solution built clean under warnings-as-errors and one
test failed - a hand-written assertion that had dropped the payload from an expected frame, not
ported logic.

`TftpEventRecorder` writes the `EventCategory.Tftp` rows the safety rules below call for, and
`spikes/Spike3.TftpWatch` is how any of it is run: **nothing in `NetControl.App` references
`NetControl.Core.Tftp` yet**, so the spike is the whole surface, and BENCH.md Run 6b uses it.

Still to do: E3's Accept mode and E4's probe. **E1's UDP/69 row, E3's watch and E5's verdict are now in the
app, on a TFTP backup tab - written without an SDK and not yet compiled; see "As built in the app" at the end.**

The problem it comes from is real and current: FANUC robots running PaintTool back their images up
over Ethernet to a TFTP server, and sometimes the backup does not start. Nobody can currently say
why, and "sometimes" is the part that hurts.

## The goal, stated plainly

When an image backup fails, the tool says **which step it failed at**, and what to do about it.

That is the whole feature. Not a better TFTP server - there are plenty and they are fine. The gap
is that a failed backup today produces one bit of information at the teach pendant, and the five
things that could have caused it need five different people to fix.

## What a FANUC image backup over Ethernet actually is

This is the part worth getting right before designing anything, because it is not what the name
suggests. It is **not** a file transfer. It is a boot-time network bring-up followed by a file
transfer, and the first half is the half this repository already owns.

The sequence, as far as it is understood from the field (see *Open questions* - none of this has
been watched on a wire by us):

| | Step | Protocol | Who owns it today |
|---|---|---|---|
| 1 | Controller restarted into boot monitor (F1+F5 held at power-up) | - | Nobody |
| 2 | Boot monitor brings up the Ethernet port with **no address** | - | Nobody |
| 3 | Controller broadcasts a BOOTP/DHCP request | UDP/67 | **NetControl, completely** |
| 4 | Server offers an address; controller accepts it | UDP/67, 68 | **NetControl, completely** |
| 5 | Controller sends a TFTP write request for the image | UDP/69 | Nothing here |
| 6 | Server answers **from a fresh ephemeral port**, blocks flow lockstep | UDP/ephemeral | Nothing here |

Two consequences fall straight out of that table and they are the reason this is worth building
*here* rather than as a separate utility.

**The common tool does both halves, and so it hides which half broke.** tftpd32/tftpd64 is what
almost everyone uses, and it serves the DHCP and the TFTP from one window. When a backup does not
start, its log shows either a BOOTP line or nothing, and distinguishing "the robot never asked",
"the robot asked and ignored the answer" and "the robot took the address and the TFTP request was
refused" is left to the reader.

**A documented, reproducible field failure is a BOOTP failure, not a TFTP one.** The pattern of a
controller repeating its BOOTP request while the server logs a proposed address each time - offer
sent, offer ignored, round and round - is a known one. Every cause of it is already written down in
[CLAUDE.md](CLAUDE.md#protocol-gotchas): a reply shorter than the 300-octet minimum, a reply
unicast to a device that has no address yet, `IP_UNICAST_IF` with the interface index the wrong way
round, or the answer leaving the wrong adapter. This repository knows more about that failure than
the tool the plant is currently using does.

So the honest framing of Part E is not "add TFTP to the BOOTP tool". It is: **the tool already does
step 3 and 4 better than what is on the laptop today; steps 5 and 6 are what stops it being able to
say which step failed.**

## Where it can fail, and what each failure looks like

The reported symptom is *never starts*, and *varies*. Those two together rule out surprisingly
little, which is the point - and they rule out the failure everyone reaches for first.

**The 32 MB thing is almost certainly not it, and it is worth saying so early.** Classic TFTP
numbers its blocks in 16 bits at 512 bytes each, so a transfer dies at 32 MiB unless both ends roll
the counter over - and implementations disagree on whether to roll to 0 or to 1, which corrupts
quietly rather than failing loudly. A FANUC image runs comfortably past that. But that failure
happens *partway*, twenty minutes in, and the symptom here is that it never starts. Keep it in the
catalogue, because it is the one that will bite next once starting is fixed, and design the probe
in E4 to answer it once and for all. Do not go looking there first.

What "never starts, intermittently" is actually consistent with:

| Fails at | Cause | How the tool tells |
|---|---|---|
| 3 | Request never leaves - wrong port on the switch, wrong VLAN, link down | Watch on every adapter sees nothing at all |
| 3 | Request lands on an adapter nobody is serving | Watch is unfiltered and already names the arrival adapter |
| 4 | Offer sent, controller ignores it - short reply, wrong send mode, wrong NIC | Request repeats with the same `xid`; already visible in the log |
| 4 | Two DHCP servers answer and the controller takes the other one | Two offers, one of them not ours |
| 5 | WRQ never arrives - firewall on the server, UDP/69 held by something else | E1 and E3 |
| 5 | WRQ arrives, server refuses it - path not writable, overwrite not allowed | E3 records the exact filename and the exact error code |
| 5 | Server is multi-homed and answers from an address the controller did not ask | E4 reports the source of the reply, not just that a reply came |
| 6 | Stateful firewall drops the ephemeral data port | E4: the OACK/ACK 0 never arrives though the WRQ was accepted |
| 6 | Block rollover, or a `blksize` nobody negotiated | E4's round trip, sized deliberately past 32 MiB |
| 6 | Block rollover *despite* a negotiated `blksize` - see the correction below | E4, sized past 92 MiB |

The last three are the ones no existing tool on that laptop will ever tell them, and 5 and 6 are
exactly why TFTP has a reputation for working on a bench and not in a plant: **the data transfer
does not happen on port 69.** The server answers from a new source port, and a firewall or NAT that
does not track TFTP drops it. The request is accepted and then nothing happens, which reads from
the pendant as "never started".

## What already exists (checked in the code, not assumed)

The reason Part E is small:

| | Exists | Takes a port? | Needed for UDP/69 |
|---|---|---|---|
| `PortConflictDetector.Inspect(int port)` | yes | **yes, already** | nothing |
| `FirewallCheck.Inspect(int port)` | yes | **yes, already** | nothing |
| `FirewallCheck.BuildAddRuleCommand(int, string?)` | yes | **yes, already** | nothing |
| `Preflight.InspectAsync(int port, ...)` | yes | **yes, already** | nothing |
| `InterfaceBarViewModel(..., int listenPort = 67)` | yes | **yes, already** | a second instance |
| `ReadinessCheck` / `ReadinessState`, grey-is-not-green | yes | labels built from the port | nothing |
| `TraceLog`, the diagnostic log | yes | n/a | nothing |
| `ProjectStore` event log, append-only | yes | n/a | one new `Category` |
| `RetransmitFilter`, `(xid, chaddr)` collapsing | yes | n/a | the same idea, different key |
| `tests/.../WireHarness.cs`, a real socket in a test | yes | n/a | nothing |

Every one of those was written for UDP/67 and none of them hardcodes it. That was not luck - the
port was threaded through deliberately - but it does mean **E1 is a constructor argument and a
second row in the bar**, not a piece of work. The engine for "why is nothing arriving on this port"
already exists and has never been pointed at a second port.

---

## Part E1 - grade UDP/69 with the code that already grades UDP/67  *(`TftpRootCheck` done; the bar row is to do)*

Add a second `InterfaceBarViewModel`, constructed with `listenPort: 69`, shown when the TFTP tab is
open. It answers, before anything is attempted:

- who owns UDP/69 on this machine, by PID and process name - which on the backup server is the
  answer to "is the TFTP server actually running?", and on a laptop is the answer to "can I bind
  it to watch?"
- whether an inbound UDP/69 firewall rule exists, and the exact `netsh` line to add one

Two things this must not do:

- **It must not report the two ports as one grade.** UDP/67 held by tftpd64 while NetControl wants
  to serve is a conflict; UDP/69 held by tftpd64 is the system working correctly. The same fact is
  good news on one row and bad on the other, and `ReadinessCheck.Worst` across both rows would
  average exactly the information somebody needs.
- **It must not offer to add a firewall rule for a server it is not running.** The existing rule
  builder names the executable. A rule for `NetControl.exe` on UDP/69 does nothing for tftpd64.

**One extra check that has no equivalent on UDP/67.** When the tool is running *on* the backup
server, the TFTP root folder is inspectable and three of the catalogue's causes live there: the
path does not exist, the account the server runs as cannot write to it, or the volume is full. A
`TftpRootCheck` that grades a folder the user points at is a handful of lines and covers the most
boring third of the failure space. It is a `ReadinessCheck` like any other and it goes in the same
bar.

## Part E2 - `NetControl.Core/Tftp` - the codec  *(done)*

RFC 1350 is five opcodes and no state to speak of; RFC 2347/2348/2349 add option negotiation and
the OACK. The whole codec is smaller than `BootpPacket` and should be built the same way:

- static encode/decode helpers over `ReadOnlySpan<byte>`, called *from* the async paths and never
  declared inside them - CS4013, which is already the first entry in the C# specifics list
- `BinaryPrimitives.Read/WriteUInt16BigEndian`, stated at the call site. TFTP is big-endian
  throughout, unlike everything in `Enip/` and `Cip/`; the two live in the same solution now, and
  the codebase's existing rule about converting deliberately earns its keep here
- round-trip tests against hand-built frames, in the shape of `tests/.../Frames.cs`
- the error table (codes 0-7) rendered as **actionable** text, per the conventions in CLAUDE.md.
  "Access violation" is the spec wording and it is useless. "The server refused the write. Its root
  folder is usually read-only by default, or the file already exists and overwrite is off" is the
  product.

Two decisions worth taking now rather than discovering later:

- **Transfer mode is a first-class field, not a detail.** A `netascii` transfer of a firmware image
  translates line endings and silently corrupts it. If a controller ever asks for an image in
  `netascii` the tool should say so loudly. Record the mode on every request and grade it.
- **Options are recorded as requested *and* as agreed.** `blksize`, `tsize`, `timeout`,
  `windowsize`. The gap between what the client asked for and what the server granted is a
  diagnostic in itself, and it is invisible in every server log we have seen.

## Part E3 - Watch: a TFTP server that records, and by default refuses  *(Observe done; Accept to do)*

The direct analogue of DHCP Watch mode, and the highest-value piece for the symptom described.

Bind UDP/69, log every arriving RRQ and WRQ with source address and port, filename **exactly as
sent**, transfer mode, and the options requested. Then, in the default sub-mode, answer with a TFTP
error and transfer nothing.

**Why refusing is the default and not an afterthought.** The question "did the controller ever ask
at all, and what did it ask for?" is answered completely by the first packet. Accepting the
transfer means becoming the backup server for twenty minutes and writing a 200 MB file somewhere,
which is a much larger thing to do to a plant and a much larger thing to get wrong. Watch answers
the question; Accept is a separate, deliberate act.

The filename is the payload here. A controller asking for a path the server does not have, or
asking with a leading slash or a drive prefix the server rejects, is a five-second fix that
currently costs an afternoon - and no server log shows the *requested* name when it refuses the
request.

**Accept mode** is the next piece: receive the file into a folder the user picks, with the full
transfer statistics from E4. Its purpose is not to replace the backup server; it is to prove the
network and the controller are fine and the server's configuration is not. It is declared and
refuses to start, rather than quietly behaving like Observe.

Hard rules, mirroring Serve mode:

- **Never binds UDP/69 without a deliberate arm action**, the same second action Serve mode needs.
- **Refuses to bind if another process owns UDP/69.** On the backup server that process is the
  backup server, and taking its port during a shift is a genuinely bad outcome. The refusal names
  the process, as `RefuseOnSeriousPortConflict` already does for 67.
- **Accept mode never writes outside the folder the user chose**, and never over an existing file
  without saying so.
- A request that arrives while only Watch is armed is answered with an error, never with silence.
  Silence makes the controller retransmit and makes the log unreadable - the same reasoning that
  put `RetransmitFilter` in `Dhcp/`.

## Part E4 - Probe: be the client, against the real server

Everything above watches. This one asks. It works from either vantage - on the server over
loopback, or from a laptop on the robot network, which is the vantage that matters because it is
the controller's.

A probe is: write a file of a chosen size, read it back, compare hashes. Report:

- whether the WRQ was accepted, and **what source port and source address the answer came from** -
  a multi-homed server answering from an address the client did not write to is discarded by a
  correct TFTP client as an unknown TID, and it looks exactly like silence
- the options requested versus the options granted
- time to first response, throughput, retransmit count, and the block number where it stalled if
  it stalled
- whether the server requires the target file to pre-exist, and whether it permits overwrite -
  two settings that differ per server product and are the classic cause of a refused write

**The default size crosses 32 MiB on purpose**, because that single run answers the rollover
question for this server and this client permanently, and answers it before it costs somebody a
night shift rather than after. A run that stops dead at block 65535 has told you everything.

Safety, and this is the part to be careful about because it is the only piece of Part E that
*writes* to plant infrastructure:

- **The probe writes to a filename the user typed.** Default it to something unmistakably a test.
  Never derive it from a real backup name, and never write into the directory the robots back up
  to unless the user explicitly points there and is told what will happen.
- **The probe never deletes anything.** It cannot clean up after itself, and that is correct: a
  tool that deletes files off a plant server to tidy up is one bug away from an incident. It
  reports the file it left and where.
- **One transfer at a time.** The backup path on the server is per-transfer and the field advice is
  already that robots go one at a time.

## Part E5 - the verdict

The panel that makes the rest worth building. One line per step of the six-step table at the top,
each an existing `ReadinessCheck`, each grey until something has actually been observed - because
`ReadinessState.Unknown` sorting worse than `Ready` is the one rule in the bar that already earns
its own unit test, and "we never saw a TFTP request" must never render as "TFTP is fine".

Below it, one sentence: the earliest step that is not green, and its remediation. That sentence is
the deliverable of this entire plan.

## Safety - additions to the hard rules

To be folded into [CLAUDE.md](CLAUDE.md#safety---this-touches-live-plant-networks) when this is
built, in the same register as the ones there:

- **Never bind UDP/69 while another process owns it.** On a backup server that process is the
  backup server.
- **Never write a file to a TFTP server the user did not name**, and never into the live backup
  directory by default. TFTP has no authentication at all; the only thing standing between this
  tool and somebody's image archive is this rule.
- **Never delete anything over TFTP.** The probe leaves its test file and says where.
- **A watch answers; it does not go quiet.** Silence provokes retransmits from a controller that is
  already having a bad day.
- **Every accepted transfer, and every refusal, gets an event row** - done, in `TftpEventRecorder` - a new `tftp` value alongside
  `dhcp | cip | scan | app`. Adding it is safe for project files already in the field: readers map
  a category they have never heard of to `EventCategory.Other` rather than refusing the file, which
  is exactly the case that member was written for and this is the first time it is used.

## What E3 settled that this document did not

Five decisions came out of building the watch, and they are here rather than only in the code
because each one is a judgement rather than a mechanism:

- **Silence is not an option, and refusing is a transmission.** Those two pull against each other.
  The resolution is that a request on the watched adapter is refused, and a request on an adapter
  outside the filter is recorded and left alone - because refusing there would mean transmitting
  onto a segment nobody selected, which is a rule this repository already holds everywhere else.
- **The port-conflict wording had to be inverted.** On UDP/67, another process holding the port is
  an intruder. On UDP/69 it is the backup server doing its job, and the refusal has to read that
  way round or it is wrong about the most common case in the field.
- **Accept is declared and refuses to start.** Reporting that backups are being accepted while
  refusing every one of them would be the worst failure this tool could have.
- **The retransmit key is the source endpoint plus the filename**, since TFTP has neither a
  transaction id nor a MAC. A controller that gave up and started again comes back from a different
  port, and that is a second attempt rather than a repeat of the first.
- **A mid-transfer packet on port 69 is a finding, not noise.** A transfer moves off the well-known
  port after its first packet, so traffic still arriving there means one end is addressing the wrong
  port - which is what a NAT or firewall mangling the ephemeral data port looks like from this side,
  and one of the very few ways to see it without a capture.

## What the port corrected

The first draft of this document said that negotiating `blksize` was **the fix** for block-counter
rollover. It is not, and the arithmetic says so plainly once it is actually run:

| Block size | Largest transfer without wrapping |
|---|---|
| 512 (RFC 1350 default) | 33,553,920 bytes - just under 32 MiB |
| 1468 (largest that still fits an Ethernet frame) | 96,205,380 bytes - about 91.7 MiB |

A FANUC image runs from a few megabytes to a few hundred. So an image at the large end wraps the
block counter **even at the biggest block size that does not fragment**, and the two ends still
have to agree about whether to wrap to 0 or to 1. Escaping rollover outright for a 128 MiB image
needs `blksize=2049`, and for 256 MiB `blksize=4097` - both above the Ethernet limit, both
fragmenting, which trades one intermittent failure for another.

What that changes: **"negotiate blksize" is good advice for an image under about 90 MB and wrong
advice above it.** `TftpLimits.SmallestBlockSizeWithoutRollover` exists so the tool says which of
the two situations somebody is in rather than repeating the folklore, and
`TftpLimitsTests.NegotiatingTheLargestUnfragmentedBlockDoesNotSaveALargeImage` is there so the
correction cannot quietly regress into the comfortable version.

This does not change the ordering advice. Rollover still kills a transfer twenty minutes in, and
the symptom here is one that never starts.

## Sequencing

| | | Leaves you with |
|---|---|---|
| E1 | UDP/69 in the bar, plus the root-folder check | Is the server running, reachable and writable - **in the app, on the TFTP tab's own row** |
| E2 | The codec | Nothing on its own - **done, compiled, green** |
| E3 | Watch, refusing | **Did the controller ask, and for what** - the answer to the stated symptom. **Observe done, compiled, green** |
| E4 | Probe | The rollover question answered, and the ephemeral-port question answered |
| E5 | The verdict panel | One sentence instead of six tabs - **in the app: `BackupVerdict`** |

**E1 before E2.** It is the only part that needs no new code in `Core` at all, and it covers the
dullest and most common third of the failure space. If the TFTP root turns out to be full or
read-only, the rest of this document was not needed.

**E3 before E4.** Watching answers the question that was actually asked; probing answers the
question that will be asked next.

## What the first spike run established

Run on the development laptop, `--list --root "C:\TFTP-Root"`, before a robot was involved.

**The headline is a correction, not a finding.** The run named *SolarWinds TFTP Server* (pid 17152)
holding UDP/69 with a wildcard bind - and that turned out to be an instance left running from
testing months earlier, which nobody remembered was there. **It is not the plant's backup server**,
so it settles nothing about what the robots talk to. The question of which server they use is still
open, and is now more interesting rather than less.

What the run does establish:

| | |
|---|---|
| A TFTP server nobody remembered was holding UDP/69, on a wildcard bind | The interface-bar thesis, working on the first real machine it met |
| `C:\TFTP-Root` is **writable, 21,537 MB free** | On this laptop. Says nothing about the real server |
| **No inbound firewall rule** for UDP/69 for the spike's exe | Needs the rule, or the Windows prompt, before Run 6b |
| The machine carries 13 adapters; only VMware, Hyper-V, VirtualBox and `Ethernet 5` (172.22.4.102) are up | The physical `Ethernet` is down on an APIPA address |
| The bind refusal fired, and worded the conflict the right way round | Behaved as designed against a real port owner |

Three things follow, and none of them is settled:

- **Confirm nothing depended on that stray server before it was stopped.** It was a wildcard bind,
  so anything sending TFTP to any address on that laptop reached it. If a robot was ever pointed at
  this machine, stopping it changed behaviour - and if a backup has been quietly landing in
  `C:\TFTP-Root` rather than where somebody expected, that is worth knowing before the next one.
- **The write probe ran as the signed-in user, not as a service.** "Writable by me" is not "writable
  by the account the real TFTP server runs as". `TftpRootCheck`'s own remediation says so.
- **What answers the robot's BOOTP request?** Unchanged and unanswered. The image backup needs an
  address before it needs a file, and the tftpd32/tftpd64 story - one tool serving both halves - is
  not what is running here. BENCH.md Run 6a watches UDP/67 during a normal backup, transmits
  nothing, and answers it.

## Open questions

- **Which TFTP server do the robots actually back up to, and on which machine?** The first spike
  run looked like it answered this and did not: the SolarWinds instance it found was a forgotten
  test install on a development laptop. Whatever the plant uses, its file-size and block-size
  behaviour is worth checking, since a robot image is large.
- **Which controller and which software version?** R-30iA, R-30iB and R-30iB Plus differ in the
  boot monitor and in what the Ethernet bring-up will negotiate. The catalogue above holds for all
  three in outline and in none of them in detail.
- **What is actually serving DHCP for these backups?** Sharpened rather than answered by the first
  spike run: it is *not* tftpd64 doing both halves, because the TFTP server here is SolarWinds. So
  either a plant DHCP server is answering the controller or something else on that laptop is, and
  a controller taking an address from a scope nobody expected is a different problem with the same
  symptom. Watching UDP/67 during a normal backup - BENCH.md Run 6a, which transmits nothing -
  answers it without disturbing anything.
- **Does the FANUC boot-monitor TFTP client negotiate `blksize` at all, and if it rolls the block
  counter over, does it roll to 0 or to 1?** Nobody knows. It cannot be answered from a
  specification, only by watching one, and E3 answers it in the first thirty seconds of a real
  backup. This is the single most useful unknown in the document.
- **Is the failure even reaching TFTP?** Stated plainly because it is the likeliest outcome of the
  first bench session: given "never starts" and a known field failure mode where the controller
  ignores a BOOTP offer, there is a real chance this is a BOOTP problem and Part E is not what
  fixes it. **That would be a good outcome**, and the tool is already built for it.

## The scope-drift argument, honestly

ROADMAP.md names scope drift as the highest risk in the project, and it names it in these words:
network scanning is more fun to build than a reliable BOOTP server. TFTP is Phase 5 material -
adjacent to item 5 on that list - and this document pulls it forward past a Phase 1 that **has
still never been in front of a real device.** BENCH.md is still worth more than any code in this
plan. That has not changed and this document does not change it.

The case for doing it anyway, and it is a real one:

- **It is not a second tool, it is the second half of the one already built.** A FANUC image backup
  is BOOTP and *then* TFTP, and the two halves fail identically from the teach pendant. A tool that
  owns step 3 and 4 and cannot see step 5 can never finish the sentence it starts.
- **E1 costs almost nothing** because the readiness stack was already written to take a port.
- **It supplies the bench session rather than competing with it.** A controller doing an image
  backup emits a real FANUC BOOTP request and then a real TFTP write request, from real hardware,
  on demand, without a panel to commission or a customer to inconvenience. That one session
  answers the send-mode question open since Phase 0, produces the real captured frame that
  `Frames.cs` currently only imitates, and - if E3 exists by then - the TFTP question too.

The sequencing that follows from that: **do the bench session first, with Watch mode and a robot
doing an image backup as the test article.** Build E1 before it, since it is a constructor argument
and it might make the session unnecessary. Build E2 and E3 only if the session shows the BOOTP half
working and the TFTP half not.

Which is the same rule the rest of this repository already follows: find out what is actually
happening before writing the thing that assumes.

## As built in the app

E1, E3 (Observe) and E5 went into `NetControl.App` as a **TFTP backup** tab. Four things were
decided while wiring them that this document did not settle:

- **UDP/69 got its own row on the tab, not a second row in the interface bar.** The bar's overall
  grade is a maximum across its checks, and the point made under E1 - the same fact is good on one
  port and bad on the other - means the two must never feed one grade.
- **The vantage is a tick, not a guess.** "This PC is the backup server" decides whether nothing on
  UDP/69 is the fault (it is, on the server) or the precondition for watching (it is, on a laptop).
  Nothing on the machine can tell the two apart reliably, and a guess wrong in either direction
  produces confident advice about the wrong problem.
- **E5 has four steps, not six.** Steps 1 and 2 of the table at the top (boot monitor, port up with
  no address) are invisible on the wire until step 3 happens, so they fold into "asked for an
  address". Steps 5 and 6 are "asked for the file" and "sent the file", and the last is never seen
  while only Observe exists - so the backup as a whole is never graded green, by construction.
- **The verdict follows one device.** A segment can have several things asking for addresses; the
  sentence is about a FANUC when one is asking (by OUI), otherwise the latest asker, and a device
  NetControl has just served takes precedence over both.
