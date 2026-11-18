# One-time test for OMEN board 8901, BIOS F.24. Run in an administrator shell.
[CmdletBinding()]
param([switch]$RestoreOnly)

$ErrorActionPreference = 'Stop'
$bios = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.SystemManufacturer -ne 'HP' -or $bios.BaseBoardProduct -ne '8901' -or $bios.BIOSVersion -ne 'F.24') {
    throw 'This test is specific to HP board 8901 with BIOS F.24.'
}

function Get-ChargeMode {
    $reading = & (Join-Path $PSScriptRoot 'Get-OmenChargeStatus.ps1')
    return [Convert]::ToByte($reading.RawBatteryStatus.Substring(0, 2), 16)
}

function Set-TestChargeMode([ValidateSet('Pause','Auto')][string]$Mode) {
    # The second byte is the SBCO mode. Auto hands control back to firmware.
    $payload = [byte[]](0, 0, 0, 0)
    if ($Mode -eq 'Pause') { $payload[1] = 5 }
    $query = New-CimInstance -Namespace root/wmi -ClassName hpqBDataIn -ClientOnly -Property @{
        Sign = [Text.Encoding]::ASCII.GetBytes('SECU')
        Command = [uint32]2
        CommandType = [uint32]0x2B
        Size = [uint32]4
        hpqBData = $payload
    }
    $response = Invoke-CimMethod -InputObject $target -MethodName hpqBIOSInt4 -Arguments @{ InData = $query } -OperationTimeoutSec 10
    $reply = $response.OutData
    if ($null -eq $reply.rwReturnCode -or $reply.rwReturnCode -ne 0 -or
        $reply.Data.Length -ne 4 -or $reply.Data[0] -ne 0) {
        throw "The firmware did not confirm the $Mode request."
    }
}

function Get-BatteryReading {
    $batteries = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -OperationTimeoutSec 5 |
        Where-Object Active)
    if ($batteries.Count -ne 1) { throw 'Expected one active battery.' }
    $battery = $batteries[0]
    foreach ($field in 'PowerOnline', 'Charging', 'Discharging') {
        if ($null -eq $battery.$field) { throw "Missing battery reading: $field" }
    }
    return $battery
}

function Confirm-ChargingState([bool]$Charging) {
    $deadline = [datetime]::UtcNow.AddSeconds(25)
    $matchesInARow = 0
    $expectedMode = if ($Charging) { 1 } else { 4 }
    do {
        $battery = Get-BatteryReading
        if (!$battery.PowerOnline -or $battery.Discharging) { throw 'AC power lost or battery discharging.' }
        $mode = Get-ChargeMode
        if ($battery.Charging -eq $Charging -and $mode -eq $expectedMode) {
            if (++$matchesInARow -ge 2) { return }
        } else { $matchesInARow = 0 }
        Start-Sleep -Seconds 2
    } while ([datetime]::UtcNow -lt $deadline)
    throw "Could not confirm Charging=$Charging on AC (last firmware status $mode)."
}

if (!$RestoreOnly) {
    Add-Type -AssemblyName System.Windows.Forms
    $power = [System.Windows.Forms.SystemInformation]::PowerStatus
    if ($power.PowerLineStatus -ne 'Online' -or $power.BatteryLifePercent -ge 1 -or
        $power.BatteryChargeStatus -eq 'Unknown' -or
        !($power.BatteryChargeStatus -band [System.Windows.Forms.BatteryChargeStatus]::Charging)) {
        $percent = [Math]::Round(100 * $power.BatteryLifePercent)
        $reason = if ($power.PowerLineStatus -ne 'Online') {
            'Connect the charger and wait for Windows to show charging.'
        } elseif ($power.BatteryLifePercent -ge 1) {
            'The battery is full. Retry when it is below full and charging.'
        } else {
            'AC is connected. Retry when Windows reports active charging.'
        }
        Write-Output "Skipped before firmware access. AC=$($power.PowerLineStatus), battery=$percent%, status=$($power.BatteryChargeStatus). $reason"
        return
    }
}
$targets = @(Get-CimInstance -Namespace root/wmi -ClassName hpqBIntM | Where-Object Active)
if ($targets.Count -ne 1) { throw 'Expected one active HP firmware interface.' }
$target = $targets[0]
if ($RestoreOnly) {
    Set-TestChargeMode Auto
    $mode = Get-ChargeMode
    if ($mode -notin 0, 1) { throw "Automatic charging could not be confirmed (firmware status $mode)." }
    Write-Output 'Firmware reports automatic charging mode.'
    return
}
Confirm-ChargingState $true
try {
    Set-TestChargeMode Pause
    Confirm-ChargingState $false
    Write-Output 'Pause verified by Windows and firmware with AC online.'
} finally {
    try {
        Set-TestChargeMode Auto
        Confirm-ChargingState $true
        Write-Output 'Charging resumed on AC.'
    } catch {
        throw "Recovery unconfirmed. Keep AC connected and rerun this script with -RestoreOnly. $($_.Exception.Message)"
    }
}
