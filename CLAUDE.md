# CLAUDE.md

Guidance for working in this repository.

## What this is

A Windows network tool for controls engineers. It replaces the Rockwell BOOTP/DHCP tool with
something reliable, then grows into a broader network toolkit (scanning, discovery, diagnostics).

It talks to live industrial equipment. Read the Safety section before writing anything that
sends packets.

## Stack

- .NET 10 (LTS), C# 13, `net10.0` - Windows only. `NetControl.App` and `NetControl.Tests` target
  `net10.0-windows` because WPF requires it, and a `net10.0` project cannot reference a
  `net10.0-windows` one. Everything else stays on plain `net10.0`.
- WPF + MVVM via CommunityToolkit.Mvvm for the GUI. Source generator plus a small runtime library:
  no container, no navigation model, no conventions - which is why it is easy to justify.
- SQLite via Microsoft.Data.Sqlite for project files
- xUnit for tests
- Microsoft.Data.Sqlite (not `.Core`) - the bundled build ships the native `e_sqlite3`, which is
  what keeps `dotnet publish` a single self-contained exe. Only third-party dependency in the engine.
  `SQLitePCLRaw.bundle_e_sqlite3` is pinned to 2.1.12 alongside it: the provider still resolves
  2.1.11 transitively, whose SQLite predates 3.50.2 and fails NuGetAudit on CVE-2025-6965. Drop the
  pin when the provider resolves 2.1.12 or later by itself.
- No npcap dependency in Phases 1-3; plain UDP/TCP sockets only. Adding a driver dependency
  makes deployment on locked-down plant laptops much harder, so it stays out until PROFINET
  DCP genuinely requires it.

Keep dependencies few. Every package added is a package someone has to justify to plant IT.

## Layout

```
NetControl.sln                solution - spikes are deliberately NOT in it; tools/ is
.github/workflows/            verify.yml on every push; release.yml builds and publishes on a v* tag
CHANGELOG.md                  one section per version - release.yml copies it onto the release page
assets/icon.png               the family mark, for the README; drawn by tools/icon.py
installer/NetControl.iss      Inno Setup, per-user, no admin - see RELEASING.md
src/NetControl.Core/          network engine - MUST NOT reference any UI assembly
    Ipv4Subnet.cs             checked address + mask: prefix, network, contains
    Interfaces/               NIC inventory, UDP port ownership, firewall state
    Dhcp/                     BOOTP/DHCP codec, server engine, assignment policy
    Persistence/              SQLite project file: plan, assignments, append-only event log
                              (DhcpEventRecorder and TftpEventRecorder live here, not in the engines)
    Enip/                     EtherNet/IP encapsulation: one session, one device
    Cip/                      CIP objects - 0xF5 is where the address lives
    Commissioning/            set static / disable BOOTP, with the readback
    Discovery/                the scan + ARP join, and the plan-against-segment comparison
    Diagnostics/              the rolling diagnostic log - NOT the commissioning record
    Tftp/                     RFC 1350 codec + the server root check - the image-backup half
    Oui/                      packed IEEE vendor table + the embedded oui.bin
src/NetControl.App/           WPF front end - net10.0-windows; see its README
    Appearance/               the shared design system: tokens, styles, the appearance store
    Composition/              object graph, and the one abstraction over the WPF dispatcher
    Diagnostics/              readiness grading, the build stamp, settings, and the updater -
                              check, download-with-checksum, and the two ways of applying one
    Serving/                  listener lifetime, and "is this MAC in the plan?"
    ViewModels/               all the UI logic, deliberately free of WPF types
    Views/                    XAML, two value converters, one file dialog
src/NetControl.Cli/           headless commissioning               (not started)
src/NetControl.DeviceSim/     fake EtherNet/IP device for hardware-free development
tests/NetControl.Tests/       xUnit
tools/NetControl.OuiPacker/   refreshes src/NetControl.Core/Oui/oui.bin; not shipped
tools/publish.ps1             the exe, and with -Installer the setup exe too
tools/icon.py                 redraws the icon (src/NetControl.App/Assets/NetControl.ico) - a chore
spikes/                       not part of the solution build
    Spike1/Spike2             Phase 0 throwaways, self-contained
    Spike3.TftpWatch          a door into Core's TFTP watch, until the app has a tab
_trash/                       things the agent sandbox could not delete - safe to remove
```

### The appearance system

`src/NetControl.App/Appearance/` is a port of the design system shared with the DWG viewer
(`~/Projects/DWG Viewer/src/theme.py`) and the Redline PDF app (`src/css/app.css`), so the three
read as one product. Three independent axes - **theme** (5), **accent** (6), **density** (3) - and
90 combinations that need no per-combination code.

- `Theme.cs` holds the catalogs, **copied value for value from `theme.py`**: same token names, same
  hex, same labels and notes, so a change in one app can be diffed straight against the others.
  Three tokens exist here that `theme.py` does not have and are marked where they are defined -
  `bad`, the `row-*` tints, and `grid-row-h` - because this app paints a table of device states
  rather than a drawing, and needs a red that is not the accent. **They are worth back-porting.**
- `ThemeResources.cs` is the only toolkit-specific half: it turns a token set into a
  `ResourceDictionary` and swaps it in. Qt substitutes `var(--bg-1)` into a text stylesheet; WPF
  resolves `{DynamicResource bg-1}`. The tokens are the contract, the delivery is per-toolkit.
- `Controls.xaml` is every control template, written **entirely** in `{DynamicResource}`.
  `{StaticResource}` for a colour would resolve once at load and leave open windows on the old
  theme, which is what the live preview in the appearance dialog depends on not happening.
- `AppearanceStore.cs` writes `%LOCALAPPDATA%\NetControl\appearance.json` - its own file, not a
  section of `settings.json`, because that one is site configuration the app must never rewrite.
- `ThemeTests.cs` is the guard: no literal colour in any XAML file (only `#ffffff`, the text on the
  accent fill), every `{DynamicResource}` names a token that exists, every accent tint moves when
  the accent does, and the normalisers never return junk.

Three traps, all already paid for:

- **The implicit `TextBlock` style must not set `Foreground`.** Every string on a button, a tab or
  a menu row is rendered by a TextBlock a ContentPresenter made, and a style foreground beats the
  inherited one - so disabled controls keep full-strength text and a background tab looks selected.
  Colour comes down the tree from the window; the style only says typeface and rendering mode.
- **An explicit `RowStyle`/`ItemContainerStyle` replaces the implicit one rather than extending
  it.** Both logs and the plan grid carry `BasedOn="{StaticResource {x:Type ...}}"` for this
  reason; without it they keep their triggers and lose their templates.
- **A `{DynamicResource}` inside a Freezable in a `Setter.Value` does not resolve.** That is what a
  `GradientStop` in a style is, so the two gradients are composed in `ThemeResources` and
  referenced as one finished brush.

**The layering rule is the important one.** `NetControl.Core` must stay UI-free so the same code path
drives the GUI, the CLI, and the tests. If something in Core needs to report progress, it raises
an event or returns a stream - it does not know a window exists.

## Commands

```powershell
dotnet build
dotnet test

# The one test that broadcasts on a real adapter is opt-in. Do not set this on a plant network.
$env:NETCONTROL_WIRE_SERVE=1; dotnet test --filter ServesAPlannedDeviceOutOfARealAdapter

dotnet run --project src/NetControl.App
dotnet run --project src/NetControl.Cli -- commission plan.csv --nic "I219"

# What gets onto a plant laptop, and both are published on every release. Deliberately not in
# the .csproj: setting PublishSingleFile there pins a RuntimeIdentifier onto every build and test run.
#
# Use the script rather than the raw command: it runs the tests first, passes the short commit as
# SourceRevisionId so the exe reports 0.5.0+a1b2c3d rather than 0.5.0, and writes the SHA256 and the
# update manifest beside it. DEPLOY.md is the whole story; RELEASING.md is how a release is cut.
pwsh tools/publish.ps1                    # artifacts/NetControl-<version>/NetControl.exe
pwsh tools/publish.ps1 -Installer         # and dist_installer/NetControl-Setup-<version>.exe
pwsh tools/publish.ps1 -DownloadUrl https://intranet.example/tools/netcontrol/

# A release is a tag push. The workflow refuses to build a tag that disagrees with VersionPrefix.
git tag v0.6.0; git push --tags

dotnet publish src/NetControl.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Spikes 1 and 2 are standalone Phase 0 throwaways. Spike 3 is not: it references NetControl.Core
# on purpose, because it exists to put the shipping TFTP engine in front of a robot before the app
# has a tab for it. It takes UDP/69, so the real TFTP server has to be stopped - BENCH.md Run 6b.
dotnet run --project spikes/Spike1.BootpListen -- --list
dotnet run --project spikes/Spike2.CipStaticIp -- read 192.168.1.51
dotnet run --project spikes/Spike3.TftpWatch -- --list
dotnet run --project spikes/Spike3.TftpWatch -- --nic 12 --root "C:\TFTP-Root"
# --send is a client: one write request, no file data, and it reports which address and port
# answered. Point it at the watch in another window to exercise the whole path with no robot.
dotnet run --project spikes/Spike3.TftpWatch -- --send 127.0.0.1 --port 6969
# From another machine on the robot network, leaving by a named adapter. No robot, no outage.
dotnet run --project spikes/Spike3.TftpWatch -- --send 10.0.0.5 --from 10.0.0.55 --log probe.txt
# In a plant, always both: --log is a flushed transcript, --project is the append-only record.
# Started with neither it says so before it binds, because a scrollback buffer is not a record.
dotnet run --project spikes/Spike3.TftpWatch -- --log run6b.txt --project run6b.netcproj
# The bench copy: one self-contained exe at a path that stays put, plus the netsh line for it.
# A firewall rule names an exe, and bin\Debug is not somewhere to point one.
pwsh tools/publish-tftp-spike.ps1

# Refresh the embedded IEEE OUI table. A chore, not a build step - commit the changed oui.bin.
dotnet run --project tools/NetControl.OuiPacker -- update
dotnet run --project tools/NetControl.OuiPacker -- dump --mac 00:1D:9C:C7:B0:70
```

