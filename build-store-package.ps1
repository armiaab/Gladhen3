<#
.SYNOPSIS
  Builds and verifies the Microsoft Store package (.msixupload) for Gladhen3.

.DESCRIPTION
  1. Bumps the version in Package.appxmanifest (third segment) unless -Version or -KeepVersion is given.
  2. Builds ShellExtension.dll (Release x86 + x64), then the app in StoreUpload mode as an x86|x64 bundle (unsigned - Microsoft signs after certification).
  3. Opens the produced .msixupload and verifies every package inside matches the manifest identity (catches the dev-cert/sideload trap that Partner Center rejects with "Invalid package family name / publisher name").
  4. Prints the exact file to upload to Partner Center.

.EXAMPLE
  .\build-store-package.ps1                  # auto-bump: 1.0.9.0 -> 1.0.10.0
  .\build-store-package.ps1 -Version 1.1.0.0 # set version explicitly
  .\build-store-package.ps1 -KeepVersion     # rebuild the current version (e.g. after a failed build)
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$KeepVersion
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$manifestPath = Join-Path $repo "Package.appxmanifest"
$startTime = Get-Date

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "FAILED: $msg" -ForegroundColor Red
    exit 1
}

# Family-name suffix exactly as Windows computes it:
# SHA-256 of the UTF-16LE publisher string, first 8 bytes + one 0 bit,
# encoded as 13 chars of Crockford base32.
function Get-FamilySuffix([string]$publisher) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = $sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($publisher))
    $sha.Dispose()
    $bits = ""
    foreach ($b in $hash[0..7]) { $bits += [Convert]::ToString($b, 2).PadLeft(8, "0") }
    $bits += "0"
    $alphabet = "0123456789abcdefghjkmnpqrstvwxyz"
    $out = ""
    for ($i = 0; $i -lt 65; $i += 5) { $out += $alphabet[[Convert]::ToInt32($bits.Substring($i, 5), 2)] }
    return $out
}

# ---------- locate MSBuild ----------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Fail "vswhere.exe not found - is Visual Studio installed?" }
$msb = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msb) { Fail "MSBuild not found via vswhere" }
Write-Host "MSBuild: $msb"

# ---------- version handling ----------
if (-not (Test-Path $manifestPath)) { Fail "Package.appxmanifest not found at $manifestPath" }
$raw = [IO.File]::ReadAllText($manifestPath)
$identityMatch = [regex]::Match($raw, '(?s)<Identity\b.*?Version="([0-9\.]+)"')
if (-not $identityMatch.Success) { Fail "could not find Identity Version in Package.appxmanifest" }
$currentVersion = $identityMatch.Groups[1].Value

if ($KeepVersion -and $Version) { Fail "use either -Version or -KeepVersion, not both" }
if ($KeepVersion) {
    $newVersion = $currentVersion
} elseif ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { Fail "-Version must look like 1.2.3.0 (the Store requires the last part to be 0)" }
    $newVersion = $Version
} else {
    $parts = $currentVersion.Split(".")
    if ($parts.Count -ne 4) { Fail "current version '$currentVersion' is not in a.b.c.d form" }
    $parts[2] = [string]([int]$parts[2] + 1)
    $newVersion = $parts -join "."
}

if ($newVersion -ne $currentVersion) {
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $updated = [regex]::Replace($raw, '(?s)(<Identity\b.*?Version=")[0-9\.]+(")', ('${1}' + $newVersion + '${2}'))
    $enc = New-Object Text.UTF8Encoding($hasBom)
    [IO.File]::WriteAllText($manifestPath, $updated, $enc)
    Write-Host "Version: $currentVersion -> $newVersion  (Package.appxmanifest updated - remember to commit it)" -ForegroundColor Cyan
} else {
    Write-Host "Version: $newVersion (unchanged)" -ForegroundColor Cyan
}

