<#
.SYNOPSIS
    Produit les notes de version a partir de src\MilfTV\Assets\changelog.json (le fichier affiche par l'application) :
    CHANGELOG.md (anglais) et CHANGELOG.<langue>.md a la racine du depot.
    Charge par release.ps1 (". .\build\changelog.ps1") pour composer la page d'une version sur GitHub.

.EXAMPLE
    .\build\changelog.ps1
#>
param([string]$ChangelogDir)

$ChangelogLangs = @('en', 'fr', 'es', 'de', 'it', 'pt')
$ChangelogPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MilfTV\Assets\changelog.json'

function Get-Changelog { Get-Content $ChangelogPath -Raw -Encoding UTF8 | ConvertFrom-Json }

# Valeur dans la langue demandee, sinon en anglais, sinon en francais.
function Get-Localized($obj, [string]$lang) {
    if ($null -eq $obj) { return $null }
    foreach ($l in @($lang, 'en', 'fr')) { if ($null -ne $obj.$l) { return $obj.$l } }
    return $null
}

function Get-ChangelogFileName([string]$lang) { if ($lang -eq 'en') { 'CHANGELOG.md' } else { "CHANGELOG.$lang.md" } }

# Une version en Markdown : resume, puis une rubrique par titre (niveau de titre au choix).
function Format-ChangelogVersion($entry, [string]$lang, [int]$level = 2, [switch]$NoHeading) {
    $cl = Get-Changelog
    $labels = Get-Localized $cl.labels $lang
    $h = '#' * $level
    $lines = New-Object System.Collections.Generic.List[string]
    if (-not $NoHeading) {
        $lines.Add("$h $($entry.version)")
        $lines.Add('')
        $lines.Add("*$($labels.released) $($entry.date)*")
        $lines.Add('')
    }
    $summary = Get-Localized $entry.summary $lang
    if ($summary) { $lines.Add($summary); $lines.Add('') }
    foreach ($section in @(Get-Localized $entry.sections $lang)) {
        if ($section.title) { $lines.Add("$h# $($section.title)"); $lines.Add('') }
        foreach ($item in $section.items) { $lines.Add("- $item") }
        $lines.Add('')
    }
    return $lines
}

# Fichiers CHANGELOG du depot, avec la barre des langues.
function Write-ChangelogFiles([string]$dir) {
    $cl = Get-Changelog
    $bar = ($ChangelogLangs | ForEach-Object { "[$((Get-Localized $cl.labels $_).name)]($(Get-ChangelogFileName $_))" }) -join ' | '
    foreach ($lang in $ChangelogLangs) {
        $labels = Get-Localized $cl.labels $lang
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("# MilfTV - $($labels.title)")
        $lines.Add('')
        $lines.Add($bar)
        $lines.Add('')
        $lines.Add($labels.intro)
        $lines.Add('')
        foreach ($entry in $cl.versions) { foreach ($l in (Format-ChangelogVersion $entry $lang 2)) { $lines.Add($l) } }
        $path = Join-Path $dir (Get-ChangelogFileName $lang)
        [IO.File]::WriteAllText($path, (($lines -join "`n").TrimEnd() + "`n"), (New-Object Text.UTF8Encoding($false)))
        Write-Host "  $path"
    }
}

# Texte de la page d'une version sur GitHub : anglais visible, autres langues repliables.
# $packages : une entree par architecture publiee, @{ Arch = 'x64' | 'arm64'; Setup = '...exe'; Portable = '...zip' }.
function Get-ReleaseBody([string]$version, $packages) {
    $cl = Get-Changelog
    $entry = $cl.versions | Where-Object { $_.version -eq $version } | Select-Object -First 1
    if (-not $entry) { return $null }
    $blocks = foreach ($lang in $ChangelogLangs) {
        $labels = Get-Localized $cl.labels $lang
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("### $($labels.download)")
        $lines.Add('')
        foreach ($p in $packages) {
            if (@($packages).Count -gt 1) {
                $lines.Add("**$($labels.($p.Arch))**")
                $lines.Add('')
            }
            $lines.Add("- **$($p.Setup)** ($($labels.setupKind)): $($labels.setup)")
            $lines.Add("- **$($p.Portable)** ($($labels.portableKind)): $($labels.portable)")
            $lines.Add('')
        }
        $lines.Add("> $($labels.smartscreen)")
        $lines.Add('')
        $lines.Add("### $($labels.changes)")
        $lines.Add('')
        foreach ($l in (Format-ChangelogVersion $entry $lang 3 -NoHeading)) { $lines.Add($l) }
        $text = ($lines -join "`n").TrimEnd()
        if ($lang -eq 'en') { $text }
        else { "<details>`n<summary><b>$($labels.name)</b></summary>`n`n$text`n`n</details>" }
    }
    return ($blocks -join "`n`n") + "`n"
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    if (-not $ChangelogDir) { $ChangelogDir = Split-Path -Parent $PSScriptRoot }
    Write-Host '== Notes de version' -ForegroundColor Cyan
    Write-ChangelogFiles $ChangelogDir
}
