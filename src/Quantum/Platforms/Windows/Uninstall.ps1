param([switch]$Silent)

$ErrorActionPreference = 'Stop'

function Get-QuantumDataPaths {
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    @(
        [IO.Path]::Combine($localData, 'io7-cn', 'io.quantum.desktop')
        [IO.Path]::Combine($localData, 'Quantum', 'io.quantum.desktop')
    )
}

function Assert-QuantumPathWithoutLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to follow a symbolic link or junction: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Assert-QuantumDataTree([string]$Path) {
    Assert-QuantumPathWithoutLinks $Path
    if (Test-Path -LiteralPath $Path) {
        foreach ($entry in Get-ChildItem -LiteralPath $Path -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to delete a symbolic link or junction: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) {
                Assert-QuantumDataTree $entry.FullName
            }
        }
    }
}

function Get-QuantumInstallation {
    $root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Quantum'
    $registered = Get-ItemProperty -LiteralPath $key
    if (![string]::Equals($root, [IO.Path]::GetFullPath($registered.InstallLocation), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The uninstall registration does not match this Quantum installation.'
    }
    $updater = Join-Path $root 'Update.exe'
    Assert-QuantumPathWithoutLinks $updater
    if (!(Test-Path -LiteralPath $updater -PathType Leaf)) {
        throw 'Quantum Update.exe is missing. Local data has been retained.'
    }
    return $updater
}

function Show-QuantumUninstallChoice([string]$DataPath) {
    Add-Type -AssemblyName System.Windows.Forms
    $chinese = [Globalization.CultureInfo]::CurrentUICulture.Name.StartsWith('zh')
    # JSON escapes keep this UTF-8 script ASCII-compatible with Windows PowerShell 5.1.
    $text = if ($chinese) {
        (ConvertFrom-Json '"\u5378\u8f7d Quantum \u65f6\u662f\u5426\u4fdd\u7559\u672c\u5730\u6570\u636e\uff1f\n\n\u662f\uff1a\u4fdd\u7559\u6570\u636e\u5e93\u3001\u63d2\u4ef6\u3001\u8bbe\u7f6e\u548c\u767b\u5f55\u72b6\u6001\uff0c\u91cd\u88c5\u540e\u53ef\u7ee7\u7eed\u4f7f\u7528\uff08\u63a8\u8350\uff09\u3002\n\u5426\uff1a\u5220\u9664\u8fd9\u4e9b\u672c\u5730\u6570\u636e\u3002\n\u53d6\u6d88\uff1a\u9000\u51fa\u5378\u8f7d\u3002\n\n\u6570\u636e\u76ee\u5f55\uff1a{0}"') -f $DataPath
    } else {
        "Keep local data when uninstalling Quantum?`n`nYes: Keep databases, plugins, settings and sign-in state for reinstallation (recommended).`nNo: Delete this local data.`nCancel: Exit without uninstalling.`n`nData directory: $DataPath"
    }
    $choice = [Windows.Forms.MessageBox]::Show($text, 'Quantum', 'YesNoCancel', 'Question', 'Button1')
    if ($choice -eq 'Cancel') { return 'Cancel' }
    if ($choice -eq 'Yes') { return 'Keep' }
    $warning = if ($chinese) {
        ConvertFrom-Json '"\u786e\u5b9a\u6c38\u4e45\u5220\u9664 Quantum \u7684\u5168\u90e8\u672c\u5730\u6570\u636e\u5e93\u3001\u63d2\u4ef6\u3001\u8bbe\u7f6e\u548c\u767b\u5f55\u72b6\u6001\u5417\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002\u81ea\u5b9a\u4e49\u5916\u90e8\u63d2\u4ef6\u76ee\u5f55\u4e0d\u4f1a\u88ab\u5220\u9664\u3002"'
    } else {
        'Permanently delete all Quantum local databases, plugins, settings and sign-in state? This cannot be undone. Custom external plugin directories will be retained.'
    }
    $confirmed = [Windows.Forms.MessageBox]::Show($warning, 'Quantum', 'YesNo', 'Warning', 'Button2')
    if ($confirmed -eq 'Yes') { return 'Delete' }
    return 'Cancel'
}

function Invoke-QuantumUpdater([string]$Updater, [switch]$Quiet) {
    $arguments = @('--uninstall')
    if ($Quiet) { $arguments += '--silent' }
    # PowerShell lives outside the installation. Velopack stops all executables inside it.
    $process = Start-Process -FilePath $Updater -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(600000)) {
        throw 'Uninstall timed out. Local data has been retained.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Uninstall failed ($($process.ExitCode)). Local data has been retained."
    }
}

function Invoke-QuantumUninstall([switch]$Quiet) {
    $updater = Get-QuantumInstallation
    $dataPaths = @(Get-QuantumDataPaths)
    foreach ($path in $dataPaths) { Assert-QuantumPathWithoutLinks $path }
    $choice = if ($Quiet) { 'Keep' } else { Show-QuantumUninstallChoice $dataPaths[0] }
    if ($choice -eq 'Cancel') { return }
    $marker = Join-Path $dataPaths[0] '.legacy-storage-v1'
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) {
        throw 'Start Quantum once to finish migrating local data before uninstalling. Back up legacy data first if startup reports a conflict.'
    }
    if ($choice -eq 'Delete') {
        foreach ($path in $dataPaths) { Assert-QuantumDataTree $path }
    }
    Invoke-QuantumUpdater $updater -Quiet:$Quiet
    if ($choice -eq 'Delete') {
        foreach ($path in $dataPaths) {
            # Fixed app-owned paths only; never QUANTUM_MODULES_PATH or an installation parent.
            Assert-QuantumDataTree $path
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-QuantumUninstall -Quiet:$Silent
    } catch {
        if (!$Silent) {
            Add-Type -AssemblyName System.Windows.Forms
            [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Quantum', 'OK', 'Error') | Out-Null
        }
        Write-Error -ErrorRecord $_ -ErrorAction Continue
        exit 1
    }
}
