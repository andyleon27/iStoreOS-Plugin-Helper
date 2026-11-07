# Developer-only reproducible build; never invoked by the application.
[CmdletBinding()]
param([switch]$GenerateLockFile, [switch]$VerifyReproducible)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'third-party/sshnet-2026.0.0-pr1830'
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'source.json') -Raw | ConvertFrom-Json
$sdk = Join-Path $root '.tools/dotnet-10.0.201/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { throw 'Install the pinned portable SDK as described in third-party/sshnet-2026.0.0-pr1830/README.md.' }
if ((& $sdk --version).Trim() -ne $manifest.sdk.version) { throw 'Unexpected SDK version.' }
$cache = Join-Path $root '.tools/sshnet-pr1830-cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache 'baseline.zip'
if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $manifest.baseline.archiveUrl -OutFile $archive -TimeoutSec 120 }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.baseline.archiveSha256) { throw 'Baseline archive SHA256 mismatch.' }
$patch = Join-Path $vendor 'upstream-pr1830.patch'
if ((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.patch.sha256) { throw 'Official patch SHA256 mismatch.' }
$build = Join-Path $root ('.tools/sshnet-pr1830-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $build 'upstream')
$source = (Get-ChildItem -LiteralPath (Join-Path $build 'upstream') -Directory).FullName
$before = @{}
Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object { $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
# Stop parent-repository discovery: apply only the verified upstream patch to this extracted source.
$previousCeiling = $env:GIT_CEILING_DIRECTORIES
try {
    $env:GIT_CEILING_DIRECTORIES = $build
    & git -C $source apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'Official patch does not apply to pinned baseline.' }
    & git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Official patch failed.' }
} finally { $env:GIT_CEILING_DIRECTORIES = $previousCeiling }
$changed = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne $before[$_.FullName] })
if ($changed.Count -ne 1 -or $changed[0].FullName -ne (Join-Path $source 'src/Renci.SshNet/Session.cs')) { throw 'Unexpected upstream source changes.' }
$project = Join-Path $vendor 'PatchedSshNet.csproj'
$lock = Join-Path $vendor 'packages.lock.json'
if (!$GenerateLockFile -and !(Test-Path -LiteralPath $lock)) { throw 'Missing locked dependencies.' }
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:NUGET_PACKAGES = Join-Path $root '.tools/sshnet-nuget'
# Generate the exact upstream PolySharp compatibility sources once, then compile their sorted
# file set without generators. PolySharp's cross-process HashSet order is not deterministic.
$bootstrap = Join-Path $build 'bootstrap'
$generated = Join-Path $bootstrap 'generated'
$bootstrapArguments = @('build', $project, '--configuration', 'Release', '--nologo',
    ('-p:SshNetSource=' + $source), ('-p:SshNetBuildRoot=' + $bootstrap),
    ('-p:BaseIntermediateOutputPath=' + (Join-Path $bootstrap 'obj/')),
    ('-p:MSBuildProjectExtensionsPath=' + (Join-Path $bootstrap 'obj/')),
    '-p:EmitCompilerGeneratedFiles=true', ('-p:CompilerGeneratedFilesOutputPath=' + $generated),
    '--output', (Join-Path $bootstrap 'out'), ('-p:RestoreLockedMode=' + (!$GenerateLockFile).ToString().ToLowerInvariant()))
& $sdk @bootstrapArguments
if ($LASTEXITCODE -ne 0) { throw 'Pinned PolySharp source generation failed.' }
$polyfills = Join-Path $build 'polyfills'
New-Item -ItemType Directory -Path $polyfills | Out-Null
Get-ChildItem -LiteralPath $generated -Recurse -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $polyfills $_.Name) }
if (@(Get-ChildItem -LiteralPath $polyfills -Filter '*.cs').Count -eq 0) { throw 'No compatibility sources generated.' }
$hashes = @()
$copies = if ($VerifyReproducible) { 2 } else { 1 }
for ($i = 1; $i -le $copies; $i++) {
    $work = Join-Path $build ('build-' + $i)
    $arguments = @('build', $project, '--configuration', 'Release', '--nologo',
        ('-p:SshNetSource=' + $source), ('-p:SshNetBuildRoot=' + $work), ('-p:SshNetPolyfills=' + $polyfills),
        ('-p:BaseIntermediateOutputPath=' + (Join-Path $work 'obj/')),
        ('-p:MSBuildProjectExtensionsPath=' + (Join-Path $work 'obj/')),
        '--output', (Join-Path $work 'out'), ('-p:RestoreLockedMode=' + (!$GenerateLockFile).ToString().ToLowerInvariant()))
    & $sdk @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Patched SSH.NET build failed.' }
    $dll = Join-Path $work 'out/Renci.SshNet.dll'
    $hashes += (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (@($hashes | Select-Object -Unique).Count -ne 1) { throw 'Reproducible build hashes differ.' }
if ($manifest.result.sha256 -and $hashes[0] -ne $manifest.result.sha256) { throw 'Built DLL does not match the pinned result.' }
$output = Join-Path $vendor 'lib/net462'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $output 'Renci.SshNet.dll')
Copy-Item -LiteralPath (Join-Path $work 'out/Renci.SshNet.xml') -Destination (Join-Path $output 'Renci.SshNet.xml')
Write-Output ('Patched DLL SHA256=' + $hashes[0])
Write-Output ('Reproducible builds=' + $copies)
Write-Output ('Source-only changes=' + $changed[0].FullName.Substring($source.Length + 1))
