$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Split-Path $PSScriptRoot -Parent)
$previousCoreOnly = $env:QuantumCoreOnly
$previousConfiguration = $env:Configuration
try {
    $pins = Get-Content .ci/plugin-compatibility-pins.json -Raw | ConvertFrom-Json
    foreach ($property in $pins.PSObject.Properties) {
        $name = $property.Name
        $pin = $property.Value
        if ($pin.revision -notmatch '^[0-9a-f]{40}$' -or $pin.repository -notmatch '^io7-cn/Quantum\.Plugin\.[A-Za-z]+$') { throw 'Invalid plugin compatibility pin.' }
        $target = ".ci-dependencies/Quantum.Plugin.$name"
        if (-not (Test-Path "$target/.git")) {
            git init $target
            git -C $target fetch --depth 1 "https://github.com/$($pin.repository).git" $pin.revision
            git -C $target checkout --detach $pin.revision
        }
        if ((git -C $target rev-parse HEAD) -ne $pin.revision -or (git -C $target status --porcelain)) { throw 'Preserve the changed or mismatched plugin checkout for inspection.' }
        dotnet restore "$target/src/Quantum.$($name)Plugin/Quantum.$($name)Plugin.csproj"
        dotnet build "$target/src/Quantum.$($name)Plugin/Quantum.$($name)Plugin.csproj" -c Release --no-restore
    }
    $env:QuantumCoreOnly = 'true'
    $env:Configuration = 'Release'
    $project = 'tests/Quantum.HostIntegration.Tests/Quantum.HostIntegration.Tests.csproj'
    dotnet restore $project
    dotnet format $project --verify-no-changes --no-restore --include tests/Quantum.HostIntegration.Tests
    dotnet test $project -c Release --no-restore
} finally {
    $env:QuantumCoreOnly = $previousCoreOnly
    $env:Configuration = $previousConfiguration
    Pop-Location
}
