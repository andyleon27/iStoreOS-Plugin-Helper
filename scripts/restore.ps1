# Developer-only dependency bootstrap. The application never invokes PowerShell.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsDirectory = Join-Path $projectRoot '.tools'
$nugetPath = Join-Path $toolsDirectory 'nuget.exe'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $nugetPath)) {
    Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe' -OutFile $nugetPath -TimeoutSec 60
}
& $nugetPath restore (Join-Path $projectRoot 'src\iStoreOS.PluginHelper\packages.config') -PackagesDirectory (Join-Path $projectRoot 'packages') -Source 'https://api.nuget.org/v3/index.json' -NonInteractive
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

