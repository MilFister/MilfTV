<#
.SYNOPSIS
    Construit MilfTV et son programme d'installation (Velopack) dans artifacts\releases :
    MilfTV.App-win-Setup.exe (installateur), MilfTV.App-win-Portable.zip (version portable)
    et les paquets de mise a jour (.nupkg, releases.win.json).

.EXAMPLE
    .\build\release.ps1 -UpdateRepo https://github.com/<compte>/MilfTV
    Construit la version du .csproj ; les copies installees chercheront leurs mises a jour sur ce depot.

.EXAMPLE
    $env:GITHUB_TOKEN = '<jeton>'; .\build\release.ps1 -UpdateRepo https://github.com/<compte>/MilfTV -Upload
    Construit puis publie la version sur GitHub (Releases), ou les copies installees la trouveront.

.EXAMPLE
    .\build\release.ps1 -UpdateRepo https://github.com/<compte>/MilfTV -Version 1.9.0-beta.1 -Channel beta
    Construit une version beta (canal beta, publiee comme pre-version GitHub avec -Upload).
    Les copies reglees sur Beta la recoivent ; les autres restent sur les versions stables.

.EXAMPLE
    .\build\release.ps1 -UpdateRepo ... -SignParams '/n "Nom du certificat" /fd sha256 /tr http://time.certum.pl /td sha256'
    Signe l'executable et l'installateur avec un certificat installe (signtool).
