<#
.SYNOPSIS
    Build PowerDial and run it.

.DESCRIPTION
    Produces PowerDial.exe at the repo root as a single self-contained file, then starts
    it. Works from any clone on any Windows PC - nothing here is tied to a user, a drive
    or a checkout path; the repo is located from the script's own location.

    Why a script rather than one dotnet command:

    * `dotnet build` refreshes bin\, but the exe people actually double-click is the
      published single file at the repo root, and that only changes when you publish.
      Building and then running the old exe looks exactly like "my change did nothing",
      which is the single most common way to waste an hour on this project.

    * Publishing straight into the repo root does not work. The SDK excludes its own
      output directory from source globbing, and the root is an ancestor of
      src\PowerDial, so every .cs file gets excluded and the compile fails with CS5001,
      "no entry point". Publishing to a subdirectory and copying one file up avoids it.

    * A running copy locks the exe, and the app's single-instance mutex would make a new
      one exit silently anyway. So it stops the old one first - and says something useful
      when it cannot, which happens whenever the running copy is elevated.

.PARAMETER SelfContained
    Bundle the .NET runtime into the exe. Much larger, but runs on a PC with no .NET
    installed. Without it the exe needs the matching .NET desktop runtime present.

.PARAMETER NoRun
    Build and publish, but do not start the app.

.PARAMETER Configuration
    Release (default) or Debug.

.EXAMPLE
    .\scripts\publish.ps1
.EXAMPLE
    .\scripts\publish.ps1 -SelfContained -NoRun
#>

[CmdletBinding()]
param(
    [switch] $SelfContained,
    [switch] $NoRun,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# ---------------------------------------------------------------- where everything is
# From the script, not from the working directory, so it does not matter where it is run
# from or what the checkout is called.
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PowerDial\PowerDial.csproj'
$staging = Join-Path $root '.publish'
$target  = Join-Path $root 'PowerDial.exe'

if (-not (Test-Path $project)) {
    Say "Cannot find $project." Red
    Say "Run this from a full clone of the repository - the script expects src\PowerDial beside it."
    exit 1
}

# ---------------------------------------------------------------- the SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Say "The .NET SDK is not installed, or dotnet is not on PATH." Red
    Say "Get it from https://dotnet.microsoft.com/download - this project needs the version"
    Say "named in global.json, or a later feature band of it."
    exit 1
}

# ---------------------------------------------------------------- which machine
# So the exe matches the PC it was built on rather than assuming Intel.
switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { $rid = 'win-x64' }
    'ARM64' { $rid = 'win-arm64' }
    'x86'   { $rid = 'win-x86' }
    default { $rid = 'win-x64' }
}

Say ""
Say "PowerDial - $Configuration, $rid$(if ($SelfContained) { ', self-contained' })" Cyan
Say "  $root"
Say ""

# ---------------------------------------------------------------- stop the old copy
# An elevated instance cannot be signalled from a normal shell. Both the file lock and
# the single-instance mutex have to be released before a new build is worth making.
$running = @(Get-Process PowerDial -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say "PowerDial is running (PID $($running.Id -join ', ')). Stopping it." Yellow
    try {
        $running | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 500
    } catch {
        Say ""
        Say "Could not stop it - that copy is running as administrator." Red
        Say "Right-click the PowerDial icon in the notification area and choose"
        Say "'Quit PowerDial', then run this again. Closing the window only hides it."
        exit 1
    }
}

# ---------------------------------------------------------------- build and publish
# not $args: that is an automatic variable in PowerShell
$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $rid,
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-p:PublishSingleFile=true',
    '-o', $staging,
    '--nologo'
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Say ""
    Say "Build failed - nothing was replaced, the old PowerDial.exe is untouched." Red
    exit $LASTEXITCODE
}

$built = Join-Path $staging 'PowerDial.exe'
if (-not (Test-Path $built)) {
    Say "The publish reported success but produced no PowerDial.exe in $staging." Red
    exit 1
}

Copy-Item $built $target -Force

$info = Get-Item $target
Say ""
Say ("PowerDial.exe   {0:yyyy-MM-dd HH:mm:ss}   {1:n1} MB" -f $info.LastWriteTime, ($info.Length / 1MB)) Green
Say "  $target"

# ---------------------------------------------------------------- run it
if ($NoRun) {
    Say ""
    Say "Not started (-NoRun)."
    exit 0
}

Say ""
Say "Starting it." Cyan
Start-Process -FilePath $target -WorkingDirectory $root