# ---------- expected identity (from the manifest = source of truth) ----------
[xml]$manifestXml = [IO.File]::ReadAllText($manifestPath)
$expectedName = $manifestXml.Package.Identity.Name
$expectedPublisher = $manifestXml.Package.Identity.Publisher
$expectedFamily = "{0}_{1}" -f $expectedName, (Get-FamilySuffix $expectedPublisher)
if ($expectedPublisher -notmatch '^CN=[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}$') {
    Write-Host "WARNING: manifest publisher '$expectedPublisher' is not a Store GUID identity (CN=<guid>)." -ForegroundColor Yellow
    Write-Host "         Partner Center will reject this. Restore it from Package.StoreAssociation.xml." -ForegroundColor Yellow
}

# ---------- builds ----------
$upload = Join-Path $repo "AppPackages\Gladhen3_${newVersion}_x86_x64_bundle.msixupload"

Write-Host ""
Write-Host "[1/3] ShellExtension Release x86 (Win32)..." -ForegroundColor Cyan
& $msb "$repo\Gladhen3.sln" /t:ShellExtension /p:Configuration=Release /p:Platform=x86 /m /v:q /clp:ErrorsOnly /nologo
if ($LASTEXITCODE -ne 0) { Fail "ShellExtension x86 build failed (exit $LASTEXITCODE)" }

Write-Host "[2/3] ShellExtension Release x64..." -ForegroundColor Cyan
& $msb "$repo\Gladhen3.sln" /t:ShellExtension /p:Configuration=Release /p:Platform=x64 /m /v:q /clp:ErrorsOnly /nologo
if ($LASTEXITCODE -ne 0) { Fail "ShellExtension x64 build failed (exit $LASTEXITCODE)" }

Write-Host "[3/3] Store package (StoreUpload mode, x86+x64 bundle) - this takes a while..." -ForegroundColor Cyan
# Delete the target artifact so an incremental build can't leave a stale file behind
# (rebuilding the same version would otherwise skip regenerating the .msixupload).
if (Test-Path $upload) { Remove-Item $upload -Force -Confirm:$false }
# Force a clean Release build of the app project: after a version-only bump, MSBuild's
# incremental packaging can reuse a stale generated AppxManifest.xml, producing packages
# named with the new version but stamped with the OLD version inside (Store rejects them).
foreach ($plat in @("x86", "x64")) {
    foreach ($root in @("obj", "bin")) {
        $dir = Join-Path $repo "$root\$plat\Release"
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -Confirm:$false }
    }
}
$buildArgs = @(
    "$repo\Gladhen3.csproj", "/restore",
    "/p:Configuration=Release", "/p:Platform=x86", "/p:SolutionDir=$repo/",
    "/p:UapAppxPackageBuildMode=StoreUpload",
    "/p:AppxBundle=Always", "/p:AppxBundlePlatforms=x86|x64",
    "/p:GenerateAppxPackageOnBuild=true",
    "/m", "/v:q", "/clp:ErrorsOnly", "/nologo"
)
& $msb @buildArgs
if ($LASTEXITCODE -ne 0) { Fail "Store package build failed (exit $LASTEXITCODE)" }

# ---------- verify the artifact ----------
if (-not (Test-Path $upload)) {
    Get-ChildItem (Join-Path $repo "AppPackages") -Filter "*.msixupload" | ForEach-Object { Write-Host "  found: $($_.Name)" }
    Fail "expected artifact not found: $upload"
}
if ((Get-Item $upload).LastWriteTime -lt $startTime) { Fail "artifact $upload is older than this build - something went wrong" }

Write-Host ""
Write-Host "Verifying $([IO.Path]::GetFileName($upload)) ..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempDir = Join-Path $env:TEMP "gladhen3-storepkg-verify-$PID"
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $tempDir | Out-Null

