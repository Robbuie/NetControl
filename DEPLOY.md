# Getting a build onto a laptop, and keeping it current

## What ships

Every release publishes the tool twice, and both are the same build:

| | |
|---|---|
| `NetControl-Setup-<version>.exe` | Per-user installer. Start menu entry, Add/Remove Programs entry, an exe at a path that stays put. |
| `NetControl.exe` | The same tool as one loose self-contained file. Copy it anywhere and run it. |
| `*.sha256` | So a copy that arrived by email or on a USB stick can be checked against the one that was built. |
| `version.json` | The update manifest, for a site that mirrors builds onto its own intranet - see below. |

**Neither one needs administrator rights, a driver, or a .NET runtime installed first.** That is a
hard requirement rather than a preference: anything else is a conversation with plant IT before
anybody can do any work, and the tool exists to be usable on a laptop somebody was handed that
morning.

The installer is per-user - `%LOCALAPPDATA%\Programs\NetControl` - which is what keeps it out of
UAC's way. It writes nothing outside the user's own profile.

### Which one to hand somebody

The **loose exe** for a laptop somebody has for the afternoon, or for a machine where nothing may be
installed. Copy it and run it; that is the whole procedure, and it has not changed.

The **installer** for a machine somebody uses every week. It buys three things the loose file cannot:
a Start menu entry, an uninstall, and an executable at a path that stays put - which matters more
than it sounds, because a Windows Firewall rule names an executable and
`C:\Users\...\Downloads\NetControl (3).exe` is a poor thing to point one at. The app's interface bar
prints the exact `netsh` line; with an installed copy it is worth actually creating.

## Where releases come from

<https://github.com/Robbuie/netcontrol/releases>

Cutting one is a tag push. See [RELEASING.md](RELEASING.md).

Building it yourself:

```powershell
pwsh tools/publish.ps1              # -> artifacts/NetControl-<version>/NetControl.exe
pwsh tools/publish.ps1 -Installer   # -> dist_installer/NetControl-Setup-<version>.exe as well
```

The version comes from `VersionPrefix` in `Directory.Build.props`, and the script passes the short
commit hash as `SourceRevisionId`, so the exe reports something like `0.5.0+a1b2c3d`. **That suffix
is the point.** "0.5.0" is a version; "the 0.5.0 that was on the laptop that Tuesday" is what you
need when a commissioning record from six months ago has to be interpreted.

The same string goes four places, on purpose:

- the status bar, so the tool in your hand can be identified without opening anything;
- **Help > About**, for a screenshot;
- the first line of every diagnostic log;
- an event row in every project file the build opens.

A build from a dirty working tree is stamped `+<hash>-dirty`. Do not send one to anybody.

## Where the version came from

`0.x` while nothing has been done to real hardware. **1.0.0 is reserved for the build that
commissions a real panel without the Rockwell tool**, which is Phase 1's own definition of done.
Bump `VersionPrefix` in `Directory.Build.props` and nowhere else; every project reads it, and the
release workflow refuses to build a tag that disagrees with it.

## Updating

On startup the app asks the repository whether a newer release has been tagged. One GET, five-second
timeout. What it says:

| | |
|---|---|
| A newer release exists | One line in the status bar, naming the version. **Click it to install.** |
| You are on the newest | Nothing. |
| The check failed | Nothing **at startup** - see below. A line in the diagnostic log. |
| The check is switched off | Nothing. |

**Why a failed check is silent at startup.** On a segment with no route out - which is most of them
- it fails at every single launch, and a warning that is always present is one people learn to skip
past, which is the same argument that keeps the two quiet states quiet. **Help > Check for updates**
is the other half of that decision: it always answers, including "could not reach it" and "switched
off on this machine", because somebody pressed it and is standing there waiting.

### What "install" does

Clicking the status-bar line, or **Help > Check for updates** when there is something newer, opens a
dialog that says in one sentence what is about to happen to this copy, and then does it.

**An installed copy** downloads `NetControl-Setup-<version>.exe`, checks it, and runs it with
`/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`. No wizard and no
administrator prompt - the install is per-user. Setup closes NetControl **through the Restart
Manager**, which asks a window to close the ordinary way before it terminates anything, so the
listening socket and the project file are released by the normal shutdown path. Then it starts the
new build.

