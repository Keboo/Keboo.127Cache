#Requires -Version 7.2

function Get-CiCoverageReports {
    param(
        [Parameter(Mandatory)]
        [string] $CoverageDirectory
    )

    $coveragePath = (Resolve-Path -LiteralPath $CoverageDirectory).Path
    $records = @(Get-ChildItem -LiteralPath $coveragePath -Filter 'ci-module-*.trx' -File -Recurse |
        Sort-Object -Property FullName)
    if ($records.Count -eq 0) {
        throw "No per-module test records were generated in $coveragePath."
    }

    $comparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $seen = [Collections.Generic.HashSet[string]]::new($comparer)
    $reports = [Collections.Generic.List[System.IO.FileInfo]]::new()
    foreach ($record in $records) {
        $relative = [IO.Path]::GetRelativePath($coveragePath, $record.FullName)
        $parts = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
        if ($parts.Length -ne 3 -or $record.Length -eq 0) {
            throw "Expected a nonempty per-module test record: $($record.FullName)."
        }

        # TRX attachments can contain copies; only the original per-module report is an input.
        $reportPath = Join-Path $record.DirectoryName (Join-Path 'TestResults' 'coverage.cobertura.xml')
        if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "Missing fixed-name Cobertura report for $($record.FullName): $reportPath."
        }

        $report = Get-Item -LiteralPath $reportPath
        if (!$seen.Add($report.FullName)) {
            throw "Multiple test modules share the same Cobertura report: $($report.FullName)."
        }
        if ($report.Length -eq 0 -or $report.FullName.Contains(';')) {
            throw "Cobertura report is empty or cannot be passed to ReportGenerator: $($report.FullName)."
        }
        try {
            [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
        } catch {
            throw "Invalid Cobertura XML: $($report.FullName)."
        }
        if ($document.DocumentElement.Name -cne 'coverage') {
            throw "Not a Cobertura coverage report: $($report.FullName)."
        }
        $reports.Add($report)
    }

    $canonicalReports = @(Get-ChildItem -LiteralPath $coveragePath -Filter 'coverage.cobertura.xml' -File -Recurse |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($coveragePath, $_.FullName)
            $parts = $relative.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
            $parts.Length -eq 4 -and $parts[2] -ceq 'TestResults'
        })
    foreach ($report in $canonicalReports) {
        if (!$seen.Contains($report.FullName)) {
            throw "Cobertura report has no corresponding test module record: $($report.FullName)."
        }
    }

    return $reports.ToArray()
}
