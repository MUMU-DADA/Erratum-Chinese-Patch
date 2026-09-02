param(
    [switch]$AllowIncomplete
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'translation\strings.csv'
$output = Join-Path $projectRoot 'payload\BepInEx\plugins\ErratumChinesePatch\Localization\strings.tsv'
$acceptedReview = @('reviewed', 'approved')
$validModes = @('exact', 'prefix', 'suffix')
$errors = [System.Collections.Generic.List[string]]::new()
$missing = [System.Collections.Generic.List[string]]::new()
$seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$rules = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
$separator = [char]0x1f

$rows = Import-Csv -LiteralPath $source -Encoding utf8
for ($index = 0; $index -lt $rows.Count; $index++) {
    $row = $rows[$index]
    $lineNumber = $index + 2
    $id = ([string]$row.id).Trim()
    $location = "strings.csv:$lineNumber" + $(if ($id) { " ($id)" } else { '' })
    $mode = ([string]$row.mode).Trim().ToLowerInvariant()
    $scene = ([string]$row.scene).Trim()
    $hierarchy = ([string]$row.hierarchy).Trim()
    $sourceHash = ([string]$row.source_sha256).Trim().ToLowerInvariant()
    $translation = [string]$row.translation
    $status = ([string]$row.review_status).Trim().ToLowerInvariant()
    if (-not $scene) { $scene = '*' }
    if (-not $hierarchy) { $hierarchy = '*' }

    if (-not $id) {
        $errors.Add("${location}: id is empty")
    } elseif (-not $seenIds.Add($id)) {
        $errors.Add("${location}: duplicate id")
    }
    if ($mode -notin $validModes) {
        $errors.Add("${location}: unsupported mode '$mode'")
    }
    if ($sourceHash -notmatch '^[0-9a-f]{64}$') {
        $errors.Add("${location}: source_sha256 is not a lowercase SHA-256 digest")
    }

    $sourceLength = 0
    if (-not [int]::TryParse([string]$row.source_utf16_length, [ref]$sourceLength) -or $sourceLength -le 0) {
        $errors.Add("${location}: source_utf16_length must be a positive integer")
        continue
    }
    if ([string]::IsNullOrWhiteSpace($translation)) {
        $missing.Add("${location}: translation is empty")
        continue
    }
    if ($status -notin $acceptedReview) {
        $missing.Add("${location}: review_status must be reviewed or approved")
        continue
    }

    $key = "$mode$separator$scene$separator$hierarchy$separator$sourceHash$separator$sourceLength"
    if ($rules.ContainsKey($key) -and $rules[$key].Translation -ne $translation) {
        $errors.Add("${location}: conflicting translation for runtime key (also $($rules[$key].Id))")
    } else {
        $rules[$key] = [pscustomobject]@{
            Id = $id
            Mode = $mode
            Scene = $scene
            Hierarchy = $hierarchy
            SourceHash = $sourceHash
            SourceLength = $sourceLength
            Translation = $translation
        }
    }
}

$report = [ordered]@{
    allow_incomplete = $AllowIncomplete.IsPresent
    compiled_rules = $rules.Count
    missing_or_unreviewed = $missing.Count
    structural_errors = $errors.Count
}
$report | ConvertTo-Json
if ($errors.Count) {
    Write-Host "`nStructural errors:"
    $errors | Select-Object -First 100 | ForEach-Object { Write-Host "- $_" }
}
if ($missing.Count) {
    Write-Host "`nIncomplete translations:"
    $limit = if ($AllowIncomplete.IsPresent) { 10 } else { 100 }
    $missing | Select-Object -First $limit | ForEach-Object { Write-Host "- $_" }
}
if ($errors.Count -or ($missing.Count -and -not $AllowIncomplete.IsPresent)) {
    throw '本地化表校验失败。'
}
if ($AllowIncomplete.IsPresent) {
    return
}

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($output, $false, $utf8NoBom)
$writer.NewLine = "`n"
try {
    $writer.WriteLine('id' + "`t" + 'mode' + "`t" + 'scene' + "`t" + 'hierarchy' + "`t" + 'source_sha256' + "`t" + 'source_utf16_length' + "`t" + 'translation_base64')
    $orderedRules = $rules.Values | Sort-Object Mode, Scene, Hierarchy, SourceHash, SourceLength
    foreach ($rule in $orderedRules) {
        $encoded = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($rule.Translation))
        $writer.WriteLine(($rule.Id, $rule.Mode, $rule.Scene, $rule.Hierarchy, $rule.SourceHash, $rule.SourceLength, $encoded) -join "`t")
    }
} finally {
    $writer.Dispose()
}
Write-Host "Wrote payload\BepInEx\plugins\ErratumChinesePatch\Localization\strings.tsv"