**A portable copy** downloads `NetControl.exe`, checks it, renames the running executable to
`NetControl.exe.superseded`, copies the new one into the name it just vacated, and starts it.
Windows allows a running image to be renamed but not overwritten, which is the whole trick. If the
copy fails - a read-only folder, a USB stick pulled out - the rename is undone and nothing has
changed. The superseded file stays until the next start, so a build that turns out to be worse can
be put back by renaming it.

### It will not update at a bad moment

Two states refuse, and the dialog says which and why rather than greying a button out:

- **A device is being commissioned.** An update ends with this process gone, and the middle of a
  write to live equipment is not where the tool's own record of what was sent should stop.
- **The listener is running.** Anything that asks for an address while NetControl is closed is a
  request nobody sees, and on a real segment it may not come again for hours.

### Nothing unverified is ever run

**Every download is checked against the `.sha256` published beside it, and an asset with no
published checksum is refused before a single byte is fetched.** The release workflow publishes one
for both artefacts, so an asset without one is either a hand-made release or not the release it
claims to be.

This is not a defence against somebody who controls the release page - they would publish a matching
checksum. It is a defence against what actually happens: a truncated transfer, a captive portal that
answered 200 with a login page, a laptop that slept mid-download. Every one of those produces an
executable that would otherwise be run. A file that fails verification is deleted rather than left
in a folder for somebody to find later and assume is fine.

Downloads land in `%LOCALAPPDATA%\NetControl\updates` and are swept at the next start.

### Turning it off

`%LOCALAPPDATA%\NetControl\settings.json`:

```json
{
  "checkForUpdates": false
}
```

That is the one setting that stops the tool making any outbound request at all, and it is asserted
by a test that counts calls on the HTTP handler rather than by what the tool reports. With it off
there is no check and therefore nothing to install: updating is copying a new build over by hand,
exactly as it was before. **The
uninstaller does not remove this file**, deliberately: it is site configuration, and an uninstall
that quietly discarded it would turn a reinstall into a machine phoning out again without anybody
choosing it.

### Pointing it somewhere else

For a site that mirrors builds onto an intranet share because its laptops cannot reach github.com:

```json
{
  "updateManifestUrl": "https://intranet.example/tools/netcontrol/version.json"
}
```

Any URL returning the manifest works - an intranet page, a raw file in a repository, a `file://`
path on a share. `publish.ps1` writes one beside every build:

```json
{
  "version": "0.6.0",
  "url": "https://intranet.example/tools/netcontrol/",
  "notes": "Adds the CLI"
}
```

Setting this does not turn the check on or off; `checkForUpdates` does that.

## Where the tool keeps its own things

`%LOCALAPPDATA%\NetControl\`, never beside the executable - the exe gets copied into Downloads, onto
USB sticks, and occasionally into folders the user cannot write to.

| | |
|---|---|
| `logs\netcontrol-<date>.log` | The diagnostic log: what the *tool* did. Rolls daily and at 4 MB, keeps ten files, and can be opened while the tool is running. **Help > Open the diagnostic log folder.** |
| `settings.json` | Optional, absent by default. The update check and nothing else, so far. |
| `updates\` | A downloaded build, briefly. Swept at the next start. |

Project files are not here. They are wherever the user saved them, which is what Save As is for.

**The diagnostic log is not the commissioning record.** The record is the append-only `Event` table
inside the project file, it is the account of what was done to somebody's equipment, and it is the
one somebody may have to stand behind. The diagnostic log is a rolling text file for working out why
the application misbehaved, and it is deleted on a schedule - by the uninstaller too.

When the tool hits an error it did not expect, the dialog names the log file. That is what turns
"it crashed" into something that can be sent to somebody.

## Signing

**The exe is unsigned.** Windows SmartScreen will warn the first people who download it, and on a
locked-down plant laptop it may be blocked outright rather than warned about. A code-signing
certificate is the only real fix. Without one: *More info -> Run anyway*, and expect to have a
conversation with plant IT at some sites.

## Licence

[LICENSE](LICENSE): copyright reserved, published to be read and downloaded rather than copied into
other products. Nothing here is derived from decompiling the Rockwell tool or from Wireshark's GPL
dissector, which is what makes publishing it possible at all.
