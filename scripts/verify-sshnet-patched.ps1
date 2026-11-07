[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root 'third-party/sshnet-2026.0.0-pr1830'
$manifest = Get-Content -LiteralPath (Join-Path $directory 'source.json') -Raw | ConvertFrom-Json
$dll = Join-Path $directory 'lib/net462/Renci.SshNet.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Missing pinned patched SSH.NET; run scripts/build-sshnet-patched.ps1.' }
$stream = [IO.File]::OpenRead($dll)
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose(); $stream.Dispose() }
if ($hash -ne $manifest.result.sha256) { throw 'Patched SSH.NET SHA256 mismatch; do not substitute the unpatched NuGet DLL.' }
$identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
$token = -join ($identity.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') })
if ($identity.Version.ToString() -ne $manifest.result.assemblyVersion -or $token -ne $manifest.result.publicKeyToken) { throw 'Patched SSH.NET assembly identity mismatch.' }
Write-Output 'Pinned SSH.NET 2026.0.0 + official PR #1830 verified.'
