@echo off
setlocal
pushd "%~dp0.."
if not exist "packages\SSH.NET.2026.0.0\lib\net462\Renci.SshNet.dll" (
  echo Restore dependencies first: powershell -File scripts\restore.ps1
  popd
  exit /b 1
)
if not exist ".tools\tmp" mkdir ".tools\tmp"
if not exist "artifacts" mkdir "artifacts"
rem Scope these variables to this build process. Braces in a sandbox TEMP path break old MSBuild.
set "TEMP=%CD%\.tools\tmp"
set "TMP=%TEMP%"
set "MSBUILD_EXE=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not exist "%MSBUILD_EXE%" (
  echo .NET Framework build tools are unavailable. Build the solution in Visual Studio.
  popd
  exit /b 1
)
"%MSBUILD_EXE%" iStoreOS.PluginHelper.sln /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:BuildInParallel=false /m:1 /nr:false /nologo /verbosity:normal
set "BUILD_RESULT=%ERRORLEVEL%"
popd
exit /b %BUILD_RESULT%

