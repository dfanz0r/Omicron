#!/usr/bin/env bash
# Counts lines of C# code in the Omicron project.
# Recursively finds all .cs files (excluding bin/, obj/, and READ_ONLY folders),
# counts lines per project, and prints a summary.
set -euo pipefail

projects=("Omicron.CLI" "Omicron.Core" "Omicron.Core.Tests")
grand_total=0

printf "Lines of C# Code\n\n"
printf "%-25s %10s\n" "Project" "Lines"
printf "%36s\n" "------------------------------------"

for proj in "${projects[@]}"; do
    project_total=0
    while IFS= read -r -d '' file; do
        lines=$(wc -l < "$file")
        project_total=$((project_total + lines))
    done < <(find "$proj" -name '*.cs' -not \( -path '*/bin/*' -o -path '*/obj/*' -o -path '*/READ_ONLY/*' \) -print0 2>/dev/null)

    printf "%-25s %'10d\n" "$proj" "$project_total"
    grand_total=$((grand_total + project_total))
done

printf "%36s\n" "------------------------------------"
printf "\033[36m%-25s %'10d\033[0m\n" "TOTAL" "$grand_total"
echo