## Safety - this touches live plant networks

These are hard rules, not preferences:

- **Never write to a device that was not explicitly named by the user.** Discovery is broadcast;
  configuration is always unicast to one address the user typed or selected.
- **Never broadcast a write.** No "configure everything you found."
- **Never send a CIP reset, forward-open, or firmware operation as a side effect** of a read or
  scan operation.
- **Scanning defaults must be gentle.** Industrial devices have thin TCP stacks and some fall
  over under aggressive port scans. Default to low concurrency and a conservative rate; make
  anything faster opt-in and clearly labelled.
- **Serve mode must be explicit.** The BOOTP/DHCP server only answers MACs the user has listed.
  A rogue DHCP server on a plant network is a genuinely serious incident.
- **Never write a file to a TFTP server the user did not name**, and never by default into the live
  backup folder: the probe writes only the name typed, needs its tick on every run, and leaves its
  file. **Never delete anything over TFTP.**
- **Accept mode never writes outside the folder chosen, and never over an existing file unless told
  to.** `TftpAcceptPath` is the only thing standing between an unauthenticated request and the disk.
- **Every state-changing operation gets logged** to the append-only event log with timestamp,
  target, what was sent, and what came back.

## Protocol gotchas

Hard-won, easy to get wrong, expensive to debug:

- **Endianness flips between layers.** BOOTP/DHCP headers are big-endian. EtherNet/IP
  encapsulation and CIP are little-endian - *including IP addresses carried inside CIP
  attributes*, which is the reverse of `IPAddress.GetAddressBytes()`. The `ListIdentity` reply
  contains a `sockaddr_in` that is big-endian sitting inside an otherwise little-endian
  structure. Convert deliberately; never memcpy an address.
- **`IP_PKTINFO` is how you know the arrival NIC.** Bind `0.0.0.0:67`, set
  `SocketOptionName.PacketInformation`, read with `ReceiveMessageFromAsync`, use
  `IPPacketInformation.Interface`. Do not infer the interface from the bind address.
- **`IP_UNICAST_IF` (option 31) takes the interface index in network byte order** for IPv4.
  Getting that wrong fails silently.
- **BOOTP replies are limited broadcasts.** The client has no address yet, so there is nothing
  to unicast to and no ARP entry to use.
- **Pad BOOTP replies to a 300-octet minimum *message* length** (RFC 1542 section 2.1 - the 236-byte
  header plus a 64-byte vendor/options area). Some older adapters drop short replies.
  `BootpPacket.MinimumMessageLength` is that 300. If a device ever needs more, the other value
  worth trying is 548, since an RFC 2131 client must accept a 312-octet options field.
- **CIP attribute order matters.** Set TCP/IP Interface Object (`0xF5`) attribute 3
  (Configuration Control) to Static *before* writing attribute 5 (Interface Configuration).
  Several adapters reject attribute 5 while still in BOOTP/DHCP mode.
- **Always read back after a write.** A CIP success status means the request was accepted, not
  that the configuration persisted. Reconnect and re-read before reporting success.
- **Check Configuration Capability (`0xF5` attr 2) first.** If `ConfigurationSettable` is clear,
  the address is pinned by hardware switches and no write will ever work.

## C# specifics that have already bitten us

- **`Span<T>` locals are illegal in `async` methods *and in iterators*** (CS4013). Keep packet
  encode/decode in plain static helpers and call them from the async method. Passing `x.AsSpan()`
  as an argument is fine; declaring `var s = x.AsSpan();` inside an async method is not. The
  iterator half bites less often but bites the same way - `OuiDatabase.EnumerateTable` is a
  `yield return` loop precisely because the span work had to move out into `AssignmentAt`. A
  `foreach` over a `ReadOnlySpan<T>` is also out, since the enumerator is a ref struct too.
- **Do not call `NetworkInterface.GetAllNetworkInterfaces()` per packet.** It costs
  milliseconds. Use a short-TTL cache - adapters do come and go, so a permanent cache is wrong too.
- **CA1416 does not flow through lambdas.** An `if (OperatingSystem.IsWindows())` guard does not
  cover a LINQ lambda inside the block. Put Windows-only work in a method carrying
  `[SupportedOSPlatform("windows")]` and call that from the guard.
- **`Directory.Build.props` is imported before the project body**, so it cannot see properties the
  `.csproj` sets - a condition on one silently tests an empty string. Anything reacting to a
  project-level flag goes in `Directory.Build.targets`, which imports last.
- **Always build SQLite commands with `connection.CreateCommand()`.** It copies the connection's
  pending transaction onto the command. `new SqliteCommand(sql, connection)` does not, and
  Microsoft.Data.Sqlite throws when a command with no transaction executes on a connection that has
  one - so the failure only appears once a write is wrapped in a transaction, not when it is written.
- **`VACUUM INTO` silently copied nothing from an in-memory database.** Through
  Microsoft.Data.Sqlite, `VACUUM INTO $path` against a `Mode=Memory;Cache=Shared` connection
  returned without error and produced a file with no rows in it - which then reopened looking
  exactly like a brand new project, because the migrator ran and rebuilt the schema. Nothing in the
  build, the logs or the UI said a word. `ProjectStore.SaveAs` uses `SqliteConnection.BackupDatabase`
  instead, which is the documented way to copy a live database and does carry an in-memory one, and
  it counts the rows afterwards. Plain `sqlite3` handles `VACUUM INTO` from `:memory:` correctly
  including with a bound path, so this is something in the provider rather than in SQLite.
- **SQLite timestamps are text, so the format decides the sort order.** `SqlTime` writes round-trip
  ("O") UTC because it is fixed width; a shorter or variable-width format sorts wrongly across a
  fractional-second boundary, and nobody notices until a commissioning report lists events out of order.
- Prefer `BinaryPrimitives.Read/Write*Endian` over `BitConverter`. It states the byte order at
  the call site, which is exactly what this codebase needs.
- **`TreatWarningsAsErrors` is scoped in `Directory.Build.targets`** to projects that set none of
  `IsSpike`, `IsTestProject` or `IsTool`. An xUnit analyser suggestion should not be able to break
  a build, and neither should one in a maintenance tool nothing in `src/` references.
- **An XML comment may not contain `--`, and MSBuild reports it as something else entirely.**
  A comment in `Directory.Build.props` held the example command `git rev-parse --short HEAD`. That
  file carries `TargetFramework` for the whole repository, so a props file that will not parse
  leaves every project with an empty one - and the error is NuGet's
  `Invalid framework identifier ''` during restore, which says nothing about XML, nothing about
  comments and nothing about which file. Validating the `.csproj`/`.props`/`.targets` files is
  worth doing whenever they are edited by anything other than an IDE.
- **A cref to a type you have no `using` for is a phantom dependency.** Doc comments do not count
  as usage for unused-using analysis, so either fully qualify the cref or accept the warning.
  `NicInfo` fully qualifies `System.Net.Sockets.IPPacketInformation` for this reason.
- **WPF does not work under `InvariantGlobalization`, and the failure is completely silent.**
  `Directory.Build.props` sets it for the whole repo, so `NetControl.App` overrides it back to
  false. Every `FrameworkElement.Language` defaults to `XmlLanguage.GetLanguage("en-US")`, and
  building that `CultureInfo` throws `CultureNotFoundException` while `PredefinedCulturesOnly` is
  on. It throws inside `InitializeComponent` - before `OnStartup`, so before any handler can be
  attached - and a `WinExe` has no console, so the entire symptom is a process that exits with no
  window and no message. Nothing appears in the build, the tests, or on screen.
- **`UseWPF` silently removes `System.IO` from the implicit usings.** WPF ships
  `System.Windows.Shapes.Path`, so the SDK drops `System.IO` rather than let two `Path` types
  collide. The symptom is a project that compiled yesterday failing with "The name 'Path' does not
  exist in the current context" on `File`, `Directory`, `MemoryStream` and `IOException` too, and
  it will not obviously be about WPF. `NetControl.Tests` asks for it back with
  `<Using Include="System.IO" />` because it touches files everywhere and references no WPF
  namespace; a file in `NetControl.App` that needs it should carry its own `using System.IO;`
  instead, so the collision stays possible to reason about locally.
- **`Dispatcher.BeginInvoke`, never `Dispatcher.Invoke`, from a Core event handler.** `Invoke`
  parks the DHCP receive loop until the UI thread gets round to it, and a receive loop that is not
  reading the socket is a tool that misses requests. `WpfDispatcher` also drops work once
  `HasShutdownStarted`, because queuing during shutdown throws on a background thread.
- **A window's `MainWindow` property and a type called `MainWindow` are a name collision waiting
  to happen.** `App.OnStartup` simply does not assign it - WPF sets `Application.MainWindow` to
  the first window shown - because the alternative is either a `this.` qualifier the .editorconfig
  bans or a fully qualified name that reads worse than the thing it avoids.

## Conventions

- Nullable reference types on, warnings as errors in `src/`.
- `NetControl.Core` throws typed exceptions (`EnipException`, etc.); the UI catches and presents.
- Error messages name the likely cause and the next action. "Bind failed" is useless;
  "UDP/67 is held by vmnetdhcp.exe (pid 4312) - stop that service and retry" is the product.