$problems = @()
$archesSeen = @()
$resourceCount = 0
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($upload)
    $bundleEntry = $zip.Entries | Where-Object { $_.Name -like "*.msixbundle" } | Select-Object -First 1
    if (-not $bundleEntry) { Fail "no .msixbundle inside the .msixupload" }
    $bundlePath = Join-Path $tempDir $bundleEntry.Name
    [IO.Compression.ZipFileExtensions]::ExtractToFile($bundleEntry, $bundlePath, $true)
    $zip.Dispose()

    $bundleZip = [IO.Compression.ZipFile]::OpenRead($bundlePath)
    $innerEntries = @($bundleZip.Entries | Where-Object { $_.Name -like "*.msix" })
    $innerPaths = @()
    foreach ($entry in $innerEntries) {
        $p = Join-Path $tempDir $entry.Name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $p, $true)
        $innerPaths += $p
    }
    $bundleZip.Dispose()
    if ($innerPaths.Count -eq 0) { Fail "bundle contains no .msix packages" }

    foreach ($p in $innerPaths) {
        $leaf = [IO.Path]::GetFileName($p)
        $z = [IO.Compression.ZipFile]::OpenRead($p)
        $mfEntry = $z.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" }
        $reader = New-Object IO.StreamReader($mfEntry.Open())
        [xml]$mf = $reader.ReadToEnd()
        $reader.Close()
        $hasShellDll = [bool]($z.Entries | Where-Object { $_.FullName -eq "ShellExtension.dll" })
        $hasExe = [bool]($z.Entries | Where-Object { $_.Name -eq "Gladhen3.exe" })
        $z.Dispose()

        $id = $mf.Package.Identity
        if ($id.Name -ne $expectedName) { $problems += "$leaf : Name is '$($id.Name)', expected '$expectedName'" }
        if ($id.Publisher -ne $expectedPublisher) {
            $problems += "$leaf : Publisher is '$($id.Publisher)', expected '$expectedPublisher'"
            if ($id.Publisher -like "CN=Armia*") { $problems += "$leaf : ^ this is the DEV/sideload identity - the package was not built in StoreUpload mode" }
        }
        if ($id.Version -ne $newVersion) { $problems += "$leaf : Version is '$($id.Version)', expected '$newVersion'" }
        $arch = $id.ProcessorArchitecture
        if ($arch -in @("x86", "x64")) {
            $archesSeen += $arch
            if (-not $hasShellDll) { $problems += "$leaf : ShellExtension.dll missing from package" }
            if (-not $hasExe) { $problems += "$leaf : Gladhen3.exe missing from package" }
        } else {
            $resourceCount++
        }
    }
    foreach ($required in @("x86", "x64")) {
        if ($archesSeen -notcontains $required) { $problems += "bundle is missing the $required package" }
    }
} finally {
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -Confirm:$false }
}

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "VERIFICATION FAILED - do NOT upload this file:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    exit 1
}

# ---------- success ----------
Write-Host ""
Write-Host "VERIFIED OK" -ForegroundColor Green
Write-Host ("  Family name: {0}" -f $expectedFamily)
Write-Host ("  Publisher:   {0}" -f $expectedPublisher)
Write-Host ("  Version:     {0}" -f $newVersion)
Write-Host ("  Packages:    {0} (+{1} resource packages), ShellExtension.dll + Gladhen3.exe present" -f (($archesSeen | Sort-Object) -join ", "), $resourceCount)
Write-Host ""
Write-Host "UPLOAD THIS FILE to Partner Center:" -ForegroundColor Green
Write-Host "  $upload" -ForegroundColor Green
Write-Host ""
Write-Host "Never upload packages from AppPackages\*_Test\ folders (dev-signed, Store rejects them)."

$stale = Get-ChildItem (Join-Path $repo "AppPackages") -Filter "*.msixupload" | Where-Object { $_.FullName -ne $upload }
if ($stale) {
    Write-Host ""
    Write-Host "Stale uploads you may want to delete:" -ForegroundColor Yellow
    foreach ($s in $stale) { Write-Host "  $($s.FullName)" -ForegroundColor Yellow }
}
