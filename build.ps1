# Builds Aurelia.exe (single portable exe, .NET Framework 4.x which ships with Windows 10/11).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) not found' }

$obj = Join-Path $root 'build'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$common = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/unsafe', '/debug-', '/warn:3',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Xml.dll')

# Pass 1: build without icon, use it to render the icon
$tmpExe = Join-Path $obj 'Aurelia.iconpass.exe'
& $csc @common "/out:$tmpExe" @sources
if ($LASTEXITCODE -ne 0) { throw "compile failed (pass 1)" }
$ico = Join-Path $obj 'Aurelia.ico'
$p = Start-Process -FilePath $tmpExe -ArgumentList @('--export-icon', "`"$ico`"") -Wait -PassThru
if ($p.ExitCode -ne 0 -or -not (Test-Path $ico)) { throw 'icon export failed' }

# Pass 2: final exe with embedded icon
$exe = Join-Path $dist 'Aurelia.exe'
& $csc @common "/win32icon:$ico" "/out:$exe" @sources
if ($LASTEXITCODE -ne 0) { throw "compile failed (pass 2)" }
Remove-Item $tmpExe -ErrorAction SilentlyContinue
Get-Item $exe | Select-Object FullName, Length, LastWriteTime
