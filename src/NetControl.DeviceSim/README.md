# NetControl.DeviceSim

A fake EtherNet/IP adapter, so both spikes — and later the real tool — can be developed and
regression-tested at a desk with no hardware.

```powershell
dotnet run --project src/NetControl.DeviceSim -- --help
dotnet run --project src/NetControl.DeviceSim -- --list-quirks
```

## What it serves

- **TCP 44818** — CIP requests: Identity (`0x01`), TCP/IP Interface (`0xF5`), Ethernet Link (`0xF6`).
  Get/Set on the attributes that matter, including the real refusal behaviours.
- **UDP 44818** — `ListIdentity` discovery.
- **UDP 67 (client side, optional)** — with `--bootp` or `--dhcp` it behaves like an adapter that
  just powered up: broadcasts requests until something answers, then adopts the address it was given.

## Three sessions worth running

**1. CIP only — no special setup**

```powershell
# terminal 1
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1

# terminal 2
dotnet run --project spikes/Spike2.CipStaticIp -- discover
dotnet run --project spikes/Spike2.CipStaticIp -- read 127.0.0.1
dotnet run --project spikes/Spike2.CipStaticIp -- set 127.0.0.1 --mask 255.255.0.0
```

The `set` should walk attr 3 → attr 5 → readback and finish with **VERIFIED**.

**Why `--ip 127.0.0.1` and not the default?** The simulator listens on every local address,
but `--ip` is only the address it *reports* over CIP. On real hardware those are the same
thing; locally they are not. Leave it at the default `192.168.1.51` and `read` will time out,
because nothing on the machine owns that address. The simulator warns about this at startup.

**Why `--mask` rather than `--ip` for the change?** Changing the reported IP means verification
reconnects to an address that also does not exist locally, and you get UNVERIFIED instead of a
clean result. Changing the mask keeps the device reachable while still exercising the full
write-then-readback path. Against real hardware, change whatever you like.

**2. The BOOTP round trip**

```powershell
# terminal 1
dotnet run --project spikes/Spike1.BootpListen -- --serve 00:00:BC:5E:11:01=192.168.1.77 --reply-port 6868

# terminal 2
dotnet run --project src/NetControl.DeviceSim -- --bootp --client-port 6868
```

`--client-port 6868` / `--reply-port 6868` exist only for this: the Windows DHCP Client service
owns UDP/68, so a same-machine test needs a different client port. Against real hardware both stay
at their defaults. Swap `--bootp` for `--dhcp` to exercise the DORA path instead.

**3. Quirks — the reason this exists**

Terminal 1 runs the simulator, terminal 2 the matching `cip-spike` command.

```powershell
# "write succeeded" but nothing changed. set must report MISMATCH, not success.
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk LiesAboutWriteSuccess
dotnet run --project spikes/Spike2.CipStaticIp -- set 127.0.0.1 --mask 255.255.0.0

# attr 5 refused until attr 3 is Static. Proves the ordering is actually necessary.
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk RejectConfigWhileDynamic --method bootp
dotnet run --project spikes/Spike2.CipStaticIp -- set 127.0.0.1 --mask 255.255.0.0

# address pinned by rotary switches. read should say so before set is ever attempted.
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk HardwarePinnedAddress
dotnet run --project spikes/Spike2.CipStaticIp -- read 127.0.0.1

# config held until an Identity reset.
dotnet run --project src/NetControl.DeviceSim -- --ip 127.0.0.1 --quirk RequiresResetToApply
dotnet run --project spikes/Spike2.CipStaticIp -- set 127.0.0.1 --mask 255.255.0.0 --reset
```

`LiesAboutWriteSuccess` is the important one. It is the exact failure the Rockwell tool does not
catch, and if `cip-spike set` ever reports VERIFIED against it, the verification logic is broken.

Quirks combine: `--quirk SlowResponses --quirk RequiresResetToApply`.

## Running several at once

TCP 44818 binds once per address. To simulate a rack, add extra IPs to a NIC and give each
instance its own:

```powershell
netsh interface ipv4 add address "Ethernet" 192.168.1.51 255.255.255.0
netsh interface ipv4 add address "Ethernet" 192.168.1.52 255.255.255.0

dotnet run --project src/NetControl.DeviceSim -- --bind 192.168.1.51 --ip 192.168.1.51 --mac 00:00:BC:5E:11:01
dotnet run --project src/NetControl.DeviceSim -- --bind 192.168.1.52 --ip 192.168.1.52 --mac 00:00:BC:5E:11:02
```

## Caveats

- **Not compiled yet** — written without an SDK available. Expect small build fixes.
- Whether Windows loops a local `255.255.255.255` broadcast back to a local listener is worth
  confirming early. If session 2 shows nothing arriving, run the simulator in a VM or on a second
  machine on the same subnet — that is the more faithful test anyway.
- The simulator does not implement connected messaging (Forward_Open), only UCMM. Nothing in
  Phases 1–3 needs it.
- Identity `Get_Attribute_All` omits the trailing state byte some devices include. Harmless here;
  worth remembering if a parser is ever written against the simulator alone rather than real captures.
