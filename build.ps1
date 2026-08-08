#!/usr/bin/env pwsh
# ClrKernel developer task runner (Nuke bootstrapper).
# Usage: .\build.ps1 [target] [--flags]    e.g. .\build.ps1 Test   .\build.ps1 --help
[CmdletBinding()]
Param([Parameter(ValueFromRemainingArguments = $true)][string[]] $BuildArguments)
$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK is required but 'dotnet' was not found on PATH. Install it from https://dotnet.microsoft.com/download and retry."
    exit 1
}
dotnet run --project (Join-Path $ScriptDir 'build/_build.csproj') --no-launch-profile -- $BuildArguments
exit $LASTEXITCODE
