<#
.SYNOPSIS
    Generates README.md from README.tpl, injecting the per-class coverage table.

.DESCRIPTION
    README.md is a generated file: its source of truth is README.tpl. This script reads the
    template, locates the region delimited by the "<!-- COVERAGE:START -->" / "<!-- COVERAGE:END -->"
    HTML-comment markers, and replaces the content between them with a ReportGenerator
    "MarkdownSummaryGithub" report (per-class/per-assembly coverage breakdown), then writes the
    result to README.md.

    Keeping the coverage table out of README.md's own edit history is the whole point: without a
    template, every CI run would touch the same hand-authored file that contributors edit, and its
    diff/blame history would fill up with nothing but coverage-number churn. With a template, only
    README.md — a generated artifact — absorbs that churn; README.tpl (and its git history) stays
    focused on the prose contributors actually author.

    This script does not run tests or ReportGenerator itself; it only performs the template
    injection. Run the test suite with coverage collection and ReportGenerator first (see
    .github/workflows/ci.yml's "Unit Tests + Integration Tests" job, or test-coverage.ps1 locally)
    to produce the -CoverageReportPath input.

.PARAMETER TemplatePath
    Path to the template file. Defaults to README.tpl next to this script.

.PARAMETER OutputPath
    Path to write the generated file to. Defaults to README.md next to this script.

.PARAMETER CoverageReportPath
    Path to a ReportGenerator "MarkdownSummaryGithub" report (typically CoverageReport/SummaryGithub.md
    or coveragereport/SummaryGithub.md). When the file does not exist, the script falls back to
    whatever coverage content is already present between the markers in -OutputPath (so a
    template-only regeneration, run without ever executing the test suite, does not blow away the
    last real coverage table). Only when neither a fresh report nor a previously generated
    -OutputPath exists does it fall back to a placeholder notice.

    ReportGenerator wraps each section in collapsible "<details><summary>...</summary>...</details>"
    tags, meant for its own standalone report output. Rendered inline in a README they add clutter
    without adding anything a reader needs, so this script strips those tags (but keeps their
    content) before injecting the report.

.EXAMPLE
    pwsh ./generate-readme.ps1
    Regenerates README.md from README.tpl using ./coveragereport/SummaryGithub.md, if present.

.EXAMPLE
    pwsh ./generate-readme.ps1 -CoverageReportPath CoverageReport/SummaryGithub.md
    Same, pointing at a differently-cased coverage output directory (as produced by ci.yml).
#>
param(
    [string]$TemplatePath = (Join-Path $PSScriptRoot "README.tpl"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "README.md"),
    [string]$CoverageReportPath = (Join-Path $PSScriptRoot "coveragereport/SummaryGithub.md")
)

$ErrorActionPreference = "Stop"

$startMarker = "<!-- COVERAGE:START -->"
$endMarker = "<!-- COVERAGE:END -->"

if (-not (Test-Path $TemplatePath)) {
    throw "Template not found: $TemplatePath"
}

$template = Get-Content -Path $TemplatePath -Raw

if ($template -notlike "*$startMarker*" -or $template -notlike "*$endMarker*") {
    throw "README.tpl must contain both '$startMarker' and '$endMarker' markers exactly once."
}

# Strips ReportGenerator's "<details><summary>...</summary>" / "</details>" wrapper lines while
# keeping everything they wrap, so the report renders as plain, always-expanded Markdown in the README.
function Remove-DetailsTags {
    param([string]$Text)

    $lines = $Text -split "`r?`n" | Where-Object {
        $_ -notmatch '<details\b' -and $_.Trim() -ne '</details>'
    }
    $joined = ($lines -join "`n").Trim()

    # Removing a tag line can leave a run of blank lines where it used to sit; collapse those
    # down to the single blank line Markdown needs to separate blocks.
    return $joined -replace '(\r?\n){3,}', "`n`n"
}

$existingCoverageContent = $null
if (Test-Path $OutputPath) {
    $existingOutput = Get-Content -Path $OutputPath -Raw
    $existingMatch = [regex]::Match(
        $existingOutput,
        [regex]::Escape($startMarker) + "(?s)(.*?)" + [regex]::Escape($endMarker))
    if ($existingMatch.Success) {
        $existingCoverageContent = Remove-DetailsTags $existingMatch.Groups[1].Value
    }
}

if (Test-Path $CoverageReportPath) {
    $coverageContent = Remove-DetailsTags (Get-Content -Path $CoverageReportPath -Raw)
    Write-Host "Injecting coverage table from: $CoverageReportPath" -ForegroundColor Green
}
elseif ($existingCoverageContent) {
    $coverageContent = $existingCoverageContent
    Write-Host "Coverage report not found at '$CoverageReportPath' - preserving existing coverage table from $OutputPath." -ForegroundColor Yellow
}
else {
    $coverageContent = "_Coverage table not available for this build. Run the test suite with " + `
        "coverage collection, generate a ReportGenerator ``MarkdownSummaryGithub`` report at " + `
        "``$CoverageReportPath``, and re-run this script._"
    Write-Host "Coverage report not found at '$CoverageReportPath' - inserting placeholder." -ForegroundColor Yellow
}

$replacement = "$startMarker`n`n$coverageContent`n`n$endMarker"

# A MatchEvaluator (rather than a plain replacement string) sidesteps regex backreference
# interpretation (e.g. a literal '$1' inside the coverage table) entirely, since the delegate's
# return value is inserted verbatim.
$pattern = [regex]::Escape($startMarker) + "(?s).*?" + [regex]::Escape($endMarker)
$regex = [System.Text.RegularExpressions.Regex]::new($pattern)
$generated = $regex.Replace($template, { $replacement }, 1)

Set-Content -Path $OutputPath -Value $generated -NoNewline
Write-Host "Generated $OutputPath from $TemplatePath" -ForegroundColor Green
