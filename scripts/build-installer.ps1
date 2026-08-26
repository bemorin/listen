param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$release = Join-Path $Root 'release'
$setupName = 'Listen-Setup-{0}.exe' -f $Version
$target = Join-Path $release $setupName

function InstallerLog([string]$message) {
    if ($LogPath) {
        Add-Content -LiteralPath $LogPath -Value ('[{0}] Installer: {1}' -f (Get-Date -Format 'HH:mm:ss'), $message) -Encoding UTF8
    }
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path $csc)) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $csc = Join-Path $frameworkRoot 'csc.exe'
}
if (-not (Test-Path $csc)) { throw 'Le compilateur .NET Framework de Windows (csc.exe) est introuvable pour le Setup.' }

$system = Join-Path $frameworkRoot 'System.dll'
$systemCore = Join-Path $frameworkRoot 'System.Core.dll'
$winForms = Join-Path $frameworkRoot 'System.Windows.Forms.dll'
foreach ($required in @($system, $systemCore, $winForms)) {
    if (-not (Test-Path $required)) { throw ('Reference .NET introuvable pour le Setup : ' + $required) }
}

$listenExe = Join-Path $release 'Listen.exe'
$license = Join-Path $Root 'LICENSE.md'
$readme = Join-Path $Root 'README.md'
$changelog = Join-Path $Root 'CHANGELOG.md'
$bootstrap = Join-Path $Root 'scripts\InstallerBootstrap.cs'
$uninstall = Join-Path $Root 'scripts\uninstall.cmd'
$icon = Join-Path $Root 'build\icon.ico'
foreach ($required in @($listenExe, $license, $readme, $changelog, $bootstrap, $uninstall, $icon)) {
    if (-not (Test-Path $required)) { throw ('Fichier requis pour le Setup introuvable : ' + $required) }
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ListenSetupBuild_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

try {
    $installTemplate = Get-Content -LiteralPath (Join-Path $Root 'scripts\install.cmd') -Raw
    $installContent = $installTemplate.Replace('__LISTEN_VERSION__', $Version)
    $install = Join-Path $workRoot 'install.cmd'
    Set-Content -LiteralPath $install -Value $installContent -Encoding ASCII

    $versionSource = Join-Path $workRoot '_SetupVersion.cs'
    $assemblyVersion = $Version + '.0'
    @"
using System.Reflection;
[assembly: AssemblyTitle("Listen Setup")]
[assembly: AssemblyProduct("Listen")]
[assembly: AssemblyCompany("Minimalist Desktop Apps")]
[assembly: AssemblyDescription("Minimal audio player for Windows")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
"@ | Set-Content -LiteralPath $versionSource -Encoding UTF8

    if (Test-Path $target) { Remove-Item -LiteralPath $target -Force }

    $arguments = @(
        '/nologo',
        '/noconfig',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/debug-',
        '/codepage:65001',
        ('/win32icon:{0}' -f $icon),
        ('/out:{0}' -f $target),
        ('/reference:{0}' -f $system),
        ('/reference:{0}' -f $systemCore),
        ('/reference:{0}' -f $winForms),
        ('/resource:{0},Listen.Payload.Listen.exe' -f $listenExe),
        ('/resource:{0},Listen.Payload.LICENSE.md' -f $license),
        ('/resource:{0},Listen.Payload.README.md' -f $readme),
        ('/resource:{0},Listen.Payload.CHANGELOG.md' -f $changelog),
        ('/resource:{0},Listen.Payload.install.cmd' -f $install),
        ('/resource:{0},Listen.Payload.uninstall.cmd' -f $uninstall),
        $bootstrap,
        $versionSource
    )

    InstallerLog ('Compilateur Setup : ' + $csc)
    InstallerLog 'Packaging : bootstrap .NET natif avec payload embarque (sans IExpress).'

    $compilerOutput = & $csc @arguments 2>&1
    if ($LogPath -and $compilerOutput) { $compilerOutput | Add-Content -LiteralPath $LogPath -Encoding UTF8 }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target)) {
        throw 'La compilation du Setup a echoue. Voir build-windows.log.'
    }

    $setupSizeKb = [Math]::Round((Get-Item -LiteralPath $target).Length / 1KB, 1)
    InstallerLog ('Setup cree : {0} ({1} KB)' -f $target, $setupSizeKb)
    Write-Host ('Installeur prêt : ' + $target)
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
