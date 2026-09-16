<#
    Compile Webcam Control.

    Aucune dependance externe : on utilise le compilateur C# livre avec le
    .NET Framework, present sur toute installation de Windows.
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin')
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "Compilateur C# introuvable. Le .NET Framework 4.x est requis."
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$exe = Join-Path $OutDir 'WebcamControl.exe'
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$refs = @(
    'System.dll'
    'System.Core.dll'
    'System.Drawing.dll'
    'System.Windows.Forms.dll'
    'System.Runtime.Serialization.dll'
    'System.Xml.dll'
)

$cscArgs = @(
    '-nologo'
    '-target:winexe'
    '-optimize+'
    '-platform:anycpu'
    "-out:$exe"
) + ($refs | ForEach-Object { "-reference:$_" }) + $sources

Write-Host "Compilation vers $exe" -ForegroundColor Cyan
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Echec de la compilation (code $LASTEXITCODE)" }

Write-Host "OK : $exe" -ForegroundColor Green

if ($Run) { & $exe }
