param([switch]$Release)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$package = Join-Path $repo "Packages/dev.hisokakaori.hkopti"
$manifestPath = Join-Path $package "package.json"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "package.json not found"
}

$manifestText = [System.IO.File]::ReadAllText(
    $manifestPath, [System.Text.Encoding]::UTF8)
$manifest = $manifestText | ConvertFrom-Json
if ($manifest.name -ne "dev.hisokakaori.hkopti") { throw "Unexpected package ID" }
if ($manifest.displayName -ne "ItiOptimiser") { throw "Unexpected display name" }
if (-not $manifest.version) { throw "Package version is missing" }
if (-not $manifest.vpmDependencies.'com.vrchat.avatars') { throw "VRChat SDK dependency is missing" }
if (-not $manifest.vpmDependencies.'nadena.dev.ndmf') { throw "NDMF dependency is missing" }
if ($manifest.license -ne "MIT") { throw "MIT license metadata is missing" }
if (-not $manifest.licenseUrl) { throw "License URL is missing" }
if (-not $manifest.changelogUrl) { throw "Changelog URL is missing" }

$releaseWorkflow = Join-Path $repo ".github/workflows/release.yml"
$listingWorkflow = Join-Path $repo ".github/workflows/build-listing.yml"
$website = Join-Path $repo "Website/index.html"
if (-not (Test-Path -LiteralPath $releaseWorkflow)) { throw "Release workflow is missing" }
if (-not (Test-Path -LiteralPath $listingWorkflow)) { throw "VPM listing workflow is missing" }
if (-not (Test-Path -LiteralPath $website)) { throw "VPM website template is missing" }
# vrchat-community/package-list-action always reads Website/app.js. Without it the
# listing build fails, GitHub Pages is never deployed, and "Add to VCC" cannot work.
$websiteApp = Join-Path $repo "Website/app.js"
if (-not (Test-Path -LiteralPath $websiteApp)) { throw "VPM website app.js is missing (the listing build requires it)" }
# The README links "Add to VCC" to this page. GitHub strips vcc:// links from
# Markdown, so the README must go through an https page that launches VCC.
$websiteVcc = Join-Path $repo "Website/vcc.html"
if (-not (Test-Path -LiteralPath $websiteVcc)) { throw "Website/vcc.html is missing (the README 'Add to VCC' link points to it)" }
# VCC only understands vcc://vpm/addRepo. "add-repo" is silently ignored.
Get-ChildItem -LiteralPath (Join-Path $repo "Website") -File | Where-Object { $_.Extension -in ".html", ".js" } | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    if ($text -match "vcc://vpm/add-repo") { throw "Website/$($_.Name) uses vcc://vpm/add-repo; VCC requires vcc://vpm/addRepo" }
    # Static pages must hard-code the listing URL. app.js is a Scriban template
    # that fills the URL in at build time, so it is not checked here.
    if ($_.Extension -eq ".html" -and $text -match "vcc://vpm/addRepo" -and
        $text -notmatch "lil2943\.github\.io(/|%2F)ItiOpti(/|%2F)index\.json") {
        throw "Website/$($_.Name) has a VCC link that does not point to the ItiOpti listing"
    }
}

$license = Get-ChildItem -LiteralPath $package -File | Where-Object {
    $_.Name -match '^LICENSE(\..+)?$'
} | Select-Object -First 1
if ($Release -and $null -eq $license) {
    throw "Release check failed: a license file is required before distribution"
}
$rootLicense = Join-Path $repo "LICENSE"
if ($Release -and -not (Test-Path -LiteralPath $rootLicense)) {
    throw "Release check failed: root LICENSE is missing"
}

$missingMeta = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $package -Recurse -Force | Where-Object {
    $_.Name -notlike "*.meta" -and $_.Name -ne "package.json"
} | ForEach-Object {
    if (-not (Test-Path -LiteralPath ($_.FullName + ".meta"))) {
        $missingMeta.Add($_.FullName.Substring($package.Length + 1))
    }
}
if ($missingMeta.Count -gt 0) {
    throw "Missing .meta files:`n$($missingMeta -join "`n")"
}

# A .meta whose asset is gone. Unity warns about these, and the release
# workflow builds the unitypackage from the .meta list, so it can fail on them.
$orphanMeta = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $package -Recurse -Force -Filter "*.meta" | ForEach-Object {
    $assetPath = $_.FullName.Substring(0, $_.FullName.Length - 5)
    if (-not (Test-Path -LiteralPath $assetPath)) {
        $orphanMeta.Add($_.FullName.Substring($package.Length + 1))
    }
}
if ($orphanMeta.Count -gt 0) {
    throw "Orphan .meta files (asset is missing):`n$($orphanMeta -join "`n")"
}

# A folder with no files under it. Git cannot store empty folders, so after a
# clone the folder disappears and its .meta becomes an orphan. This only shows
# up on a fresh checkout (CI, the public repository), never on this machine.
$emptyDirs = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $package -Recurse -Force -Directory | ForEach-Object {
    $files = Get-ChildItem -LiteralPath $_.FullName -Recurse -Force -File |
        Where-Object { $_.Name -notlike "*.meta" }
    if ($null -eq $files -or @($files).Count -eq 0) {
        $emptyDirs.Add($_.FullName.Substring($package.Length + 1))
    }
}
if ($emptyDirs.Count -gt 0) {
    throw "Folders with no files (they vanish in git and leave orphan .meta):`n$($emptyDirs -join "`n")"
}

$guidOwners = @{}
$duplicates = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $package -Recurse -Filter "*.meta" | ForEach-Object {
    $match = Select-String -LiteralPath $_.FullName -Pattern '^guid: ([0-9a-f]{32})$' | Select-Object -First 1
    if ($null -eq $match) { return }
    $guid = $match.Matches[0].Groups[1].Value
    if ($guidOwners.ContainsKey($guid)) {
        $duplicates.Add("$guid : $($guidOwners[$guid]) / $($_.FullName)")
    } else {
        $guidOwners[$guid] = $_.FullName
    }
}
if ($duplicates.Count -gt 0) {
    throw "Duplicate Unity GUIDs:`n$($duplicates -join "`n")"
}

Write-Output "PACKAGE CHECK OK: $($manifest.displayName) $($manifest.version)"
Write-Output "Unity GUIDs: $($guidOwners.Count)"
if ($null -eq $license) {
    Write-Output "PRE-RELEASE: license is intentionally pending"
}
