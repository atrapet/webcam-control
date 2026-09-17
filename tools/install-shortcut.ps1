<#
.SYNOPSIS
    Cree le raccourci de Webcam Control dans le menu Demarrer.

.DESCRIPTION
    Raccourci par utilisateur : aucun droit administrateur n'est requis, et
    l'application devient trouvable par la recherche Windows.

.PARAMETER Tray
    Le raccourci demarre l'application directement dans la zone de notification.

.PARAMETER Remove
    Supprime le raccourci.
#>
[CmdletBinding()]
param(
    [switch]$Tray,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot '..\bin\WebcamControl.exe' | Resolve-Path -ErrorAction SilentlyContinue
$linkDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$link = Join-Path $linkDir 'Webcam Control.lnk'

if ($Remove) {
    if (Test-Path $link) { Remove-Item $link -Force; Write-Host "Raccourci supprime." -ForegroundColor Green }
    else { Write-Host "Aucun raccourci a supprimer." }
    return
}

if (-not $exe) { throw "WebcamControl.exe introuvable. Lance d'abord .\build.ps1" }

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($link)
$sc.TargetPath = $exe.Path
$sc.Arguments = $(if ($Tray) { '--tray' } else { '' })
$sc.WorkingDirectory = Split-Path $exe.Path
$sc.IconLocation = "$($exe.Path),0"
$sc.Description = 'Panneau de controle de la webcam : profils, auto-exposition adoucie, cadrage'
$sc.Save()

Write-Host "Raccourci cree : $link" -ForegroundColor Green
if ($Tray) { Write-Host "  demarre reduit dans la zone de notification" }
