<#
.SYNOPSIS
    Builds tftp-spike as one self-contained exe at a path that stays put, and prints the firewall
    rule that lets a controller's request reach it.

.DESCRIPTION
    A Windows Firewall rule names an executable. Under 'dotnet run' that executable lives in
    bin\Debug\net10.0, moves when the configuration changes, and is rebuilt by the next run - which
    is a poor thing to be adding elevated firewall rules for while a robot is sitting in its boot
    monitor. So the bench copy is published once, deliberately, somewhere it will still be tomorrow.

    Self-contained on purpose too: a plant laptop is not guaranteed to have the .NET 10 runtime, and
    "install a runtime" is a different conversation from "copy a file".

    This is the spike, not the product. tools/publish.ps1 is the one that ships NetControl.exe with
    a version stamp, a hash and an update manifest; this one only has to produce something that can
    be run and pointed at.

.PARAMETER Configuration
    Release unless you are debugging the publish itself.

.PARAMETER Runtime
    win-x64. There has never been a plant laptop here that was anything else.

.PARAMETER Output
    Where the exe lands. Defaults to artifacts/tftp-spike beside the repository.

.PARAMETER SkipTests
    Skip the test run. Available, and worth a moment's thought before using: the spike is a door
    into NetControl.Core, so the tests are what says the engine behind it still works.

.EXAMPLE
    pwsh tools/publish-tftp-spike.ps1

.EXAMPLE
    pwsh tools/publish-tftp-spike.ps1 -Output C:\Bench\tftp-spike
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'spikes/Spike3.TftpWatch/Spike3.TftpWatch.csproj'

if (-not $Output) { $Output = Join-Path $repo 'artifacts/tftp-spike' }

# The commit, for the same reason publish.ps1 wants it: an exe on a laptop that cannot be traced
# back to a commit is one nobody should be interpreting a bench observation from.
$revision = ''
try {
    $revision = (& git -C $repo rev-parse --short HEAD 2>$null).Trim()
    if (& git -C $repo status --porcelain) {
        Write-Warning 'The working tree has uncommitted changes; this build will be stamped -dirty.'
        $revision = "$revision-dirty"
    }
}
catch {
    Write-Warning 'No git here, so the build cannot be traced to a commit.'
}

Write-Host "Publishing tftp-spike to $Output" -ForegroundColor Cyan

# --- tests -------------------------------------------------------------------------------------
# The spike is deliberately not in NetControl.sln, but everything it does is Core's code. If Core is
# broken, the spike is a very expensive way to find that out - the robot is already down by then.
if (-not $SkipTests) {
    & dotnet test (Join-Path $repo 'NetControl.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing published.' }
}

# --- publish -----------------------------------------------------------------------------------
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained',
    '-p:PublishSingleFile=true',
    '-o', $Output,
    '--nologo'
)

if ($revision) { $publishArgs += "-p:SourceRevisionId=$revision" }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $Output 'tftp-spike.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe and it is not there." }

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash

Write-Host ''
Write-Host "  $exe" -ForegroundColor Green
Write-Host "  SHA256 $hash"
Write-Host ''

# --- the rule ----------------------------------------------------------------------------------
# Asked of the exe itself rather than composed here, so there is one place that knows what the rule
# has to say and it is the same place the interface bar reads.
Write-Host 'Firewall rule for this exe (elevated prompt):' -ForegroundColor Cyan
& $exe --firewall

Write-Host ''
Write-Host 'Then, in order:' -ForegroundColor Cyan
Write-Host "  $exe --list --root `"C:\TFTP-Root`""
Write-Host "  $exe --send <the TFTP server> --from <this laptop's address on that network>"
Write-Host "  $exe --log bench.txt --project bench.netcproj"
Write-Host ''
Write-Host 'The last one takes UDP/69, so the real TFTP server has to be stopped first, and started'
Write-Host 'again before you leave. BENCH.md Run 6b is the procedure.'
