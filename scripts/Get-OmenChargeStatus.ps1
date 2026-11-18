# Query the battery control status on the PowerDial development laptop.
# Only BIOS read command 1 is issued. Keep the raw result until this board's
# status codes have been validated against physical charging behavior.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bios = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.BaseBoardProduct -ne '8901' -or $bios.BIOSVersion -ne 'F.24') {
    throw 'This diagnostic targets OMEN board 8901 with BIOS F.24.'
}

$namespace = 'root/wmi'
$targets = @(Get-CimInstance -Namespace $namespace -ClassName hpqBIntM |
    Where-Object Active)
if ($targets.Count -ne 1) {
    throw "Expected one active HP firmware interface, found $($targets.Count)."
}
$query = New-CimInstance -Namespace $namespace -ClassName hpqBDataIn -ClientOnly -Property @{
    Sign = [Text.Encoding]::ASCII.GetBytes('SECU')
    Command = [uint32]1
    CommandType = [uint32]0x2B
    Size = [uint32]4
    hpqBData = [byte[]](0, 0, 0, 0)
}
$result = Invoke-CimMethod -InputObject $targets[0] -MethodName hpqBIOSInt4 -Arguments @{ InData = $query }
$reply = $result.OutData
if ($null -eq $reply.rwReturnCode -or $reply.rwReturnCode -ne 0 -or $reply.Data.Length -ne 4) {
    throw "No valid four-byte battery status reply. Firmware return code: $($reply.rwReturnCode)"
}
[pscustomobject]@{
    Board = $bios.BaseBoardProduct
    BIOS = $bios.BIOSVersion
    RawBatteryStatus = [BitConverter]::ToString($reply.Data)
}