- Comments explain *why*, especially for protocol quirks. The what is already in the code.
- One class per file in `src/`; spikes may be looser.

## Licensing

Build from public specs: RFC 951, RFC 1542, RFC 2131/2132, and ODVA's published CIP and
EtherNet/IP documentation.

- **Do not decompile or reverse-engineer the Rockwell tool.**
- Wireshark's ENIP dissector is useful for *understanding* the wire format, but it is GPL.
  Read it to learn; do not copy code from it into this repository.

## Current state

**Phase 0 is closed.** All four questions are answered in `FINDINGS.md`; both spikes compiled and
ran against `NetControl.DeviceSim`.

**Phase 1: sections 1.1, 1.2 and 1.3 are done.** Written, compiled, and green.

- `NetControl.Core/Interfaces` - `NicInfo`, `NicMonitor` (behind `INicInventory`),
  `PortConflictDetector`, `FirewallCheck`
- `NetControl.Core/Dhcp` - `BootpPacket` codec, `DhcpServer`, `IAssignmentPolicy` +
  `StaticMapPolicy`, `RetransmitFilter`, structured event records
- `NetControl.Core/Persistence` - `ProjectStore` (one SQLite file per project) with
  `SchemaMigrations`, `DeviceRepository`, `AssignmentRepository`, `EventLog`, and
  `DhcpEventRecorder` wiring the server's events into the append-only `Event` table
- `NetControl.Tests` - codec round-trips, reply construction, policy refusals, MAC parsing,
  persistence round-trips and migration, and wire tests that run the real server on a real socket

**Phase 1 section 1.4 is done.** Built, tested, and packed against the live IEEE registry.

- `NetControl.Core/Oui` - `OuiDatabase` (behind `IOuiLookup`) over a packed IEEE table embedded as
  `oui.bin`, with `OuiPackFormat` and `OuiPackWriter` beside it so the format is described once
- `tools/NetControl.OuiPacker` - downloads the four IEEE registry files and repacks `oui.bin`.
  In the solution so it opens in an IDE; referenced by nothing in `src/`
- `oui.bin` currently holds the 2026-08-07 registry: 58,134 assignments in 604 KB (39,902 MA-L,
  6,533 MA-M, 11,699 at 36 bits). A clone that has never been refreshed gets a ~20-prefix seed
  dated 1970-01-01 instead, which is the tell that nobody has run the update

**Phase 1 section 1.5 is part done: the shell, the interface bar and the live log.** Written,
compiled, green, and *run* - the window opens, grades the machine honestly, and binds UDP/67 in
Watch mode. No device has asked it for an address yet.

- `src/NetControl.App` - WPF shell on `net10.0-windows`. `AppHost` wires the graph by hand (no
  container), the app starts on `ProjectStore.CreateInMemory` so the event log is recording before
  anyone has saved a file, and `ServerController` owns the listener's lifetime so the view models
  never touch a socket. Its README covers the parts worth knowing before editing it.
- `NetControl.Tests` moved to `net10.0-windows` with `UseWPF` so it can reference the app. No test
  opens a window: the view models carry no WPF types and marshal through `IUiDispatcher`, which
  tests substitute with one that runs inline.

**Phase 1 section 1.5: the device grid is done, and so is Save As.** Written, compiled and green.
See `PLAN-NEXT.md` parts A1-A3.

- `ProjectStore.SaveAs` - the plan, the assignments and the whole event log recorded before anyone
  picked a filename, carried across together with `BackupDatabase` and then row-counted to prove
  it. Refuses to write over an existing file: a project file is a commissioning record, and the
  confirmation belongs to the dialog. See the `VACUUM INTO` note above for why it is not that.
- `DeviceRepository.Update` - edits by row id, MAC included, so correcting a typo moves the row
  rather than leaving the wrong one in the plan beside the right one. Changing the MAC of a device
  already in the commissioning record is refused, same rule as `Delete`.
- `DeviceState` - Planned / Seen / Served / Verified. The seam Phase 2's commissioning state
  machine plugs into. Nothing sets `Verified`; that is the CIP readback.
- `NetControl.App/ViewModels` - `DeviceGridViewModel` and `DeviceRowViewModel`. Every cell is text
  and the row does its own parsing; `Problem` (red, nothing written) is kept apart from `ServeNote`
  (amber, stored and unservable), and the amber wording comes from `DeviceRecord.TryToAssignment`
  so the row and the plan summary cannot disagree. See the app's README.
- **Plan a device from a log row - done.** Double-click a row in the live log, or "Add to plan" on
  its context menu, and the device is planned with its MAC and vendor. **The address cell is left
  empty and the tool never guesses at one** - an address comes off a drawing, and the lowest free
  address on the subnet is .1, which on a real panel is the gateway. A suggestion that is wrong
  nearly every time is worse than none, because it is wrong in the way that is easiest to accept by
  mistake. This was built the other way first and reverted; do not re-propose it.
- File menu gains New and Save as. There is deliberately no Save: SQLite writes as you type, so a
  project can only ever be missing a location, and the status bar says so.
- **Devices are named in the log - done** (`PLAN-NAMING.md` D2). Written, compiled and green. `DeviceRecord.DisplayName` is the
  one naming rule - `Role`, else `HostName`, else `PanelRef` - and it lives in Core so the grid, the
  log and any later report cannot disagree. The log gains a **Device** column carrying that name as
  a *snapshot* taken when the row was made: a log row is an account of a moment, and renaming a
  device must not retitle the requests it made before it had that name.
  `IPlanIndex` was widened from `Contains(mac)` to `Find(mac)` and is now backed by `PlanIndex` over
  `DeviceRepository.All()` rather than by `StaticMapPolicy`. That fixed a real bug rather than just
  adding a column: **the policy holds only rows that became a valid assignment**, so a device planned
  with a MAC and no address - which is exactly what "Add to plan" produces, and which the tool
  deliberately leaves addressless - was absent from it, and the log filed that device as a stranger
  for as long as it took to type an address. "May I serve this" and "did somebody type this in" are
  different questions and now have different objects answering them.

**Phase 2: set static / disable BOOTP is written in Core.** This is the half of the job BOOTP
alone does not do - an address served over BOOTP lasts until the next power cycle, because the
device is still a BOOTP client. Promoted from `spikes/Spike2.CipStaticIp` rather than invented.

- `NetControl.Core/Enip` - `EnipSession`: TCP 44818, RegisterSession, SendRRData over the CPF.
  There is no constructor that takes a broadcast address; configuration is a conversation with one
  device and the type makes that the only thing it can be.
