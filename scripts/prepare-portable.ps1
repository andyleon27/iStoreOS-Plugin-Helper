# Developer-only release packaging. The application never invokes this script.
[CmdletBinding()]
param([switch]$Zip)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'src\iStoreOS.PluginHelper\bin\x64\Release'
$executable = Join-Path $buildDirectory 'iStoreOS.PluginHelper.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build Release x64 first.' }
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
if ($version -ne '1.0.0') { throw 'Unexpected product version; expected 1.0.0.' }
$packageName = 'iStoreOS.PluginHelper-v' + $version + '-win-x64'
$releaseDirectory = Join-Path $projectRoot ('artifacts\release\v' + $version)
$outputDirectory = Join-Path $projectRoot ('artifacts\package-work\' + $packageName + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory,$releaseDirectory -Force | Out-Null

# An explicit runtime allowlist prevents old developer output entering a release.
[xml]$references = Get-Content -LiteralPath (Join-Path $projectRoot 'build\Common.props')
$runtimeNames = @('iStoreOS.PluginHelper.exe','iStoreOS.PluginHelper.exe.config')
$runtimeNames += @($references.Project.ItemGroup.Reference | Where-Object { $_.HintPath } | ForEach-Object { Split-Path ([string]$_.HintPath) -Leaf })
if (@($runtimeNames | Where-Object { $_ -like '*.dll' }).Count -ne 13) { throw 'Unexpected runtime dependency count.' }
foreach ($name in $runtimeNames) {
    Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination $outputDirectory
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $outputDirectory

$licenseDirectory = Join-Path $outputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
[xml]$packageConfig = Get-Content -LiteralPath (Join-Path $projectRoot 'src\iStoreOS.PluginHelper\packages.config')
foreach ($package in $packageConfig.packages.package) {
    if ($package.developmentDependency -eq 'true') { continue }
    $packageDirectory = Join-Path (Join-Path $projectRoot 'packages') ($package.id + '.' + $package.version)
    $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '(?i)(license|notice)' })
    if ($notices.Count -gt 0) {
        $destination = Join-Path $licenseDirectory ($package.id + '.' + $package.version)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $destination }
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party\SSH.NET-LICENSE.txt'),(Join-Path $projectRoot 'third-party\DOTNET-LICENSE.txt') -Destination $licenseDirectory
Write-Output $outputDirectory

if ($Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $releaseDirectory ($packageName + '.zip')
    if (Test-Path -LiteralPath $archivePath) { throw 'Release ZIP already exists; retain it until deliberately replaced.' }
    $archive = [System.IO.Compression.ZipFile]::Open($archivePath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $outputDirectory -File -Recurse | Sort-Object FullName) {
            $entryName = $file.FullName.Substring($outputDirectory.Length + 1).Replace('\','/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $packageName + '.zip' + "`n"
    [System.IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksum, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output $archivePath
}
