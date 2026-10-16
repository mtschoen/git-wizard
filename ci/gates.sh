#!/usr/bin/env bash
# Gate recipes shared by CI and tools/ci-preflight.sh.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
projects=(GitWizard/GitWizard.csproj git-wizard/git-wizard.csproj GitWizardUI/GitWizardUI.csproj GitWizardTests/GitWizardTests.csproj)
windows=false
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) windows=true ;; esac
python_command=python3
if $windows; then python_command=python; fi
case "${1:?gate required}" in
    native)
        if $windows; then
            pwsh -NoProfile -File ci/windows-gates.ps1 native
        else
            cmake -S external/MFTLib/MFTLibNative -B external/MFTLib/build-linux -G Ninja -DCMAKE_BUILD_TYPE=Release
            cmake --build external/MFTLib/build-linux
        fi
        dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
        dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
        ;;
    restore|build)
        operation=$1
        arguments=()
        if [[ "$operation" == build ]]; then arguments=(-c Release --no-restore); fi
        if $windows; then
            dotnet "$operation" git-wizard.slnx "${arguments[@]}"
        else
            failure=0
            for project in "${projects[@]}"; do
                dotnet "$operation" "$project" "${arguments[@]}" || failure=1
            done
            exit "$failure"
        fi
        ;;
    format) dotnet format git-wizard.slnx --verify-no-changes ;;
    inspect) pwsh -NoProfile -File ci/windows-gates.ps1 inspect ;;
    test)
        results=${CI_RESULTS_DIRECTORY:-./TestResults}
        mkdir -p "$results"
        rm -f "$results/current-test-directory"
        invocation=$(mktemp -d "$results/test-XXXXXXXX")
        basename "$invocation" > "$results/current-test-directory"
        arguments=()
        if ! $windows; then arguments=('--collect:XPlat Code Coverage'); fi
        # MSYS otherwise converts the semicolon in the logger argument to a path list.
        MSYS2_ARG_CONV_EXCL='*' dotnet test GitWizardTests/GitWizardTests.csproj -c Release --no-build \
            --logger 'trx;LogFileName=TestResults.trx' --results-directory "$invocation" "${arguments[@]}"
        ;;
    coverage)
        results=${CI_RESULTS_DIRECTORY:-TestResults}
        invocation=$(cat "$results/current-test-directory")
        test -n "$invocation"
        "$python_command" ci/post-coverage-status.py --cobertura "$results/$invocation/**/coverage.cobertura.xml" \
            --gate-line 55 --summary --skip-post
        ;;
    aislop) bash ci/aislop-gate.sh ;;
    contracts) "$python_command" -m unittest discover -s ci -p 'test_*.py' -v ;;
    *) echo "Unknown gate: $1" >&2; exit 2 ;;
esac
