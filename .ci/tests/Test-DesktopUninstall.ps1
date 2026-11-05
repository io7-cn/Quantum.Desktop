$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../../src/Quantum/Platforms/Windows/Uninstall.ps1"

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("quantum-uninstall-tests-" + [guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$stable = Join-Path $testRoot 'stable'
$legacy = Join-Path $testRoot 'legacy'
$external = Join-Path $testRoot 'external'
$script:Choice = 'Keep'
$script:UpdaterFailed = $false
$script:Calls = 0
$script:WasQuiet = $false

function Assert-True($Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Get-QuantumDataPaths { @($stable, $legacy) }
function Get-QuantumInstallation { 'unused-updater.exe' }
function Show-QuantumUninstallChoice {
    if ($script:ForbidPrompt) { throw 'Silent uninstall displayed a prompt.' }
    $script:Choice
}
function Invoke-QuantumUpdater([string]$Updater, [switch]$Quiet) {
    $script:Calls++
    $script:WasQuiet = $Quiet.IsPresent
    Assert-True (Test-Path -LiteralPath "$stable/user.txt") 'Data was deleted before uninstall succeeded.'
    if ($script:UpdaterFailed) { throw 'Simulated updater failure' }
}
function Reset-Data {
    foreach ($path in @($stable, $legacy, $external)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $path 'user.txt') -Value 'saved'
    }
    Set-Content -LiteralPath (Join-Path $stable '.legacy-storage-v1') -Value '1'
    $script:Calls = 0
    $script:ForbidPrompt = $false
    $script:UpdaterFailed = $false
}

try {
    Reset-Data
    Invoke-QuantumUninstall
    Assert-True (Test-Path -LiteralPath "$stable/user.txt") 'Keep lost stable data.'
    Assert-True (Test-Path -LiteralPath "$legacy/user.txt") 'Keep lost legacy data.'
    Assert-True ($script:Calls -eq 1) 'Keep did not invoke updater once.'

    Reset-Data
    $script:Choice = 'Delete'
    $script:ForbidPrompt = $true
    Invoke-QuantumUninstall -Quiet
    Assert-True (Test-Path -LiteralPath "$stable/user.txt") 'Silent uninstall lost data.'
    Assert-True $script:WasQuiet 'Silent mode was not passed to updater.'

    Reset-Data
    $script:Choice = 'Cancel'
    Invoke-QuantumUninstall
    Assert-True ($script:Calls -eq 0) 'Cancel invoked updater.'

    Reset-Data
    $script:Choice = 'Delete'
    $script:UpdaterFailed = $true
    $failed = $false
    try { Invoke-QuantumUninstall } catch { $failed = $true }
    Assert-True $failed 'Updater failure was swallowed.'
    Assert-True (Test-Path -LiteralPath "$stable/user.txt") 'Updater failure deleted data.'

    Reset-Data
    Remove-Item -LiteralPath "$stable/.legacy-storage-v1"
    $failed = $false
    try { Invoke-QuantumUninstall } catch { $failed = $true }
    Assert-True $failed 'Incomplete migration allowed uninstall.'
    Assert-True ($script:Calls -eq 0) 'Incomplete migration invoked updater.'

    Reset-Data
    $link = Join-Path $stable 'linked'
    New-Item -ItemType Junction -Path $link -Target $external | Out-Null
    try {
        $failed = $false
        try { Invoke-QuantumUninstall } catch { $failed = $true }
        Assert-True $failed 'Linked data allowed recursive deletion.'
        Assert-True ($script:Calls -eq 0) 'Linked data invoked updater.'
        Assert-True (Test-Path -LiteralPath "$external/user.txt") 'External linked data was deleted.'
    } finally {
        [IO.Directory]::Delete($link)
    }

    Reset-Data
    Invoke-QuantumUninstall
    Assert-True (!(Test-Path -LiteralPath $stable)) 'Explicit deletion retained stable data.'
    Assert-True (!(Test-Path -LiteralPath $legacy)) 'Explicit deletion retained legacy data for reimport.'
    Assert-True (Test-Path -LiteralPath "$external/user.txt") 'Explicit deletion removed external data.'
    Write-Output 'Passed 7 desktop uninstall scenarios.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -ne $expectedParent -or
        ![IO.Path]::GetFileName($resolved).StartsWith('quantum-uninstall-tests-')) {
        throw "Unexpected cleanup path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
