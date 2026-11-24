[CmdletBinding()]
param([string]$Screenshot, [switch]$LaunchCheck)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$tempBuild = Join-Path ([IO.Path]::GetTempPath()) ('tanva-test-build-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempBuild)
try {
    $exe = & (Join-Path $project 'build.ps1') -OutputDirectory $tempBuild
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    if (-not (Test-Path -LiteralPath $framework)) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
    $flags = @('/nologo','/codepage:65001','/target:exe',"/out:$tempBuild\Checks.exe","/reference:$exe",'/reference:System.Security.dll','/reference:System.Management.dll','/reference:System.Xaml.dll')
    foreach ($name in 'WindowsBase','PresentationCore','PresentationFramework') { $flags += "/reference:$framework\WPF\$name.dll" }
    & (Join-Path $framework 'csc.exe') @flags (Join-Path $PSScriptRoot 'Checks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Check compilation failed.' }
    $testArgs = @($Screenshot)
    if ($LaunchCheck) { $testArgs += '--launch-check' }
    & (Join-Path $tempBuild 'Checks.exe') @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
} finally {
    foreach ($name in 'Checks.exe','tanva-rdp.exe') {
        $target = Join-Path $tempBuild $name
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    [IO.Directory]::Delete($tempBuild)
}
