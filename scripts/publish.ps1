# Build the runnable PowerDial.exe at the repo root.
#
# There is a trap this exists to close: `dotnet build` refreshes bin\, but the exe most
# people actually double-click is the published single file at the repo root, and that
# one only changes when you publish. Building and then running the old exe looks exactly
# like "my change did not work".
#
# Publishing into the repo root directly does not work either - the SDK excludes its own
# output directory from source globbing, and the root is an ancestor of src\PowerDial, so
# every .cs file gets excluded and the compile fails with CS5001, no entry point. So it
# publishes to the project's own folder and copies the one file up.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# An elevated instance cannot be stopped from a normal shell, and it holds a lock on the
# exe plus the single-instance mutex that would make the new one exit silently.
$running = Get-Process PowerDial -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "PowerDial is running (PID $($running.Id -join ', '))." -ForegroundColor Yellow
    try {
        $running | Stop-Process -Force -ErrorAction Stop
        Write-Host "Stopped it."
    } catch {
        Write-Host "Could not stop it - it is running as administrator." -ForegroundColor Red
        Write-Host "Right-click the PowerDial icon in the notification area and choose Quit PowerDial, then run this again."
        exit 1
    }
    Start-Sleep -Milliseconds 400
}

dotnet publish "$root\src\PowerDial" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$built = "$root\src\PowerDial\bin\Release\net10.0-windows\win-x64\publish\PowerDial.exe"
Copy-Item $built "$root\PowerDial.exe" -Force

$stamp = (Get-Item "$root\PowerDial.exe").LastWriteTime
Write-Host ""
Write-Host "PowerDial.exe updated at $stamp" -ForegroundColor Green
Write-Host "  $root\PowerDial.exe"
