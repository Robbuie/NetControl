# Packaging and releasing NetControl

Same shape as Redline PDF, File Manager and File Compare: a public GitHub repository, a per-user
installer built by Inno Setup, and a release cut by pushing a tag.

## One-time setup

**1. Create the repository.** Public, named `netcontrol`, under `Robbuie`. Those two names are in
`src/NetControl.App/Diagnostics/BuildInfo.cs` (`RepositoryOwner` / `RepositoryName`) and nowhere
else - the update check, the Help menu and the workflows all derive their URLs from them, so moving
or renaming the repository is one edit rather than a search.

**2. Push.** The folder is `BootP.DHCP` locally and the repository is `netcontrol`; that is fine,
and `netcontrol` is the right name because the product outgrew BOOTP at the point it learned to
scan.

```powershell
cd "C:\Users\rjokr\Projects\BootP.DHCP"
git add .
git commit -m "NetControl"
git branch -M main
git remote add origin https://github.com/Robbuie/NetControl.git
git push -u origin main
```

**3. Build tools, only if you want to build the installer locally.** (Python with Pillow is only
needed to redraw the icon - `python tools/icon.py` - and its output is committed.) GitHub Actions needs none of
this.

- Inno Setup 6 - https://jrsoftware.org/isdl.php
- The .NET 10 SDK

## Cutting a release

**No .NET SDK is needed on this PC.** Same as the other three apps: GitHub builds and tests every
push, so the PC only needs git. (`dotnet test` here fails with "No .NET SDKs were found" - that is
expected, not a problem with the repository.)

```powershell
cd C:\Users\rjokr\Projects\BootP.DHCP

# 1. Bump the version (VersionPrefix in Directory.Build.props) and add a
#    "## 0.8.0 - <what it is>" section to the top of CHANGELOG.md.
# 2. Commit and push to main - no tag yet.
git add -A
git commit -m "0.8.0 - <what it is>"
git push

# 3. Wait for the "verify" run on that commit to go green:
#    https://github.com/Robbuie/NetControl/actions
#    If it is red, fix it in another commit and push again. Nothing has been released.
# 4. Then tag the green commit. This is what builds and publishes the release.
git tag v0.8.0
git push --tags
```

Tagging only after `verify` is green is the one difference from pushing everything at once: a tag
on a commit that does not build leaves a version number pointing at nothing, and fixing it means
moving a tag that has already been pushed.

`.github/workflows/release.yml` runs the suite, publishes the single self-contained exe, wraps it
with Inno Setup, and publishes a GitHub release with `gh` - the installer, the portable exe, a
SHA256 file beside each, and `version.json`. The release page's text is that version's section of
`CHANGELOG.md`. `.github/workflows/verify.yml` runs the build and the tests on every push to `main`.

**The build tool builds and `gh` publishes, never both.** Same rule as the other three apps, and
for the reason Redline PDF found out the hard way: a publisher left to decide what to upload raced
itself, the update metadata never arrived, and the run still exited green.

**It fails the build if the tag and `VersionPrefix` disagree.** That mismatch would otherwise ship
an installer whose filename, status bar and update check all claim different versions - and this
tool writes its version into commissioning records, so the disagreement would outlive the release
by years.

`1.0.0 is still reserved`: it is the build that commissions a real panel without the Rockwell tool,
which is Phase 1's own definition of done. Nothing before that has earned the number.

## Building locally instead

```powershell
pwsh tools/publish.ps1              # the portable exe only
pwsh tools/publish.ps1 -Installer   # and dist_installer\NetControl-Setup-<version>.exe
```

The script runs the tests first, passes the short commit as `SourceRevisionId` so the exe reports
`0.5.0+a1b2c3d` rather than `0.5.0`, and stamps `-dirty` onto a build from an uncommitted tree. Do
not hand anybody a dirty build.

## Two artefacts, on purpose

| | |
|---|---|
| `NetControl-Setup-<version>.exe` | Per-user install into `%LOCALAPPDATA%\Programs\NetControl`. Start menu entry, Add/Remove Programs entry, an exe at a path that stays put. |
| `NetControl.exe` | The same tool as one loose self-contained file. Copy it anywhere and run it. |

Neither needs administrator rights, a driver, or a .NET runtime installed first. The portable exe
is for the laptop somebody was handed that morning; the installer is for the one they use every
week - and for the firewall rule, which names an executable and therefore wants a path that is not
in `Downloads`.

**Both are load-bearing for updating, and so are their `.sha256` files.** The app picks the asset
matching what it is - the `.exe` whose name says `setup`, or the one called exactly
`NetControl.exe` - and refuses to download either without the checksum beside it. A release that
drops an artefact does not just remove a download option; it stops that kind of copy updating.

**Never change the `AppId` GUID in `installer/NetControl.iss`.** It is how Windows recognises an
existing install and upgrades it in place rather than leaving two NetControls in Add/Remove
Programs.

## How the update check works

- On startup the app asks `https://api.github.com/repos/Robbuie/NetControl/releases/latest` whether
  there is a newer tag. **It says nothing unless there is.** A failed check at startup is silent
  too, because on a plant segment with no route out it fails every launch and a warning that is
  always present is one nobody reads. The failure is in the diagnostic log.
- **Help > Check for updates** always answers, including "could not reach it" and "switched off on
  this machine" - somebody pressed it and is waiting.
- **Clicking that line installs it.** An installed copy downloads `NetControl-Setup-<version>.exe`
  and runs it with `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`, the same as the DWG viewer; a
  portable copy downloads `NetControl.exe`, renames itself to `NetControl.exe.superseded` and copies
  the new build into the name it vacated. Both come back on the new version.
- **Nothing unverified is run.** Every download is checked against the `.sha256` published beside it,
  and an asset with no published checksum is refused before a byte is fetched - which is why
  `release.yml` uploads the `.sha256` files and why removing them would silently disable updating
  rather than just losing a nicety.
- **It refuses at a bad moment**, and says why: while a device is being commissioned, and while the
  listener is running. Both are states where this process disappearing costs something that cannot
  be got back by relaunching.
- A site whose laptops cannot reach github.com mirrors builds onto an intranet share and points
  `updateManifestUrl` in `%LOCALAPPDATA%\NetControl\settings.json` at the `version.json` beside
  them. A site that wants no outbound request at all sets `"checkForUpdates": false`. See
  [DEPLOY.md](DEPLOY.md).

## Two things worth knowing

**The exe is unsigned.** Windows SmartScreen will warn the first few people who download it -
"Windows protected your PC" - until the download builds reputation, and on a locked-down plant
laptop it may be blocked outright rather than warned about. A code-signing certificate removes
that. Without one, tell people to click *More info -> Run anyway*, and expect to have a
conversation with plant IT about it at some sites.

**The repository is public, and the licence is not open source.** See [LICENSE](LICENSE): the code
is published to be read, audited and downloaded, not copied into other products. Nothing in it is
derived from the Rockwell tool or from Wireshark's GPL dissector, which is what makes publishing it
possible at all.
