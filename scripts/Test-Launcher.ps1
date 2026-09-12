param([string]$OutputRoot = 'artifacts/launcher')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Runtime.Common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$run = Assert-ChildPath -Root $OutputRoot -Path (Join-Path $OutputRoot ('run-' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$package = Join-Path $run '中文 便携目录 & spaces'
$runtime = Join-Path $package 'app'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$launcher = Join-Path $package 'ShunshouToolbox.exe'
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -OutputPath $launcher -Version '0.3.0'
$compiler = Join-Path $repoRoot '.tools/zig-x86_64-windows-0.15.2/zig.exe'
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $repoRoot '.tools/launcher-build/cache'
& $compiler cc '-target' 'x86_64-windows-gnu' '-Os' '-s' '-municode' '-Werror' '-Wall' '-Wextra' `
    (Join-Path $repoRoot 'tests/Shunshou.Launcher.Tests/fixture.c') '-ladvapi32' '-lkernel32' '-o' (Join-Path $runtime 'Shunshou.App.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native launcher fixture compilation failed.' }
function Start-Fixture([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $run
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}
function Read-String([IO.BinaryReader]$Reader) {
    $length = $Reader.ReadUInt32()
    return [Text.Encoding]::Unicode.GetString($Reader.ReadBytes($length * 2))
}
$checks = [Collections.Generic.List[string]]::new()
$reportPath = Join-Path $run 'arguments.bin'
$arguments = @($reportPath, '850', '73', '', '中文文件 路径.txt', 'spaces and "quotes"', 'C:\trailing slash\', '"', '&|<>^% $(no-shell)', '--verify-package', '--open-uninstaller')
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Fixture $launcher $arguments
try {
    if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw 'Native launcher fixture timed out.' }
    if ($process.ExitCode -ne 73 -or $clock.ElapsedMilliseconds -lt 750) { throw 'Launcher did not wait for and return the child exit code.' }
}
finally { $process.Dispose() }
$checks.Add('Launcher waits for the child and returns its exact exit code')
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($reportPath))
try {
    $count = $reader.ReadUInt32()
    $received = @(); for ($index = 0; $index -lt $count; $index++) { $received += Read-String $reader }
    $working = Read-String $reader
    $elevated = $reader.ReadUInt32()
}
finally { $reader.Dispose() }
if ($received.Count -ne $arguments.Count + 1) { throw 'Argument count changed.' }
for ($index = 0; $index -lt $arguments.Count; $index++) {
    if ($received[$index + 1] -cne $arguments[$index]) { throw "Argument changed at index $index." }
}
if ($working -cne $package -or $received[0] -cne (Join-Path $runtime 'Shunshou.App.exe')) { throw 'Private executable path or working directory is incorrect.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (($elevated -ne 0) -ne $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Child does not inherit the caller elevation state.' }
$checks.Add('Unicode paths, empty arguments, quotes, trailing backslashes and shell metacharacters are forwarded literally')
$checks.Add('Child runs from package root and inherits caller elevation without an additional prompt')
foreach ($case in @('missing', 'invalid')) {
    $caseRoot = Join-Path $run $case
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    $caseLauncher = Join-Path $caseRoot 'ShunshouToolbox.exe'
    Copy-Item -LiteralPath $launcher -Destination $caseLauncher
    if ($case -eq 'invalid') {
        New-Item -ItemType Directory -Path (Join-Path $caseRoot 'app') | Out-Null
        [IO.File]::WriteAllText((Join-Path $caseRoot 'app/Shunshou.App.exe'), 'Not an executable. Test fixture only.')
    }
    $process = Start-Fixture $caseLauncher @()
    try {
        if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw "Launcher $case case timed out." }
        # Windows can classify a deliberately malformed image as BAD_EXE_FORMAT
        # (193) or EXE_MACHINE_TYPE_MISMATCH (216), depending on its loader build.
        $expected = if ($case -eq 'missing') { @(2) } else { @(193, 216) }
        if ($process.ExitCode -notin $expected) { throw "Unexpected $case child error code: $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}
$checks.Add('Missing or invalid private executable produces the Windows error code in hidden automation')
& (Join-Path $PSScriptRoot 'Inspect-NativeDependencies.ps1') -Directory $package -ReportPath (Join-Path $run 'native-dependencies.json')
$imports = Get-Content -LiteralPath (Join-Path $run 'native-dependencies.json') -Raw | ConvertFrom-Json
if (@($imports.Imports | Where-Object { $_.File -eq 'ShunshouToolbox.exe' -and $_.Dependency -match 'hostfxr|coreclr|vcruntime|msvcp' }).Count -ne 0) { throw 'Root launcher unexpectedly depends on a separately installed runtime.' }
$checks.Add('Root launcher imports Windows system DLLs and no .NET or Visual C++ redistributable runtime')
[ordered]@{ Passed = $true; Cases = $checks.ToArray(); FixtureDirectory = $run; LauncherBytes = (Get-Item -LiteralPath $launcher).Length } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'verification.json') -Encoding utf8
Write-Host "Native launcher verification passed: $($checks.Count) checks. Fixtures: $run"
