#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Counts lines of C# code in the Omicron project.
.DESCRIPTION
    Recursively finds all .cs files (excluding bin/, obj/, and READ_ONLY folders),
    counts lines per project, and prints a summary.
#>

$projects = @("Omicron.CLI", "Omicron.Core", "Omicron.Core.Tests")
$excludePatterns = @("\b(bin|obj)\b", "\bREAD_ONLY\b")
$grandTotal = 0

Write-Host "Lines of C# Code`n" -ForegroundColor Cyan
[Console]::WriteLine("{0,-25} {1,10}", "Project", "Lines")
[Console]::WriteLine("{0}", "-" * 36)

foreach ($proj in $projects) {
    $files = Get-ChildItem -Path $proj -Recurse -Filter "*.cs" -ErrorAction SilentlyContinue |
        Where-Object {
            $full = $_.FullName
            $matches = $true
            foreach ($pat in $excludePatterns) {
                if ($full -match $pat) { $matches = $false; break }
            }
            $matches
        }

    $projectTotal = 0
    foreach ($file in $files) {
        $lines = (Get-Content $file.FullName).Count
        $projectTotal += $lines
    }

    [Console]::WriteLine("{0,-25} {1,10:N0}", $proj, $projectTotal)
    $grandTotal += $projectTotal
}

[Console]::WriteLine("{0}", "-" * 36)
[Console]::ForegroundColor = [ConsoleColor]::Cyan
[Console]::WriteLine("{0,-25} {1,10:N0}", "TOTAL", $grandTotal)
[Console]::ResetColor()
[Console]::WriteLine()
