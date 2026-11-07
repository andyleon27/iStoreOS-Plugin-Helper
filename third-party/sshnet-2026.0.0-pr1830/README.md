# SSH.NET 2026.0.0 + official PR #1830

This directory contains the project's pinned .NET Framework SSH.NET dependency. Normal application builds use `lib/net462/Renci.SshNet.dll`, verified by SHA256 before compilation. The unmodified NuGet DLL remains a test negative control and is never copied as the application's runtime SSH.NET DLL.

## Provenance

| Item | Fixed value |
| --- | --- |
| Upstream | https://github.com/sshnet/SSH.NET |
| Baseline tag | `2026.0.0` |
| Baseline commit | `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e` |
| Baseline archive SHA256 | `c75daf0093744f12bf50f9e7708c6460203ddd824ea7d2f441def7066f114cf0` |
| Official issue | [#1829](https://github.com/sshnet/SSH.NET/issues/1829) |
| Official PR | [#1830](https://github.com/sshnet/SSH.NET/pull/1830), merged 2026-09-01 |
| Original patch commit | `b0a456943e7263fce1119989edc2fc8f2060225a` |
| Merged/squashed commit | `f099365c9d4cf2ade92b92c203bbb2b345d2cd74` |
| Saved official patch SHA256 | `ba114459a3efd097c4621e642537b390eacc697b1b886018c741c62ee35d9391` |
| Build SDK | .NET SDK **10.0.201**, Windows x64 |
| Target framework | **net462**; application remains .NET Framework 4.8 x64 |
| DLL AssemblyVersion / FileVersion | `2026.0.0.1` |
| DLL informational version | `2026.0.0.1+7b2fd3dbf2.pr1830.f099365c9d` |
| Public key token | `1cee9f8bde3db106` (same upstream public strong-name identity) |
| Result DLL SHA256 | `31c62881cf8375b47118ab11a620c85d8208ae86b93f418f58ff2c79605ad481` |

The only upstream runtime-source change is the saved official patch: ten added lines in `Session.cs`, calling `_serverMac.Initialize()` under `#if NETFRAMEWORK` after successful MAC verification in both ETM and non-ETM receive paths. No cipher/MAC implementation, algorithm collection, ordering, or upstream version downgrade is involved. Neither a floating branch nor the remainder of develop is used.

`PatchedSshNet.csproj` is a build wrapper for the full fixed upstream runtime sources. It keeps the same runtime dependency versions as upstream; `packages.lock.json` pins all transitive package versions and content hashes. Only development tooling differs from the upstream project: fixed assembly metadata replaces Git-dependent version generation and `BuildVersion.cs` supplies the equivalent NBGV-generated `NuGetPackageVersion` constant. The advertised client version remains `2026.0.0`.

PolySharp 1.16.0 is upstream's build-only compatibility generator. Its generated file order varies between processes. The script first generates the compatibility sources from that exact package, freezes them in sorted filename order, and compiles the final DLL without running generators again. These generated types are compatibility shims/attributes, not new project-written cryptography. Path maps, compiler version, UI language, metadata and lockfile are fixed. Two independent build roots, each with two final compilations, produced the DLL hash above.

## Rebuilding

Use Windows 10/11 x64, PowerShell 7, Git, and the pinned portable SDK. This SDK is a developer tool only; it is not a product runtime requirement or release attachment.

Install the SDK only into the project directory, using the fixed Microsoft URL and SHA512 in `source.json`. For example, from the repository root in PowerShell 7:

```powershell
$m = Get-Content third-party/sshnet-2026.0.0-pr1830/source.json -Raw | ConvertFrom-Json
$archive = Join-Path $PWD '.tools/dotnet-sdk-10.0.201-win-x64.zip'
Invoke-WebRequest -Uri $m.sdk.url -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $m.sdk.sha512) { throw 'SDK hash mismatch' }
Expand-Archive $archive .tools/dotnet-10.0.201
./scripts/build-sshnet-patched.ps1 -VerifyReproducible
./scripts/build.cmd
```

The script downloads the immutable source archive into `.tools/sshnet-pr1830-cache/baseline.zip` if absent, checks its hash and the saved patch hash, applies that patch in an isolated extraction, and verifies that **only `src/Renci.SshNet/Session.cs` changed**. It then restores locked build dependencies into `.tools/sshnet-nuget`, builds twice, checks identical hashes against `source.json`, and copies only the verified DLL/XML to this directory. A missing/mismatched dependency or output is an error, never a silent fallback to the NuGet DLL.

`-GenerateLockFile` is only for initially authoring an audited lockfile. Normal rebuilds must use the committed `packages.lock.json` with locked mode. Preserve the SDK, archive and NuGet cache for offline rebuilding. Build intermediates and SDKs stay under `.tools` and do not enter the Git source set or application ZIP.

To reproduce tests:

```powershell
./tests/iStoreOS.PluginHelper.Tests/bin/x64/Release/iStoreOS.PluginHelper.Tests.exe --ssh-protocol-only
./tests/iStoreOS.PluginHelper.Tests/bin/x64/Release/iStoreOS.PluginHelper.Tests.exe
```

The MAC tests invoke the actual DLL's private `Session.ReceiveMessage` on buffered valid SSH packets, for SHA-256/SHA-512/SHA-1 and both receive branches. A deterministic old-framework HMAC-state fixture forces missing-reset failure even on updated mscorlib. The original NuGet DLL fails at packet two; patched DLL passes three consecutive packets. Native framework HMAC tests and corrupted-MAC rejection also run. IL inspection requires both actual Initialize calls in the patched DLL and zero in the original.

## License

MIT, with upstream LICENSE and THIRD-PARTY-NOTICES retained here. The publicly distributed upstream strong-name build key is read only from the fixed source archive during compilation; user credentials, private authentication keys and raw negotiated key material are never part of this directory or its diagnostics.
