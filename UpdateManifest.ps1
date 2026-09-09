param (
    [Parameter(Mandatory = $true)]
    [string] $manifestFile,

    [Parameter(Mandatory = $true)]
    [string] $versionString
)

$ErrorActionPreference = 'Stop'

if ($versionString -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid manifest version '$versionString'."
}

$fullPath = [IO.Path]::GetFullPath($manifestFile)
$utf8NoBom = New-Object Text.UTF8Encoding($false, $true)
$manifest = [IO.File]::ReadAllText($fullPath, $utf8NoBom)
$pattern = '"version_number"\s*:\s*"[^"]*"'
$matches = [Text.RegularExpressions.Regex]::Matches($manifest, $pattern)
if ($matches.Count -ne 1) {
    throw "Expected exactly one version_number property in '$fullPath'."
}

$replacement = '"version_number": "' + $versionString + '"'
$updated = [Text.RegularExpressions.Regex]::Replace(
    $manifest,
    $pattern,
    $replacement)
$directory = [IO.Path]::GetDirectoryName($fullPath)
$temporaryPath = [IO.Path]::Combine(
    $directory,
    '.' + [IO.Path]::GetFileName($fullPath) + '.' +
    [Guid]::NewGuid().ToString('N') + '.tmp')
$backupPath = $temporaryPath + '.bak'

try {
    [IO.File]::WriteAllText($temporaryPath, $updated, $utf8NoBom)
    [IO.File]::Replace(
        $temporaryPath,
        $fullPath,
        $backupPath,
        $true)
}
finally {
    if ([IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
    if ([IO.File]::Exists($backupPath)) {
        [IO.File]::Delete($backupPath)
    }
}
