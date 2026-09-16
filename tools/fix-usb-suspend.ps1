<#
.SYNOPSIS
    Empeche Windows d'endormir la webcam, ce qui lui fait perdre ses reglages.

.DESCRIPTION
    Deux leviers independants :
      1. le parametre "suspension selective USB" du plan d'alimentation actif ;
      2. l'autorisation donnee a Windows d'eteindre le concentrateur USB qui
         porte la camera, et la camera elle-meme.

    Sans -Apply ni -Revert, le script se contente d'afficher l'etat courant.

.PARAMETER Apply
    Desactive la mise en veille. Sauvegarde l'etat precedent pour -Revert.

.PARAMETER Revert
    Restaure l'etat sauvegarde par -Apply.

.PARAMETER DeviceMatch
    Fragment du nom de la camera. Par defaut "NexiGo".
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Revert,
    [string]$DeviceMatch = 'NexiGo'
)

$ErrorActionPreference = 'Stop'

$UsbSubGroup = '2a737441-1930-4402-8d77-b2bebba308a3'
$UsbSelectiveSuspend = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
$BackupPath = Join-Path $env:APPDATA 'WebcamControl\usb-suspend-backup.json'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SelectiveSuspend {
    # powercfg n'a pas de sortie machine : on lit les deux index dans le texte.
    $raw = powercfg /query SCHEME_CURRENT $UsbSubGroup $UsbSelectiveSuspend 2>$null
    $ac = ($raw | Select-String 'alternatif\s*:\s*(0x[0-9a-f]+)|AC Power Setting Index:\s*(0x[0-9a-f]+)' |
           Select-Object -First 1).Matches.Groups | Where-Object { $_.Success -and $_.Value -like '0x*' } |
           Select-Object -First 1
    $dc = ($raw | Select-String 'continu\s*:\s*(0x[0-9a-f]+)|DC Power Setting Index:\s*(0x[0-9a-f]+)' |
           Select-Object -First 1).Matches.Groups | Where-Object { $_.Success -and $_.Value -like '0x*' } |
           Select-Object -First 1
    [pscustomobject]@{
        Ac = if ($ac) { [int]$ac.Value } else { $null }
        Dc = if ($dc) { [int]$dc.Value } else { $null }
    }
}

function Get-CameraPowerDevices {
    # La camera, et le concentrateur USB dont elle depend.
    $cam = Get-PnpDevice -Class Camera -ErrorAction SilentlyContinue |
           Where-Object { $_.FriendlyName -like "*$DeviceMatch*" }
    if (-not $cam) { return @() }

    $targets = @($cam)
    $parentId = (Get-PnpDeviceProperty -InstanceId $cam.InstanceId `
                    -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    while ($parentId) {
        $parent = Get-PnpDevice -InstanceId $parentId -ErrorAction SilentlyContinue
        if (-not $parent) { break }
        $targets += $parent
        if ($parent.Class -eq 'USB' -and $parent.FriendlyName -match 'racine|Root') { break }
        $parentId = (Get-PnpDeviceProperty -InstanceId $parentId `
                        -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    }
    $targets
}

function Get-PowerState([string]$instanceId) {
    $esc = $instanceId.Replace('\', '\\')
    $node = Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue |
            Where-Object { $_.InstanceName -like "$esc*" -or $_.InstanceName -like "*$($instanceId.Split('\')[-1])*" }
    if ($node) { return [bool]$node[0].Enable }
    $null
}

function Set-PowerState([string]$instanceId, [bool]$allowPowerDown) {
    $node = Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue |
            Where-Object { $_.InstanceName -like "*$($instanceId.Split('\')[-1])*" }
    if (-not $node) { return $false }
    $node[0].Enable = $allowPowerDown
    Set-CimInstance -InputObject $node[0] -ErrorAction Stop
    $true
}

# ---------------------------------------------------------------- etat courant

$suspend = Get-SelectiveSuspend
$devices = Get-CameraPowerDevices

Write-Host ''
Write-Host 'Plan d''alimentation actif' -ForegroundColor Cyan
$label = { param($v) if ($null -eq $v) { 'inconnu' } elseif ($v -eq 0) { 'desactivee' } else { 'ACTIVEE' } }
Write-Host ("  suspension selective USB (secteur) : " + (& $label $suspend.Ac))
Write-Host ("  suspension selective USB (batterie): " + (& $label $suspend.Dc))

Write-Host ''
Write-Host 'Peripheriques concernes' -ForegroundColor Cyan
if ($devices.Count -eq 0) {
    Write-Host "  aucune camera correspondant a '$DeviceMatch'" -ForegroundColor Yellow
}
foreach ($d in $devices) {
    $state = Get-PowerState $d.InstanceId
    $txt = if ($null -eq $state) { 'pas de gestion d''alimentation' }
           elseif ($state) { 'Windows PEUT l''eteindre' } else { 'extinction interdite' }
    Write-Host ("  {0,-45} {1}" -f $d.FriendlyName, $txt)
}
Write-Host ''

if (-not $Apply -and -not $Revert) {
    Write-Host 'Relance avec -Apply pour desactiver la mise en veille, -Revert pour revenir en arriere.'
    return
}

if (-not (Test-Admin)) {
    throw 'Droits administrateur necessaires. Relance PowerShell en tant qu''administrateur.'
}

# ---------------------------------------------------------------- application

if ($Apply) {
    $backup = [pscustomobject]@{
        Ac      = $suspend.Ac
        Dc      = $suspend.Dc
        Devices = @($devices | ForEach-Object {
            [pscustomobject]@{ InstanceId = $_.InstanceId; Enable = (Get-PowerState $_.InstanceId) }
        })
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $BackupPath) | Out-Null
    $backup | ConvertTo-Json -Depth 5 | Set-Content $BackupPath -Encoding UTF8
    Write-Host "Etat precedent sauvegarde dans $BackupPath" -ForegroundColor DarkGray

    powercfg /setacvalueindex SCHEME_CURRENT $UsbSubGroup $UsbSelectiveSuspend 0
    powercfg /setdcvalueindex SCHEME_CURRENT $UsbSubGroup $UsbSelectiveSuspend 0
    powercfg /setactive SCHEME_CURRENT
    Write-Host 'Suspension selective USB desactivee.' -ForegroundColor Green

    foreach ($d in $devices) {
        if (Set-PowerState $d.InstanceId $false) {
            Write-Host ("  extinction interdite : " + $d.FriendlyName) -ForegroundColor Green
        }
    }
}

if ($Revert) {
    if (-not (Test-Path $BackupPath)) { throw "Aucune sauvegarde trouvee dans $BackupPath" }
    $backup = Get-Content $BackupPath -Raw | ConvertFrom-Json

    if ($null -ne $backup.Ac) { powercfg /setacvalueindex SCHEME_CURRENT $UsbSubGroup $UsbSelectiveSuspend $backup.Ac }
    if ($null -ne $backup.Dc) { powercfg /setdcvalueindex SCHEME_CURRENT $UsbSubGroup $UsbSelectiveSuspend $backup.Dc }
    powercfg /setactive SCHEME_CURRENT
    Write-Host 'Suspension selective USB restauree.' -ForegroundColor Green

    foreach ($d in $backup.Devices) {
        if ($null -ne $d.Enable -and (Set-PowerState $d.InstanceId ([bool]$d.Enable))) {
            Write-Host ("  restaure : " + $d.InstanceId) -ForegroundColor Green
        }
    }
}

Write-Host ''
Write-Host 'Termine. Debranche et rebranche la camera pour que le changement prenne effet.' -ForegroundColor Cyan
