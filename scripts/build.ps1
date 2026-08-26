param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'release'
$log = Join-Path $root 'build-windows.log'
$versionFile = Join-Path $root 'VERSION'
if (-not (Test-Path $versionFile)) { throw 'VERSION est introuvable.' }
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw ('Version invalide dans VERSION : ' + $version) }

function Log([string]$message) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $message)
    Write-Host $line
    Add-Content -LiteralPath $log -Value $line -Encoding UTF8
}

try {
    Set-Content -LiteralPath $log -Value ('Listen Windows build - {0}' -f (Get-Date)) -Encoding UTF8
    if (Test-Path $release) { Remove-Item $release -Recurse -Force }
    New-Item -ItemType Directory -Path $release | Out-Null

    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $csc = Join-Path $frameworkRoot 'csc.exe'
    if (-not (Test-Path $csc)) {
        $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
        $csc = Join-Path $frameworkRoot 'csc.exe'
    }
    if (-not (Test-Path $csc)) {
        throw 'Le compilateur .NET Framework de Windows (csc.exe) est introuvable.'
    }

    # Load only the WPF assemblies so we can resolve their exact installed paths.
    # IMPORTANT: do not load/pass System.Drawing from the GAC while also using
    # csc.exe's default response file: that imports a second System.Drawing.dll
    # with the same identity and triggers CS1703 on Windows 10/11.
    Add-Type -AssemblyName PresentationFramework
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName System.Xaml

    $presentationFramework = [System.Windows.Window].Assembly.Location
    $presentationCore = [System.Windows.Media.MediaPlayer].Assembly.Location
    $windowsBase = [System.Windows.DependencyObject].Assembly.Location
    $systemXaml = [System.Xaml.XamlReader].Assembly.Location

    # Compile with /noconfig and an explicit, single set of framework references.
    # This avoids duplicate GAC/framework references regardless of the machine's
    # csc.rsp contents.
    $system = Join-Path $frameworkRoot 'System.dll'
    $systemCore = Join-Path $frameworkRoot 'System.Core.dll'
    $systemDrawing = Join-Path $frameworkRoot 'System.Drawing.dll'
    foreach ($required in @($system, $systemCore, $systemDrawing, $presentationFramework, $presentationCore, $windowsBase, $systemXaml)) {
        if (-not (Test-Path $required)) { throw ('Référence .NET introuvable : ' + $required) }
    }

    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { throw 'Aucun fichier C# trouvé dans src\.' }

    # VERSION is the single release-version source of truth. Assembly version metadata
    # is generated for this build so source files and installer scripts cannot drift.
    $generatedVersionSource = Join-Path $release '_VersionInfo.cs'
    $assemblyVersion = $version + '.0'
    $versionSource = @"
using System.Reflection;
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
"@
    Set-Content -LiteralPath $generatedVersionSource -Value $versionSource -Encoding UTF8
    $sources += $generatedVersionSource

    $outputExe = Join-Path $release 'Listen.exe'
    $icon = Join-Path $root 'build\icon.ico'
    if (-not (Test-Path $icon)) { throw 'build\icon.ico est introuvable.' }
    $manifestTemplate = Join-Path $root 'build\app.manifest'
    if (-not (Test-Path $manifestTemplate)) { throw 'build\app.manifest est introuvable.' }
    $manifest = Join-Path $release '_app.manifest'
    (Get-Content -LiteralPath $manifestTemplate -Raw).Replace('__LISTEN_ASSEMBLY_VERSION__', $assemblyVersion) | Set-Content -LiteralPath $manifest -Encoding UTF8

    Log 'Compilation de Listen.exe (WPF/.NET Framework, sans Electron ni runtime embarqué)…'
    $arguments = @(
        '/nologo',
        '/noconfig',
        '/target:winexe',
        '/platform:anycpu',
        '/optimize+',
        '/debug-',
        '/codepage:65001',
        ('/win32icon:{0}' -f $icon),
        ('/win32manifest:{0}' -f $manifest),
        ('/out:{0}' -f $outputExe),
        ('/reference:{0}' -f $system),
        ('/reference:{0}' -f $systemCore),
        ('/reference:{0}' -f $systemDrawing),
        ('/reference:{0}' -f $windowsBase),
        ('/reference:{0}' -f $presentationCore),
        ('/reference:{0}' -f $presentationFramework),
        ('/reference:{0}' -f $systemXaml)
    ) + $sources

    Log ('Compilateur : ' + $csc)
    Log ('System.Drawing : ' + $systemDrawing)

    $compilerOutput = & $csc @arguments 2>&1
    $compilerOutput | Add-Content -LiteralPath $log -Encoding UTF8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outputExe)) {
        throw 'La compilation a échoué. Voir build-windows.log.'
    }
    Remove-Item -LiteralPath $generatedVersionSource -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $manifest -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath (Join-Path $root 'LICENSE.md') -Destination (Join-Path $release 'LICENSE.md')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $release 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination (Join-Path $release 'CHANGELOG.md')

    $portableDir = Join-Path $release 'Listen-Portable'
    New-Item -ItemType Directory -Path $portableDir | Out-Null
    Copy-Item -LiteralPath $outputExe -Destination (Join-Path $portableDir 'Listen.exe')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE.md') -Destination (Join-Path $portableDir 'LICENSE.md')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $portableDir 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination (Join-Path $portableDir 'CHANGELOG.md')
    Copy-Item -LiteralPath (Join-Path $root 'scripts\unregister-portable.cmd') -Destination (Join-Path $portableDir 'Unregister-Listen.cmd')

    $portableZip = Join-Path $release ('Listen-Portable-{0}.zip' -f $version)
    Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $portableZip -Force
    Log ('Portable prêt : {0}' -f $portableZip)

    if (-not $SkipInstaller) {
        Log "Création de l'installeur Windows…"
        & (Join-Path $PSScriptRoot 'build-installer.ps1') -Root $root -Version $version -LogPath $log
        $setup = Join-Path $release ('Listen-Setup-{0}.exe' -f $version)
        if (-not (Test-Path $setup)) {
            throw "Le build Release est incomplet : Listen-Setup n'a pas été généré."
        }
        Log ('Setup prêt : {0}' -f $setup)
    }

    $sizeKb = [Math]::Round((Get-Item $outputExe).Length / 1KB, 1)
    $sha256 = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash
    Log ('Terminé. Listen.exe : {0} KB' -f $sizeKb)
    Log ('SHA-256 Listen.exe : ' + $sha256)
    Write-Host ''
    Write-Host 'OK — release\ contient le portable et le setup prêts à tester.' -ForegroundColor Green
    Start-Process explorer.exe $release
    exit 0
}
catch {
    $message = $_.Exception.Message
    Add-Content -LiteralPath $log -Value ('ERROR: ' + $message) -Encoding UTF8
    Write-Host ''
    Write-Host ('ERREUR : ' + $message) -ForegroundColor Red
    Write-Host ('Log : ' + $log)
    exit 1
}