#>
param(
    # Version a construire (par defaut : <Version> du .csproj). Stable : x.y.z ; beta : x.y.z-beta.n
    # (x.y.z = la future version stable, qui sera donc plus recente que ses betas).
    [string]$Version,
    # Canal : stable (versions publiees) ou beta (nouveautes en avance).
    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',
    # Architecture : x64 (par defaut) ou arm64 (PC Windows sur ARM : Snapdragon...). Les copies ARM64 ont leurs propres
    # canaux de mise a jour (win-arm64, beta-arm64) et leurs fichiers (MilfTV.App-beta-arm64-Setup.exe...).
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    # Architectures dont les fichiers sont listes sur la page de la version (on publie x64, puis arm64 qui s'y ajoute).
    [string[]]$ReleaseArch = @('x64', 'arm64'),
    # Ajoute les fichiers a une version GitHub deja publiee (automatique pour arm64).
    [switch]$Merge,
    # Depot GitHub des versions : active les mises a jour automatiques des copies installees.
    [string]$UpdateRepo = "",
    # Identifiant d'installation. Ne pas utiliser "MilfTV" : l'application s'installerait dans
    # %LOCALAPPDATA%\MilfTV, le dossier de ses donnees, que la desinstallation effacerait.
    [string]$PackId = "MilfTV.App",
    [string]$Title = "MilfTV",
    [string]$Authors = "MilfTV",
    # Raccourcis crees a l'installation : Desktop, StartMenuRoot, StartMenu, Startup (separes par des virgules).
    # Le bureau n'est pas dans la liste : MilfTV propose ce raccourci au premier lancement (Core/DesktopShortcut.cs).
    [string]$Shortcuts = "StartMenuRoot",
    # Signature (facultative) : parametres signtool, ou fichier metadata.json d'Azure Artifact Signing.
    [string]$SignParams = $env:MILFTV_SIGN_PARAMS,
    [string]$AzureSignFile = $env:MILFTV_AZURE_SIGN_FILE,
    [string]$OutDir,
    # Publie la version sur GitHub (Releases) ; necessite $env:GITHUB_TOKEN (droit contents:write).
    [switch]$Upload,
    # Branche dont la version porte l'etiquette (tag) : par defaut Beta pour une beta, main pour une stable.
    [string]$Branch,
    # Garde les outils de test (scenarios MILFTV_DEV) dans la publication : pour les tests locaux seulement.
    [switch]$DevTools
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\MilfTV\MilfTV.csproj'
if (-not $Version) { $Version = (Select-Xml -Path $proj -XPath '//Version').Node.InnerText }
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts\releases' }
$rid = "win-$Arch"
$pub = if ($Arch -eq 'x64') { Join-Path $root "artifacts\publish\$Version" } else { Join-Path $root "artifacts\publish\$Version-$Arch" }
# Canal Velopack : 'win' (canal par defaut sous Windows) pour les versions stables, 'beta' pour les betas.
$baseChannel = if ($Channel -eq 'beta') { 'beta' } else { 'win' }
$vpkChannel = if ($Arch -eq 'x64') { $baseChannel } else { "$baseChannel-$Arch" }
if (-not $Branch) { $Branch = if ($Channel -eq 'beta') { 'Beta' } else { 'main' } }
if ($Channel -eq 'beta' -and $Version -notmatch '-') { throw "Une version beta doit avoir un suffixe de pre-version, par exemple 1.9.0-beta.1." }
if ($Channel -eq 'stable' -and $Version -match '-') { throw "Une version stable n'a pas de suffixe ($Version) : utilisez -Channel beta." }

$nativeDir = if ($Arch -eq 'x64') { 'native' } else { "native\$Arch" }
foreach ($f in 'libmpv-2.dll', 'vulkan-1.dll') {
    if (-not (Test-Path (Join-Path $root "$nativeDir\$f"))) { throw "$nativeDir\$f est absent : voir la section Build du README." }
}
if (-not $UpdateRepo) { Write-Warning "Sans -UpdateRepo, cette version ne cherchera pas de mises a jour." }
if ($Upload -and -not $env:GITHUB_TOKEN) { throw "-Upload : definissez d'abord `$env:GITHUB_TOKEN (jeton GitHub avec le droit contents:write)." }

Write-Host "== Publication de MilfTV $Version ($Arch)" -ForegroundColor Cyan
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
# Outils de test retires des versions publiees, sauf demande explicite (-DevTools) pour tester une publication.
if ($DevTools -and $Upload) { throw "-DevTools et -Upload ensemble : une version publiee ne doit pas contenir les outils de test." }
$devToolsValue = if ($DevTools) { 'true' } else { 'false' }
dotnet publish $proj -c Release -r $rid -o $pub "-p:Version=$Version" "-p:UpdateRepo=$UpdateRepo" "-p:DevTools=$devToolsValue" -nologo
if ($LASTEXITCODE) { throw "dotnet publish a echoue." }

Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE) { throw "dotnet tool restore a echoue (outil vpk)." }

    # Version precedente : permet de produire un paquet delta (mise a jour bien plus legere).
    if ($UpdateRepo -like 'https://github.com/*') {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $dl = @('vpk', 'download', 'github', '--repoUrl', $UpdateRepo, '-o', $OutDir, '-c', $vpkChannel)
        if ($Channel -eq 'beta') { $dl += '--pre' }
        dotnet @dl 2>&1 | Out-Null
        $ErrorActionPreference = $previous
    }

    Write-Host "== Installateur et paquets de mise a jour" -ForegroundColor Cyan
    $pack = @('vpk', 'pack', '-u', $PackId, '-v', $Version, '-c', $vpkChannel, '-r', $rid, '-p', $pub, '-e', 'MilfTV.exe', '-o', $OutDir,
        '--packTitle', $Title, '--packAuthors', $Authors,
        '--icon', (Join-Path $root 'src\MilfTV\Assets\milftv.ico'))
    # Aucun raccourci : -Shortcuts None (ou vide).
    if (-not $Shortcuts) { $Shortcuts = 'None' }
    $pack += @('--shortcuts', $Shortcuts)
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    if ($AzureSignFile) { $pack += @('--azureTrustedSignFile', $AzureSignFile) }
    # Notes de la version, tirees de Assets\changelog.json (le fichier que l'application affiche) : anglais, puis les
    # autres langues repliables, avec les fichiers a telecharger. Jointes au paquet et a la page GitHub de la version.
    . (Join-Path $PSScriptRoot 'changelog.ps1')
    $packages = foreach ($a in $ReleaseArch) {
        $ch = if ($a -eq 'x64') { $baseChannel } else { "$baseChannel-$a" }
        @{ Arch = $a; Setup = "$PackId-$ch-Setup.exe"; Portable = "$PackId-$ch-Portable.zip" }
    }
    $body = Get-ReleaseBody $Version $packages
    if ($body) {
        $notes = Join-Path $root "artifacts\notes-$Version.md"
        [IO.File]::WriteAllText($notes, $body, (New-Object Text.UTF8Encoding($false)))
        $pack += @('--releaseNotes', $notes)
    }
    else {
        Write-Warning "Pas de notes pour $Version dans Assets\changelog.json : ajoutez-les, elles s'affichent dans l'application."
    }
    dotnet @pack
    if ($LASTEXITCODE) { throw "vpk pack a echoue." }

    if ($Upload) {
        Write-Host "== Publication sur GitHub" -ForegroundColor Cyan
        $up = @('vpk', 'upload', 'github', '--repoUrl', $UpdateRepo, '--token', $env:GITHUB_TOKEN, '-o', $OutDir, '-c', $vpkChannel,
            '--publish', '--releaseName', "MilfTV $Version", '--tag', "v$Version", '--targetCommitish', $Branch)
        # Les betas sont des pre-versions GitHub : seules les copies reglees sur Beta les lisent.
        if ($Channel -eq 'beta') { $up += '--pre' }
        # ARM64 : ses fichiers rejoignent la version deja publiee pour x64 (meme etiquette, meme page).
        if ($Merge -or $Arch -ne 'x64') { $up += '--merge' }
        dotnet @up
        if ($LASTEXITCODE) { throw "vpk upload a echoue." }
    }
}
finally {
    Pop-Location
}
Write-Host "== Termine : $OutDir" -ForegroundColor Green
