# Check whether this machine can run hana-desktop-companion.
# Mirrors the self-check style of hana-window-capture.
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\check-environment.ps1

$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $root 'app\hana-desktop-companion\native\hana-desktop-companion-helper.exe'

$windows = $env:OS -eq 'Windows_NT'
$x64Process = [Environment]::Is64BitProcess

$dotnet8 = $false
$dotnetOutput = & dotnet --list-runtimes 2>$null
foreach ($line in $dotnetOutput) {
    if ($line -match 'Microsoft\.WindowsDesktop\.App 8\.') { $dotnet8 = $true; break }
}

$helperExists = Test-Path -LiteralPath $helper

# Read-only smoke test: enumerate at most one window. No capture, no injection,
# no side effect on any window.
$smokeOk = $false
if ($helperExists -and $windows -and $x64Process) {
    try {
        $out = & $helper 'list-windows' '--limit' '1' 2>$null | Select-Object -Last 1
        if ($out) {
            $parsed = $out | ConvertFrom-Json
            $smokeOk = ($parsed.ok -eq $true)
        }
    } catch { $smokeOk = $false }
}

$result = [ordered]@{
    ok                        = ($windows -and $x64Process -and $dotnet8 -and $helperExists -and $smokeOk)
    windows                   = $windows
    x64Process                = $x64Process
    dotnet8WindowsDesktopRuntime = $dotnet8
    nativeHelperExists        = $helperExists
    nativeHelperSmokeTest     = $smokeOk
}

$result | ConvertTo-Json
