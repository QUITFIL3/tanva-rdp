[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $framework)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$output = Join-Path (Resolve-Path -LiteralPath $OutputDirectory) 'tanva-rdp.exe'
Push-Location $PSScriptRoot
try {
    $flags = @('/nologo', '/utf8output', '/codepage:65001', '/optimize+', '/target:winexe', '/platform:anycpu', "/out:$output", '/win32manifest:app.manifest', '/win32icon:tanva.ico',
        '/resource:Main.xaml,Main.xaml', '/resource:Theme.xaml,Theme.xaml',
        '/reference:System.Security.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Xaml.dll')
    foreach ($name in 'WindowsBase','PresentationCore','PresentationFramework') {
        $flags += "/reference:$framework\WPF\$name.dll"
    }
    & $compiler @flags 'App.cs' 'Profiles.cs' 'Backup.cs' 'Dialogs.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
    Write-Output $output
} finally { Pop-Location }
