# Getting a build onto a laptop, and keeping it current

> **Open decision: the licence.** There is no `LICENSE` file, deliberately - picking one is not a
> technical choice. If this stays an internal tool, a short "Copyright <year> <owner>. All rights
> reserved." file is the honest default; if it is ever handed to a customer or published, that is a
> conversation to have first. Until it is decided, nothing in here assumes either way.

## What ships

One file: `NetControl.exe`. Self-contained, single-file, `win-x64`. No installer, no admin rights,
no driver, no runtime to install first. That is a hard requirement rather than a preference -
anything else is a conversation with plant IT before anybody can do any work, and the tool exists
to be usable on a laptop somebody was handed that morning.

```powershell
pwsh tools/publish.ps1
```

That produces `artifacts/NetControl-<version>/` containing:

| | |
|---|---|
| `NetControl.exe` | The tool. |
| `NetControl.exe.sha256` | So a copy that arrived by email or USB stick can be checked. |
| `version.json` | The update manifest for this build - see below. |

The version comes from `VersionPrefix` in `Directory.Build.props`, and the script passes the short
commit hash as `SourceRevisionId`, so the exe reports something like `0.5.0+a1b2c3d`. **That suffix
is the point.** "0.5.0" is a version; "the 0.5.0 that was on the laptop that Tuesday" is what you
need when a commissioning record from six months ago has to be interpreted.

The same string goes three places, on purpose:

- the status bar, so the tool in your hand can be identified without opening anything;
- the first line of every diagnostic log;
- an event row in every project file the build opens.

A build from a dirty working tree is stamped `+<hash>-dirty`. Do not send one to anybody.

## Where the version came from

`0.x` while nothing has been done to real hardware. **1.0.0 is reserved for the build that
commissions a real panel without the Rockwell tool**, which is Phase 1's own definition of done.
Bump `VersionPrefix` in `Directory.Build.props` and nowhere else; every project reads it.

## Getting it to people

Copy the folder. That is the whole mechanism, and it is deliberately the whole mechanism: a plant
laptop is frequently on a segment with no route out, and often on one where an unexplained
outbound connection is a reportable incident.

## The update check

Off by default. **A machine with no settings file makes no network connection of any kind.**

To turn it on, create `%LOCALAPPDATA%\NetControl\settings.json`:

```json
{
  "updateManifestUrl": "https://intranet.example/tools/netcontrol/version.json"
}
```

Any URL that returns the manifest works - an intranet page, a raw file in a repository, a
`file://` path on a share. The manifest is deliberately trivial, and `publish.ps1` writes one:

```json
{
  "version": "0.6.0",
  "url": "https://intranet.example/tools/netcontrol/",
  "notes": "Adds the CLI"
}
```

At startup the tool does one GET with a five-second timeout and then says, in the status bar,
either nothing at all or `Version 0.6.0 is available - <url>`.

It never downloads and never installs. A tool that fetches and runs an executable is a tool plant
IT is right to block, and the person who has to approve putting a new build on a plant laptop is
not the tool.

Both quiet states are silent on purpose - no manifest configured, and already on the newest build.
A status bar that always has something in it is a status bar nobody reads. A check that was asked
for and *failed* does say so, because otherwise a site turns this on and believes it is current for
a year.

## Where the tool keeps its own things

`%LOCALAPPDATA%\NetControl\`, never beside the executable - the exe gets copied into Downloads,
onto USB sticks, and occasionally into folders the user cannot write to.

| | |
|---|---|
| `logs\netcontrol-<date>.log` | The diagnostic log: what the *tool* did. Rolls daily and at 4 MB, keeps ten files, and can be opened while the tool is running. |
| `settings.json` | Optional, absent by default. Currently only the update manifest URL. |

Project files are not here. They are wherever the user saved them, which is what Save As is for.

**The diagnostic log is not the commissioning record.** The record is the append-only `Event` table
inside the project file, it is the account of what was done to somebody's equipment, and it is the
one somebody may have to stand behind. The diagnostic log is a rolling text file for working out
why the application misbehaved, and it is deleted on a schedule.

When the tool hits an error it did not expect, the dialog names the log file. That is what turns
"it crashed" into something that can be sent to somebody.
