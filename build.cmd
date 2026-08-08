@echo off
:: ClrKernel developer task runner (Nuke bootstrapper).
:: Usage: build.cmd [target] [--flags]    e.g. build.cmd Test   build.cmd --help
where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK is required but 'dotnet' was not found on PATH.
  echo Install it from https://dotnet.microsoft.com/download and retry.
  exit /b 1
)
dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