- `NetControl.Core/Cip` - `CipRequest`/`CipResponse` with EPATH encoding, `CipGeneralStatus` (the
  wording is the product - "attribute not settable" becomes "the address is usually pinned by
  switches on the module"), `InterfaceConfig` for 0xF5 attribute 5, and `TcpIpInterface` as four
  typed operations over that object.
- `NetControl.Core/Commissioning` - `StaticIpCommissioner`. Capability, attribute 3 to Static,
  attribute 5, then reconnect and read both back. Refuses before writing anything when
  `ConfigurationSettable` is clear. Never resets a device unless the caller passed `AllowReset`:
  on a running line a reset is an outage, so it is a decision, not a side effect.
- `NetControl.Tests` - the commissioner runs end to end against `NetControl.DeviceSim` started
  in-process on a spare loopback port, so the framing, the EPATH and the little-endian address
  encoding are exercised over a real socket rather than asserted about. Plus byte-level codec
  tests, because the simulator was written from the same spec and a shared misreading would pass
  both sides.

**The commissioner is wired to the grid.** "Set static" on the selected row writes the planned
address into that one device and turns BOOTP/DHCP off, then reads it back. Progress goes to the
live log as it happens and every step into the append-only `Event` table under `EventCategory.Cip`;
`CommissionOutcome.Verified` is the only thing in the app that sets `DeviceState.Verified`.
"Allow reset if needed" is its own checkbox, defaulting to off, because on a running line
resetting a module is an outage.

**Phase 4 pulled forward: active discovery, Core and UI. Written, compiled and green** - 355 tests.
`PLAN-NEXT.md` parts B1, B3, B4 and B5. It was written in a session with no .NET SDK, so the first
run was a real check rather than a formality; one test was wrong (it asked about the mask rule
while arranging a device with no ARP entry, so the MAC refusal fired first) and the code was not.

- `NetControl.Core/Enip/ListIdentityReply` - the reply codec. It is the one thing allowed to read
  the `sockaddr_in`, which is big-endian sitting inside an otherwise little-endian structure. The
  address the device *claims* and the address it *answered from* are both carried and never
  collapsed: when they disagree, that disagreement is the finding.
- `NetControl.Core/Enip/IdentityScanner` (behind `IIdentityScanner`) + `ScanOptions` + `ScanReport`.
  One broadcast from the selected adapter, an optional paced list of unicast probes, a bounded
  collection window that starts when the last probe is away. **Replies dedupe by identity, not by
  address** - collapsing on address would hide two devices sharing one, which is the fault only a
  scan can find. A sweep refuses a broadcast or multicast target, and a scan with nothing to probe
  transmits nothing rather than falling back to a broadcast. An ICMP port-unreachable is counted as
  `Refused` rather than discarded: something is at that address and it is not EtherNet/IP.
- `NetControl.Core/Interfaces/ArpTable` (behind `IArpLookup`) - `GetIpNetTable`, beside the existing
  `GetExtendedUdpTable` and for the same reason there is no managed equivalent. `GetIpNetTable2` is
  the modern call but its row embeds a `SOCKADDR_INET` union and a `NET_LUID`, so marshalling it by
  hand means computing alignment by hand; the older row is a flat 24 bytes and IPv4-only, which is
  all this tool is.
- `NetControl.Core/Discovery/DeviceDiscovery` - the scan, then the ARP read, then the OUI lookup.
  The order is the feature: the replies are what populated those ARP entries, so the MAC costs
  nothing extra on the wire. **A MAC learned on a different adapter is not taken** - it is very
  likely a different device sharing an address, and a plan row keyed on the wrong MAC is a device
  that is never served for a reason nobody can see. `MacSource` keeps "not in the cache", "on
  another adapter" and "the cache could not be read" apart, because they need three different
  sentences in front of the user.
- Vendor still comes from the IEEE table against the MAC. The ODVA vendor id in the identity object
  is a different numbering scheme and no table for it is shipped - the product name does that work,
  which is B2 decided the honest way.

**The scan is wired to the window.** The bottom pane is now two tabs - **Live requests** and **Scan
results** - because a device that asked for an address and a device that answered a scan are
different facts about different devices, and only the first needs BOOTP at all.

- `ScanResultsViewModel` + `ScanResultViewModel`, and `MainViewModel.ScanCommand`, which needs the
  same `NicInfo.CanServe` adapter serve mode does: a scan transmits.
- **Every scan appends an `EventCategory.Scan` row** - the B4 rule - whether or not it found
  anything, graded Warn when two devices answered on one address. `DiscoveryResult.Summary` is that
  row's message and the status line above the list, so they cannot disagree.
- **"Add to plan" on a scan result fills the address in, and that is not the tool guessing.** It is
  the address the device itself reported. It also makes the common job one gesture: a device that
  came up on somebody else's DHCP server is scanned, added, and Set static writes the address it
  already has into flash. The mask is filled *only* when the device answered from inside the
  selected adapter's subnet, which is the one case where the adapter's mask is evidence rather than
  an invention - see `MainViewModel.MaskForScannedAddress`. This does not contradict the log-row
  rule below it: that rule is against *suggesting* an address nobody stated, and this address was
  stated by the device.

**Phase 1 section 1.5: CSV import and export is done.** `PLAN-NEXT.md` part A5. Written in a
session with no .NET SDK, then compiled and run on a machine that had one: 410 tests green, and
`src/` builds warning-free under warnings-as-errors. The parser's algorithm was checked before that
by porting it and running the fixtures through the port, which found the one real bug in it - see
the stray-quote note below.

- `NetControl.Core/Plan` - `CsvFile` (hand-rolled RFC 4180, about a hundred lines, no new package),
  `PlanCsv` over it for the eight columns, `PlanValidation` for the whole-file pass, and
  `PlanValidationResult` which **cannot hold both devices and problems**: the factories are
  `Imported` and `Refused`, so "a file with one bad row imports nothing" is a property of the type
  rather than a rule each caller has to remember.
- **Vendor is not a column.** It is resolved from the MAC against the OUI table at import and
  stored, same as everywhere else - a vendor typed into a spreadsheet is a vendor nobody checked.
- Quoting is the part that matters. A parser that splits on commas passes every file written by
  hand while testing, then shifts every column of the first real plan whose `Notes` says
  "drive, panel 3". Reading is lenient about a stray quote in an unquoted field and strict about
  one that never closes, because the second silently swallows the rest of the file.
  **Only a quote at the *start* of a field opens a quoted one.** The first version treated any
  quote as an opening quote, so `3" conduit` in an unquoted cell swallowed the rest of the file and
  then reported it as an unterminated quote - a real bug, found by porting `CsvFile.Parse` to
  another language and running the fixtures through the port, in a session that had no compiler.
  `TakesAStrayQuoteInsideAnUnquotedFieldLiterally` is the regression test.
- Addresses are parsed by `PlanValidation.TryParseIPv4`, not `IPAddress.TryParse`, which reads
  "192.168.1" as 192.168.0.1 and a bare "51" as 0.0.0.51. Both are plausible half-typed cells and
  both would import silently as an address nobody wrote.
- The three checks against *this machine* - own address, adapter's subnet, and therefore
  reachability for the readback - run only when the caller passes an adapter in
  `PlanImportContext`. That is the seam that lets a plan for a segment nobody is patched into yet
  still import, rather than being a wall the tool cannot be talked past.
- `MainViewModel.ImportPlan` / `ExportPlan`, on the File menu. Import requires the listener stopped
  (the A6 rule - swapping a whole set of mappings under a running server is a different job from
  adding one device, which stays allowed). Rows match by MAC and replace, so re-importing a
  corrected file corrects the plan rather than doubling it. Both write an `EventCategory.App` row,
  including refusals.

**Handing a device back to BOOTP/DHCP is wired up.** Written in a session with no .NET SDK, then
compiled and run on a machine that had one: 454 tests green, and `src/` builds warning-free under
warnings-as-errors.

- Core already did the work: `StaticIpRequest.Method`, the commissioner's skip of attribute 5, the
  `VerifyAddress` switch and the wording in `Compare` were all written for this and never called.
  What was missing was a way to ask for it, which is what this adds.
- `StaticIpRequest.HandBack` is the only way to build one, and it refuses `ConfigMethod.Static`. The
  difference between "make this permanent" and "give this up" was one enum member in an object
  initialiser, which is far too quiet for an operation that stops a device owning its address.
- `ToInterfaceConfig()` now throws for anything but Static. A hand-back request carries no mask
  anybody chose, and if the attribute 5 skip were ever removed the quiet result would be 0.0.0.0
  written into a live device's mask. It fails in a test instead.
- Two commands, `EnableBootp` and `EnableDhcp`, on a menu behind a confirmation that names the
  consequence - the device keeps working until its next power cycle, which may be weeks away and
  will not be attended by anybody who remembers the dialog.
- **A verified hand-back moves the row backwards.** `Verified` means "read back holding the address
  the plan gave it"; a device just told to ask for one is not that, and a plan that goes on saying
  Verified is a plan that lies. It returns to Served / Seen / Planned. The event is graded Warn
  rather than Info so that "when did this device stop being static" is findable.
- `CanHandBack` is deliberately weaker than `CanSetStatic`: no mask is needed because no address is
  written, and the device most likely to need handing back is one somebody set static months ago
  whose plan row has been half edited since.

**The scan is compared against the plan.** `PLAN-NEXT.md`'s last open question, closed. Also
written without an SDK, and green on the same run.

- `NetControl.Core/Discovery/PlanConformance` + `PlanConformanceReport` + `PlanFinding` +
  `PlanFindingKind`. Pure comparison: it sends nothing, reads nothing, changes nothing.
- **This is what the active scan was for.** The plan knows what was intended and the scan knows what
  is answering, and neither can say "the address you planned for the conveyor drive is one the HMI
  is already sitting on" alone. A duplicate check reading only the plan cannot say it, because the
  HMI was never typed in - and untracked equipment is exactly what causes this.
- **Silence is not evidence.** A planned device that did not answer produces no finding at all: it
  may be powered down, behind a switch the scan did not reach, or not built yet. A list that
  reported every absence as a problem is a list people learn to skip.
- Both facts about one row are reported. "Your device is not where you meant it to be" and
  "something else is where you meant it to be" are different problems with different fixes, and on
  a half-commissioned panel they are usually both true.
- A device is spoken about exactly once - the occupant of a planned address is reported against that
  row and not also as an unplanned stranger.
- Findings sort worst first, and by address as a *number* - otherwise .100 sorts above .2 and a list
  read top to bottom stops matching the panel.
- In the UI: a panel under the scan list holding the Warn and Error findings, each finding also
  landing in its own device's row note, and one event row per conflict attributed to the plan row it
  is about. The comparison re-runs when the plan is edited, so a finding about an address somebody
  has just corrected stops being shown.
- The decision table was ported to Python and run over fourteen arranged cases before it was ever
  compiled - the same technique that found the `CsvFile` stray-quote bug. The one failure was a
  wrong expectation in the port rather than in the code.

**There is a diagnostic log, and the build says which build it is.**

- `NetControl.Core/Diagnostics` - `TraceLog` behind `ITraceLog`, with `NullTraceLog` as the default
  everywhere it is optional. Hand-rolled rather than Serilog: the requirement is small and entirely
  known, and every package is one somebody has to justify to plant IT. Same argument as `CsvFile`.
- **Every line is flushed**, because the most valuable entry in the file is the last one before a
  crash. **It never throws** - it runs inside the crash handlers, where an exception would replace a
  reportable fault with an unreportable one. A folder it cannot write to comes back as a disabled
  log with `LastFailure` set, and startup carries on.
- Owned by `App`, not by `AppHost`: the graph is the thing most likely to fail on a locked-down
  laptop, and a log opened inside that constructor would be lost with it. The crash dialog now names
  the file, which is what turns "it crashed" into something that can be sent on.
- `%LOCALAPPDATA%\NetControl\logs`, via `AppPaths` - never beside the exe, which gets copied into
  Downloads and onto USB sticks and sometimes into folders nobody can write to.
- `Directory.Build.props` sets `VersionPrefix`. `BuildInfo` reads the informational version, the
  status bar shows it, the diagnostic log opens with it, and **every project file gets an event row
  naming the build that opened it** - a commissioning record that cannot say which version wrote it
  is missing the fact you need once a bug has been found and fixed. Only the real app stamps it; a
  test that constructs a view model gets an event log holding only what its own actions put there.
- `UpdateCheck` + `AppSettings`: one GET, five-second timeout, and it fetches nothing but the
  answer - downloading and applying are `UpdateDownloader` and `UpdateApplier` below, reached only
  by somebody pressing Install. It asks the repository's latest GitHub release by default; a site that mirrors builds
  points `updateManifestUrl` at its own `version.json`, and a site that wants no outbound request at
  all sets `"checkForUpdates": false` - the one state in which nothing is contacted, asserted by a
  test that counts calls on the handler. Both shapes of manifest are understood and **which one is
  in front of it is decided by the JSON, not by the URL**: a document carrying `tag_name` is a
  GitHub release, and the page offered to a person is `html_url`, never GitHub's own `url`, which is
  the API address of the release object. See `DEPLOY.md`.
- **A failed check is silent at startup and loud on request.** On a segment with no route out it
  fails at every launch, and a warning that is always present is one people learn to skip - the same
  argument that keeps the two quiet states quiet. `AppHost` therefore puts only an *available* update
  in the status bar, while **Help > Check for updates** answers all four outcomes because somebody
  pressed it and is waiting. That split is the whole reason `UpdateResult.StatusText` says something
  for `Failed` while the startup path does not use it.

**The updater downloads and applies.** `UpdateDownloader`, `UpdateApplier`, `InstallLocation` and
`UpdateWindow`, reached from the status-bar line or from Help. Four things in it are decisions
rather than plumbing:

- **An asset with no published `.sha256` is refused before a byte is fetched**, and one that hashes
  to something else is deleted rather than kept. The next thing that happens to a verified download
  is that it is *executed*, on a laptop that writes configuration into plant equipment. This is not
  a defence against somebody who controls the release page - they would publish a matching checksum
  - it is a defence against a truncated transfer, a captive portal answering 200 with a login page,
  and a laptop that slept mid-download. All three produce an executable that would otherwise run.
  The length is checked before the hash so the message can name the actual fault.
- **There is no helper script, and there must not be one.** A batch file that waits for a process to
  exit and then moves files is the traditional way to do this and is the one part nobody can test -
  it runs once, after the application that would report on it has closed. Instead: an installed copy
  hands off to Inno Setup, whose `/CLOSEAPPLICATIONS` closes this process **through the Restart
  Manager**, which asks a window to close the ordinary way before terminating anything - so
  `App.OnExit` runs and the socket and the project file are released normally. Which is why
  `UpdateApplier.RunInstaller` must **not** shut the application down: a process that has already
  gone is not one setup can restart. A portable copy uses the other Windows fact - a running image
  can be *renamed* though not overwritten - so it moves itself to `NetControl.exe.superseded`, copies
  the new build into the name it vacated, and starts it. The rename goes first because it is both the
  step most likely to be refused and the reversible one.
- **The two successes place opposite obligations on the caller**, which is why `UpdateApplyOutcome`
  has two of them rather than a boolean: after `InstallerRunning` the app must stay alive to be
  closed, after `Restarting` it must shut down. A single "it worked" would be right half the time.
  The one asymmetry inside the swap: a launch that fails after a successful swap is **not** rolled
  back - the new build is in place and correct, and putting the old one back would undo a good
  update because a window did not open.
- **An update is refused while the listener is running or a device is being commissioned**, with the
  reason on screen rather than a greyed-out button. Both are states where this process disappearing
  costs something relaunching cannot get back: a request nobody saw, or a record that stops in the
  middle of a write to live equipment.

`UpdateApplier.SweepLeftovers` runs from `App.OnStartup` rather than at the end of an update,
because the file being deleted is the one the updating process was running from - which cannot
delete itself and which anything can delete a moment later.
- `tools/publish.ps1` runs the tests, publishes the single-file exe, stamps the commit, writes the
  SHA256 and the manifest beside it, and with `-Installer` wraps it with Inno Setup.

**TFTP: the codec and the server-root check are written.** `PLAN-TFTP.md` parts E1 and E2. Written
in a session with no SDK; compiled and green on a machine that had one, at 549 tests.

The reason this is in a BOOTP tool at all: **a FANUC image backup over Ethernet is BOOTP and *then*
TFTP.** The controller is restarted into its boot monitor, brings the port up with no address,
broadcasts a BOOTP request, and only then sends a TFTP write request for the image. tftpd32/tftpd64
serves both halves from one window, which is exactly why nobody can tell which half broke when a
backup will not start. This tool already owns the first half and owns it better; `Tftp/` is what
lets it finish the sentence.

- `NetControl.Core/Tftp` - `TftpPacket` (all six opcodes, RFC 2347/2348/2349/7440 options), the
  message records, `TftpErrorText`, `TftpLimits`, and `TftpRootCheck`. No new dependency.
- **Strings decode as Latin-1, not ASCII.** `Encoding.ASCII` replaces every byte above 0x7F with a
  question mark, and the filename a controller asked for is the one thing this module exists to
  report accurately. The raw bytes are kept beside the string, and the name is never trimmed,
  normalised or case-folded - no TFTP server logs the name it refused, so this tool must not be the
  second thing that rewrites it.
- **`TftpRootCheck` writes a real probe file and deletes it.** Every permission API on Windows
  answers about the ACL, which is a different question from "will a file land here" - share
  permissions, a full volume, a quota or a network path that has gone away all pass the ACL check
  and refuse the write. Same reasoning as reading an address back off a device.
- **Negotiating `blksize` is not the fix for block-counter rollover, above about 90 MB.** 512-octet
  blocks cap a transfer at 33,553,920 bytes; 1468 - the largest that still fits an Ethernet frame -
  only reaches 96,205,380. A large FANUC image wraps the counter either way, and RFC 1350 does not
  say whether to wrap to 0 or to 1, so two ends that disagree write a file of the right length with
  the wrong contents. `NegotiatingTheLargestUnfragmentedBlockDoesNotSaveALargeImage` exists so that
  correction cannot regress into the comfortable version.

**The UDP/69 watch is written.** `PLAN-TFTP.md` part E3, Observe mode. `TftpWatchServer` binds
0.0.0.0:69 with IP_PKTINFO on and attributes each datagram to its arrival adapter in software - the
same design as `DhcpServer`, and for the same reason: binding per adapter is what makes a tool miss
the request that explains everything. It records the filename **exactly as sent**, the mode, the
options offered, the source endpoint and the arrival adapter, and then refuses.

Five things were decided while building it that the plan did not settle:

- **It answers rather than staying silent.** TFTP has no other way to say no. A client that gets
  silence assumes the packet was lost and retransmits until it times out, so a silent watch turns
  one request into a minute of them and leaves somebody at the pendant watching a progress bar that
  will never move. `SendRefusal` can turn it off for a strictly passive observation; it defaults on.
- **A request on an adapter outside the filter is recorded and never answered.** This is the one
  place the DHCP analogy breaks. Watch mode there transmits nothing at all, so the question never
  arose; here a refusal *is* a transmission, and this tool does not transmit onto a segment nobody
  selected.
- **The port-conflict refusal is worded the opposite way round from the DHCP one.** On UDP/67
  another process is an intruder. On UDP/69 it is almost certainly the backup server doing its job,
  and a message that reads like an accusation would be wrong about the most common case.
- **`TftpWatchMode.Accept` is declared and refuses to start.** A tool that reports itself as
  accepting backups while quietly refusing every one of them would be worse than no tool. Same
  precedent as `DeviceState.Verified` existing before anything set it.
- **The retransmit key is the source endpoint plus the filename.** TFTP has no transaction id and no
  MAC. Address and port together are the client's TID, fixed for one attempt and different on the
  next, so a controller that gave up and started again reads as a new attempt rather than a repeat.
  `TftpRetransmitFilter` is deliberately not `RetransmitFilter`: same job, different key, and the
  DHCP one is load-bearing.

`EventCategory.Tftp` was added, which is the first use of that enum's `Other` member doing its job -
a project file written by an older build maps the unfamiliar name to `Other` rather than refusing to
open.

**The watch records, and there is a way to run it.**

- `TftpEventRecorder` beside `DhcpEventRecorder`, under the same two rules: a handler on the receive
  loop must not throw, and a failed INSERT is counted and reported rather than allowed to stop the
  watch recording the next request - which on a bench session may be the only one anybody gets.
  **No row it writes carries a device id.** TFTP has no hardware address in it, so the source
  address goes in `Target` and the device column stays null; turning an IP into a planned device
  would be a join this tool has no evidence for.
- Severity follows the DHCP rule - about whether something needs fixing, not about whether we
  answered. In Observe mode every request is refused by design, so a refusal is `Info`; a request
  that would corrupt the image, or one whose refusal could not be sent, is `Warn`.
- **A concern raised against every request is not a concern.** The first version of
  `TftpRequestMessage.Concerns` flagged any write request that offered no `blksize` - which is
  exactly what a plain RFC 1350 client sends, so an ordinary request could never be graded `Info`
  and the warning column would have meant nothing. A test caught it. What replaced it fires only
  when the controller has **told us** the size via `tsize` and the block size in force cannot carry
  it: the evidence comes from the far end rather than from a guess, and it routes through
  `TftpLimits.DescribeRolloverRisk`. Same reasoning that keeps `PlanConformance` silent about a
  device that did not answer - a list that flags everything is a list people learn to skip.
- The raw filename bytes are carried as hex **only when the decoded name will not print**. That is
  the case where the string is not evidence and the bytes are, and it is rare enough that carrying
  hex on every row would be noise.
- `spikes/Spike3.TftpWatch` is the only way to run any of this today: **nothing in `NetControl.App`
  references `NetControl.Core.Tftp`.** The spike references Core rather than carrying its own codec,
  which is the one thing that would make a bench session meaningless. See `spikes/README.md`.

**The bench session's own equipment.** Three things that are not features and exist only so an hour
of robot downtime produces something afterwards:

- **`--log` and `--project`.** The deliverable of Run 6b is three short strings printed once, in a
  plant, by somebody with a robot to put back. `--log` mirrors everything printed to a file, flushed
  line by line and appended - a copy of what a person read, headed with the machine, the account and
  the arguments. `--project` records through `TftpEventRecorder` into a project file, which is the
  record proper: append-only, and the database refuses an `UPDATE`. **`--project` refuses to start
  if the file cannot be opened**, because a watch running without the record somebody asked for
  looks afterwards exactly like a watch that saw nothing. With neither, the spike says so before it
  binds. The two are deliberately different things, the same way `TraceLog` and `EventLog` are.
- **`TftpRefusalSource`.** See the "not proven" note below. A setting rather than a decision,
  because the observation that would settle it costs a robot for an hour.
- **`tools/publish-tftp-spike.ps1`.** One self-contained exe at a path that stays put, and
  `--firewall` printing the elevated `netsh` line for it from `FirewallCheck.BuildAddRuleCommand`.
  A firewall rule names an executable, and `bin\Debug\net10.0\tftp-spike.exe` is rebuilt by the next
  run - which is a poor thing to have pointed an elevated rule at while a controller is waiting.

**The TFTP tab: the watch, the UDP/69 checks and the verdict are in the app.** PLAN-TFTP.md parts
E1, E3 (Observe) and E5. **Written in a session with no .NET SDK and not yet compiled** - the first
`dotnet build`, or the `verify` workflow on push, is the check. Nothing in Core changed.

- `Serving/TftpWatchController` - `ServerController`'s shape for `TftpWatchServer`, with
  `TftpEventRecorder` attached for exactly as long as the watch runs. A separate controller because
  both listeners run at once: an image backup is a BOOTP request *and then* a TFTP request.
- `Diagnostics/BackupVerdict` - **the E5 sentence, as a pure function of `BackupEvidence`.** Four
  steps (asked for an address, got one, asked for the file, sent the file), graded in order, and the
  sentence names the earliest that is not known to have worked. Every step starts grey and turns
  green only on something seen; the transfer is never seen, because the watch refuses on purpose, so
  the backup as a whole is never reported as fine. When the first three are green the sentence says
  the robot side works and hands the problem to the server's root folder, filename rules and
  overwrite setting.
- `ViewModels/TftpViewModel` + `TftpRequestRowViewModel`, and a **TFTP backup** tab beside Live
  requests and Scan results. `MainViewModel` forwards the DHCP listener's requests, replies and state
  into it, so the verdict sees both halves. **UDP/69 is graded on its own row, never averaged into the
  interface bar**, and graded by vantage: a tick says whether this PC is the backup server, because
  nothing holding UDP/69 is the fault there and is what lets the watch bind on a laptop.
- The watch is **armed separately from being started**, like Serve, and refuses only on the adapter
  selected at the top of the window - a refusal is a transmission. Requests on other adapters are
  recorded and left alone, which is `TftpWatchServer`'s existing rule.
- The verdict follows one device: a FANUC (by OUI) in preference to anything else asking on the
  segment, and otherwise the most recent asker; a device NetControl has just served takes over.
- `RequireStopped` now also refuses to swap projects while the TFTP watch runs.
- `BackupVerdictTests` and `TftpViewModelTests` drive all of it with hand-built events and no
  socket; `TftpViewModel.NoteFileRequest` is internal for that reason, the same way the recorders are.

**Accept mode and the probe are built too** (PLAN-TFTP.md E3 Accept and E4), also without an SDK
and not yet compiled.

- `Tftp/TftpTransfer` - the lockstep send and receive loops, shared by both. Never answers a
  duplicate ACK with a retransmit (the Sorcerer's Apprentice bug), refuses packets from any other
  endpoint with error 5, sends block 0 after 65,535 and accepts 0 or 1, recording which. Results are
  `TftpTransferOutcome` + `TftpTransferStats`; a stall exactly at block 65,536 says it is the rollover.
- `Tftp/TftpProbe` - write a file of a known size to the real server, read it back, compare SHA-256.
  Reports the address and port the server answered from (a multi-homed server answering from the
  wrong address looks like silence to a controller), requested against granted options, the
  firewall signature of a transfer that never gets past block 1, and a closed port. The payload is
  `TftpProbePattern`, a position-derived stream, so 40 MB is never held in memory. 40 MB by default
  because it crosses the 512-byte rollover. **Writes one file, the name typed, and never deletes.**
- Accept mode in `TftpWatchServer` - receives a write into `AcceptFolder` and serves a read from it
  (which is what lets the probe's read-back run against it), one transfer at a time, each from a
  fresh port bound to the address the request arrived on. `TftpAcceptPath` keeps every name inside
  the folder and words each refusal as the diagnosis a real server would need: a missing subfolder,
  a `..`, a drive letter, a trailing space or dot Windows would silently drop. Writes go to
  `<name>.partial` and are renamed at the end; an existing file is refused unless
  `AllowOverwrite`. Finished transfers raise `TransferFinished`, which `TftpEventRecorder` records.
- **The probe and Accept mode are tested against each other over loopback**
  (`TftpAcceptAndProbeTests`), including a run at `blksize=8` that crosses the block counter's
  rollover in both directions - so neither side's reading of RFC 1350 is only checked by itself.
- The TFTP tab gains "Receive the backup" and "Allow overwrite" ticks and a probe panel whose
  confirmation tick clears after every run. `BackupVerdict`'s fourth step turns green when Accept
  mode receives the file, and the sentence then says the robot and the network are fine and the
  real server's configuration is the cause. The folder, the vantage tick, the overwrite tick and the
  probe's server are remembered in `%LOCALAPPDATA%\NetControl\tftp.json` (`TftpSettingsStore`).
- `PlanValidation.TryParseIPv4` is public now; the probe's server address uses it for the same reason
  the plan does.

### Pick up here

**Everything in `src/` compiles and all 590 tests pass.** The hand-back, the plan-versus-scan
comparison and the diagnostic log were all written in a session with no SDK and needed no fixing
once one was available - the only thing that had to be corrected was an XML comment in
`Directory.Build.props`, which is in the C# specifics section because of how far its error message
lands from its cause.

`Tftp/` went the same way: 23 files written without a compiler, and on first build the whole
solution came up clean under warnings-as-errors with 548 of 549 tests passing. The one failure was
a hand-written assertion, not ported logic - the Python port asserted a whole six-octet frame and
the transcription to C# kept the four header bytes and dropped the payload from the *expectation*.
**If that technique gets used again, have the port emit the C# expectations rather than retyping
them**, because retyping is now the only step in it that has ever produced a bug.

There is no outstanding code work that can be done at a desk. What is left is looking at it and
putting it in front of hardware.

**Four things have never been on screen.** A XAML binding that does not resolve costs nothing at
build time and shows an empty column at the bench:

- the Enable BOOTP/DHCP menu and its confirmation,
- the plan-versus-segment findings panel under the scan list,
- the version in the status bar,
- the update-check line beside it - point `updateManifestUrl` at a file that does not exist and
  check startup stays silent about it (the failure belongs in the diagnostic log, not the status
  bar) while **Help > Check for updates** says so out loud,
- the whole **Help** menu, which is new: Check for updates, Releases page, Open the diagnostic log
  folder, About. Worth doing on a machine with no route out as well as one with, since silence at
  startup and an answer on request are the two halves of one decision,
- **`UpdateWindow`, and the update it applies.** The first real one has to be watched: cut 0.5.1,
  run 0.5.0 from a folder on the desktop, and check the portable swap end to end - the progress bar
  moves, `NetControl.exe.superseded` appears, the new build comes up reporting 0.5.1, and the
  superseded file is gone after the *next* start rather than that one. Then the installed path, which
  is the one with no test behind it at all: install 0.5.0, update to 0.5.1, and check that setup
  closed the app rather than killing it - an `App.OnExit` line at the end of the diagnostic log is
  the evidence, and its absence means the Restart Manager terminated the process, which would be a
  project file closed without its handle released.

**Check the log file exists.** Run the app and look in `%LOCALAPPDATA%\NetControl\logs`. Then make
it fail - open a project file that another program is holding - and check the dialog names the log
and the log holds the stack.

**Do the hand-back against the simulator before doing it against anything else**, and watch the row
go from Verified back to Planned. That transition is the only thing in the app that moves a row
backwards, and getting it wrong leaves a plan claiming a device is commissioned when it is not.

**Open the window and press Scan.** The tests are green but nobody has looked at the new tab, and a
XAML binding that does not resolve costs nothing at build time and shows an empty column at the
bench. With nothing plugged in it should find nothing, say so in the status line, and write exactly
one `Scan` row to the event log. Then start `NetControl.DeviceSim` on the same machine - the first
check that proves the *app* parses a real reply off a real socket rather than a test doing it.

**Export a plan, edit it in Excel, and import it back.** The same argument: the two File menu
entries and their dialogs have never been on screen. Worth doing deliberately is opening the
exported file in a spreadsheet rather than a text editor, since the UTF-8 BOM and the quoting exist
for that reader specifically, and then breaking one row on purpose to see the refusal read the way
it was meant to - every problem at once, in the log, with the plan untouched behind it.

While there: **`ArpTable` has still never read a real ARP table.** It is the one piece with a
hand-marshalled native struct in it, every test that touches it uses `FakeArpLookup`, and a
mis-marshalled row reads plausible garbage rather than failing. Run a scan against the simulator
and check the MAC column says what `arp -a` says.

The new tests, for orientation: `ListIdentityReplyTests` (hand-built frames, including the address
stated the wrong way round so a "fix" that reverses it fails there rather than in a panel),
`IdentityScannerTests` (the real scanner against `NetControl.DeviceSim` over loopback - **no test
broadcasts**, every one probes an explicit loopback target), `DeviceDiscoveryTests` (the ARP join
and the wording for every way it fails) and `MainViewModelScanTests` (the event row, the plan row,
and the refusals). Shared fakes live in `FakeIdentityScanner`, `FakeArpLookup`, `FakeOuiLookup` and
`Identities`; `StubPreflight` moved out of `MainViewModelTests` into `Preflights.cs`.

**Nothing has been done to a real device yet.** The most valuable hour is still a bench session,
not more code. `BENCH.md` is the run sheet: setup, then Watch, Serve, Set static, and a power
cycle. That last step is the only test that tells a configuration written to flash from one
written to RAM, and no amount of simulator work substitutes for it. It also settles the
reply-send-mode question that has been open since Phase 0 - which the scanner now shares, since
`ScanOptions.SendMode` is the same setting and the same open question.

CSV import/export is written but uncompiled, above. The shape of a plan was settled while choosing
that work: **a flat list of rows, validated per row**. No scheme, no ranges - the only honest
conflict check against intent the tool does not hold is the live segment, which is what the scan is
for. Planning from a log row is done.

### What is actually proven, and what is not

Be precise about this, because the gap is where the remaining risk lives.

Proven on this machine, by tests that run every build:

- binding, receiving, and per-datagram attribution through `IP_PKTINFO`
- the codec, against hand-built frames matching what a Rockwell adapter emits
- Watch mode transmits nothing, even when a planned device asks
- Serve mode transmits nothing for a MAC that is not in the plan
- a request on an unselected adapter is reported rather than dropped
- a project file survives close/reopen, migrates only once, and refuses a file from a newer build
- the database itself rejects `UPDATE` and `DELETE` on `Event`, not just the API
- a failed database write is reported and the receive loop keeps running

- the packed OUI format round-trips, longest prefix wins over a parent MA-L block, shared vendor
  names are stored once, and a locally administered address is refused rather than guessed at

- the interface bar never shows green over a check it could not run, a fatal listener clears the
  green light, a serious port conflict blocks while an address-specific one only warns, and the
  selected adapter survives being unplugged and replugged
- the log collapses a retransmit into a counter, keeps two interleaved devices apart, and never
  files a planned-but-refused device under the same heading as a stranger
- serve mode cannot be started without both an adapter that can source a reply and an explicit arming

- an in-memory project survives Save As with its plan, its event log, its schema version and its
  append-only triggers intact, and an existing file is refused rather than replaced
- a grid edit reaches the dictionary the receive loop reads, in one gesture and with nothing to press
- correcting a mistyped MAC moves the row instead of adding a second servable one beside it
- a half-typed row shows its own problem and never reaches the file, and typing does not fill the
  request log with plan complaints

- a device double-clicked out of the log lands in the file in one gesture, having armed nothing and
  with no address invented for it - and reaches the policy the receive loop reads once one is typed
- planning a device already in the plan selects its row and leaves every cell on it alone
- a /26 subnet says a device one address past its broadcast is somewhere else, which is where a
  byte-wise comparison starts answering confidently and wrongly

- a planned device is logged under the name the plan gave it, a stranger's name cell is blank rather
  than saying "unknown", and renaming a device leaves the rows already logged alone
- a device in the plan with no address yet is not a stranger - the regression the policy-backed
  plan index caused, and the reason the index now reads the plan instead of the policy
- the naming rule falls through Role to HostName to PanelRef, and a cell holding only spaces is an
  empty cell rather than a name

- the whole set-static sequence, against a simulated adapter over a real socket: capability is read
  before anything is written, attribute 3 goes before attribute 5, and the address comes back off
  the device before it is called done
- a device that returns CIP success and discards the write is caught by the readback and reported
  as a mismatch - the failure the Rockwell tool does not catch
- a device whose address is pinned by switches is refused with **nothing written to it at all**
- a device that needs a reset is never reset unless the caller asked, and is reported unverified
  rather than done
- an address is encoded little-endian inside a CIP attribute, asserted against literal bytes

- from the grid: only a readback sets `Verified`, a device that lies about the write never reaches
  it, the whole sequence lands in the `Event` table as it happens, and the record can afterwards
  say that nothing at all was written to a device pinned by switches

- a ListIdentity reply is parsed off a real socket, from the simulator over loopback, and the
  address inside its `sockaddr_in` comes out big-endian while everything around it stays
  little-endian - asserted against hand-built frames, including one stating the address the wrong
  way round
- what the device claims about its address and where it answered from are carried separately and
  never collapsed
- one device answering twice collapses to one row; two devices answering on one address stay two
  rows and are reported as a contested address
- a scan with nothing to probe transmits nothing rather than falling back to a broadcast, and a
  unicast sweep refuses a broadcast, a directed broadcast or a multicast target
- a MAC learned on a different adapter is not taken, an invalid ARP entry is not a MAC, and "not in
  the cache" and "the cache could not be read" are worded differently
- every scan writes exactly one `EventCategory.Scan` row, whether or not it found anything, graded
  Warn when two devices answered on one address
- planning a scanned device carries the address the device itself reported, takes the mask only when
  the device answered from inside the selected adapter's subnet, and refuses outright - out loud,
  in Core's words - when the MAC could not be resolved

- a `Notes` cell containing a comma, a quote and a newline survives export and re-import, and a
  stray quote in an unquoted cell is content rather than the start of one that swallows the file
- a row is reported against the line it *starts* on, so a two-line quoted field does not send
  somebody to the wrong line of a hundred-row file
- a header naming one column twice is refused rather than letting one of them silently win, and a
  column the tool does not know keeps its position instead of shifting every column after it
- "192.168.1" and a bare "51" are refused rather than imported as 192.168.0.1 and 0.0.0.51
- a file with one bad row imports **nothing**, the plan that was already there is untouched, and
  every problem is listed at once with its line number
- two rows planning one address, or one MAC written two ways, are caught before anything is written
- a plan for a subnet this machine is not on imports when no adapter was passed, and is refused
  with the reason - including what the readback would have reported - when one was
- an import and a refusal each write exactly one `EventCategory.App` row, graded Info and Warn

- a hand-back writes Configuration Control and nothing else: the device comes back reporting BOOTP
  while holding exactly the address and mask it held before, and `ToInterfaceConfig` refuses to
  produce a structure for a request that is not Static, so a change that stopped skipping attribute
  5 fails there rather than on a panel
- `StaticIpRequest.HandBack` refuses `ConfigMethod.Static`, so the two opposite operations cannot be
  reached through one door by changing one enum member
- from the grid: a verified hand-back takes the row back off `Verified`, and the event is recorded as
  a warning naming what happens at the next power cycle
- a device can be handed back without a mask - nothing is written, so there is nothing for a missing
  mask to make wrong - while Set static still refuses the same row

- a planned address that something else is answering on is reported as a conflict naming both ends,
  and a planned device that did not answer at all is reported as nothing, because absence is not
  evidence
- two devices on one planned address stay a contested address and never become a confirmation, even
  when one of them is the device that was planned there
- a planned address answered by something with no resolvable MAC is neither confirmed nor a conflict
- both facts about one row are reported: the device answering from somewhere else, and the stranger
  sitting where it was meant to be
- a device is spoken about exactly once - the occupant of a planned address is not also filed as an
  unplanned stranger
- findings sort worst first and then by address as a number, so .2 comes before .100

- the diagnostic log writes timestamped lines, keeps one event on one line, carries the whole
  exception under it, rolls on a new day and on size, prunes to its retention count, and appends to
  the day's file rather than truncating it
- **a folder it cannot write into disables the log and records why, and every later call is a no-op
  rather than an exception** - it runs inside the crash handlers, where throwing would replace a
  reportable fault with an unreportable one
- the update check contacts nothing at all when it is turned off, asserted by the handler counting
  its calls; it asks the repository's own latest release when nothing is configured; it says nothing
  when already current, and does say so when a check was asked for and failed
- a GitHub release is told from a mirrored manifest by its JSON rather than by its URL, the page
  offered to a person is `html_url` and never the API `url` beside it, and the installer and the
  portable exe are picked out of the assets by name with the `.sha256` beside each
- **an asset with no published checksum is refused without a byte being fetched**, asserted by the
  handler's call count; one whose body is the wrong length is refused naming the length; one that
  hashes to something else is refused *and the file is deleted*
- the portable swap puts the new build in place and keeps the old one beside it, replaces a
  superseded build left by an earlier update, **puts the running build back when the copy fails**,
  keeps the new build when the swap worked but the launch did not, and replaces nothing at all when
  the process cannot say where it is running from
- the leftover sweep clears the previous build and the updates folder, and says nothing about a
  folder that was never there - it runs during startup, where throwing is the tool not opening
- versions compare as numbers, so 0.10.0 is newer than 0.9.0, and a commit suffix is which build
  rather than which version

- the TFTP codec round-trips every opcode against hand-built frames, reads block numbers
  big-endian, keeps a filename byte-for-byte through Latin-1 including one ASCII would destroy,
  parses a zero-length DATA as the end of an exactly-sized file rather than as a malformation,
  takes the whole message from an ERROR that was never null-terminated, tolerates zero padding
  after the options, and refuses a duplicate option rather than letting one of two silently win
- 1468-octet blocks reach 96,205,380 bytes and not a byte more, so a large image wraps the block
  counter even at the biggest block size that does not fragment - the claim that "negotiating
  blksize is the fix" fails in a test rather than on a night shift
- `TftpRootCheck` writes a real probe file and removes it, reports a missing folder as missing
  rather than as unwritable, warns rather than blocks on low space, and grades a folder it could
  not measure as Unknown rather than as fine

- the watch records a write request off a real socket over loopback, with the filename exactly as
  sent, the options as offered and the source port as the client's transfer identifier
- it answers an observed request with a TFTP error, and answers a retransmit again while marking
  only the log entry
- the refusal leaves the well-known port by default and a fresh ephemeral one when asked, asserted
  by the source port the client actually saw rather than by what the engine intended - and the row
  records which was used, while a request that was not answered records no port at all
- it records a request on an adapter outside the filter and **transmits nothing**, and transmits
  nothing at all when refusals are turned off - both asserted on the wire, not just in the engine
- a datagram on UDP/69 that is not TFTP is reported rather than discarded, and a mid-transfer
  packet arriving there is reported as the firewall-or-NAT signature it usually is
- Accept mode refuses to start rather than quietly behaving like Observe

Proven once, by hand, on a real machine - not by a test, so treat these as "seen working" rather
than "cannot regress":

- **`tftp-spike --list` ran on a real Windows machine and did diagnostic work on its first run.**
  It enumerated thirteen adapters, named the process holding UDP/69 *by name and PID*, read the
  firewall state, and graded a real folder as writable with its free space. What it found was a
  TFTP server nobody remembered was running - a SolarWinds instance left over from testing months
  earlier, on a wildcard bind. That is the interface-bar thesis working on the first real machine
  it met, and it is also a caution: it looked like an answer to "which server do the robots use"
  and was not.
- **The bind refusal fired against a real port owner**, and worded the conflict the right way round
  for a machine where the process holding UDP/69 is meant to be there.

- the window opens, every binding in `MainWindow.xaml` resolves, and the bar renders
- `NicMonitor` reports a fourteen-adapter laptop accurately, ranks the one real Ethernet port to
  the top, and notices an adapter's address changing mid-session
- `PortConflictDetector` finds the Hyper-V Default Switch's ICS DHCP on UDP/67 and grades it
  Advisory rather than blocking on it
- `FirewallCheck` reports no inbound rule for the executable, which is the true answer
- the listener binds UDP/67 alongside that conflict and runs in Watch mode

Not proven, and not provable at a desk:

- **No device has ever asked this app for an address.** Everything above happened with nothing
  plugged in. The request log has never shown a real request, no reply has been sent, and no
  `Assignment` row has been written by the app. That is the whole of what a bench session is for -
  see `BENCH.md`.

- **A remote device receiving an `IP_UNICAST_IF`-pinned broadcast.** Phase 0 only had a listener
  on the same machine. `ReplySendMode.PerSocketBind` stays until hardware says otherwise.
- **Anything against a real adapter.** The only test that broadcasts is opt-in behind
  `NETCONTROL_WIRE_SERVE=1`, because `dotnet test` must never put DHCP traffic on a plant network.
- **The frames.** `tests/NetControl.Tests/Frames.cs` holds synthetic frames, not captures. When a
  panel is next on the bench, take a pcap and add real ones. They are worth more than anything
  else in that project.

- **Any of the CIP work, against a real adapter.** Every commissioning test runs against
  `NetControl.DeviceSim`, which only reproduces quirks somebody has already met and written down -
  so it can prove the sequence has not regressed and can never discover a new way for hardware to
  misbehave. In particular nobody has yet power-cycled a real device to check a written
  configuration survived, which is the only test that distinguishes a configuration written to
  flash from one written to RAM.

- **No controller has ever sent this tool a TFTP request.** Every watch test is a synthetic frame
  over loopback. In particular the refusal goes out of the listening socket by default, so its
  source port is 69 rather than a fresh transfer identifier - which is what servers do for an
  immediate rejection, and which no FANUC has been seen to accept. **Both behaviours are now
  reachable without a code change** (`TftpWatchOptions.RefusalSource`, `--refuse-from-ephemeral`),
  because the only way to answer it is a controller in its boot monitor and nobody gets two of
  those in one visit - but which one a FANUC accepts is still unknown, and every log row names the
  port its refusal left from so the observation survives the session. **`TftpFrames.cs` holds
  frames built to match what a client should emit, not a capture.** When a controller is next doing
  an image backup, take one and add the real ones; it is worth more than the rest of that file.
- **Accept mode and the probe have only ever talked to each other.** Over loopback, in tests. No
  controller has sent Accept mode a backup and the probe has never written to a real server, so how
  either behaves against tftpd64, SolarWinds or a FANUC is unknown - which is exactly what the probe
  exists to find out on its first real run.
- **The TFTP tab has never been on screen and has never been compiled by anybody but CI.** It was
  written in a session with no SDK - see "The TFTP tab" under Current state. Same warning as every
  other blind-written screen here: a binding that does not resolve costs nothing at build time.
- **The watch has never received a TFTP request from anything but a test.** `tftp-spike --list` has
  run on a real machine; the watch itself has never had a real client talk to it, and `--send` has
  never been run at all.

- **No update has ever been applied.** The download and the portable swap are covered by tests,
  including the rollback, but every one of them runs against a temp folder and a stubbed launcher.
  Nothing has replaced a real running executable, and **the installed path has no test at all** -
  it is `Process.Start` on an Inno Setup exe plus the Restart Manager, and there is nothing in that
  a test could assert without asserting about the stub. `InstallLocation.Describe` has never read
  the real uninstall key either, so the choice between the two paths is itself unproven. The first
  release is the test: see the note in "Pick up here".

- **`ArpTable` has never read a real ARP table.** Every test that touches the join uses
  `FakeArpLookup`. It is the one piece in the project with a hand-marshalled native struct in it,
  and a wrong offset there produces plausible garbage rather than an exception - so until a scan
  has been run beside `arp -a`, the MAC column is unproven in a way nothing else here is.
- **No scan has ever been broadcast.** Every scanner test probes an explicit loopback target,
  deliberately, so `dotnet test` cannot put discovery traffic on a plant network. The broadcast
  path and `ScanOptions.SendMode` are bench work, same as the DHCP reply path.
- **The scan results tab has never been on screen.** The tests drive the view models, which carry
  no WPF types; a binding that does not resolve is silent at build time.

### Also worth knowing

- **The diagnostic log and the commissioning record are different things and must stay that way.**
  `EventLog`, inside the project file, is the account of what was done to somebody's plant
  equipment: append-only at the database level, and the one somebody may have to stand behind.
  `TraceLog`, in `%LOCALAPPDATA%`, is a rolling text file for working out why the *application*
  misbehaved, and it is deleted on a schedule. Do not put equipment events in the second, and do
  not put stack traces in the first.
- `DEPLOY.md` - what ships, where the version comes from, and how the update check is turned off.
  `RELEASING.md` - the repository, the installer, and how a release is cut. `README.md` is the front
  door for somebody who has never seen the repository.
- **The repository is public and the licence is not open source** (`LICENSE`: copyright reserved,
  published to be read and downloaded). That is only possible because nothing here is derived from
  decompiling the Rockwell tool or from Wireshark's GPL dissector - which makes the rule in the
  Licensing section above a distribution constraint now, not just a preference.
- **Never change the `AppId` GUID in `installer/NetControl.iss`.** It is how Windows recognises an
  existing install and upgrades it in place rather than leaving two NetControls in Add/Remove
  Programs. The install is per-user (`%LOCALAPPDATA%\Programs\NetControl`) so it needs no admin,
  and the uninstaller removes the log folder but **deliberately leaves `settings.json`** - that file
  is site configuration, most importantly the switch that stops the tool touching the network.
- `AppPaths` is the only place that decides where the tool keeps its own files, and it is
  `%LOCALAPPDATA%` rather than beside the exe on purpose. The product is a single file that gets
  copied into Downloads and onto USB sticks, and a tool that writes its log next to itself will one
  day silently write nothing.
- **`AppPaths.cs` carries its own `using System.IO;`** and always will. `UseWPF` drops `System.IO`
  from the implicit usings, so a file that is entirely about paths has to ask for it back. See the
  C# specifics section.
- `src/NetControl.DeviceSim/NetControl.DeviceSim.csproj` still sets `TreatWarningsAsErrors=false`
  with a TODO to flip it. It compiles clean now, so that can be tried.
- `src/NetControl.App/README.md` - why the view models have no WPF types in them, how the
  interface bar grades, and why the app overrides Core's default reply send mode.
- `Directory.Build.props` sets `InvariantGlobalization=true` for the whole repo, and
  `NetControl.App` turns it back off because WPF cannot start with it on. See the C# specifics
  section - it is the most expensive hour in this project so far, because it produces no error
  anywhere at all.
- `spikes/README.md` - what each spike proved.
- `src/NetControl.Core/Oui/README.md` - refreshing the OUI table, and why all four IEEE files.
  Worth knowing: nearly a third of all IEEE assignments are narrower than 24 bits, so a
  three-byte-prefix table would confidently misname them rather than leave them unknown.
- `src/NetControl.DeviceSim/README.md` - how to exercise both spikes without hardware, including
  the quirk scenarios.

`NetControl.DeviceSim` arrived earlier than the roadmap called for because it makes the spikes testable
at a desk. It is the regression harness for everything after: when a real device misbehaves in a
new way, add a `Quirk` for it and a test, so the fix cannot silently regress once that hardware is
back in the panel.
