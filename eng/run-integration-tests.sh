#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_path="${1:-}"
target_framework="${2:-net10.0}"

if [[ -z "${package_path}" ]]; then
    package_path="$(find "${repository_root}/artifacts/packages" -maxdepth 1 -name 'DisposableGenerator.*.nupkg' ! -name '*.symbols.nupkg' | sort | tail -n 1)"
fi

if [[ ! -f "${package_path}" ]]; then
    echo "Package not found: ${package_path}" >&2
    echo "Pack DisposableGenerator first or pass the exact .nupkg path." >&2
    exit 1
fi

case "${target_framework}" in
    net8.0|net9.0|net10.0)
        ;;
    *)
        echo "Unsupported integration target framework: ${target_framework}" >&2
        echo "Expected net8.0, net9.0, or net10.0." >&2
        exit 1
        ;;
esac

package_path="$(cd "$(dirname "${package_path}")" && pwd)/$(basename "${package_path}")"
package_file="$(basename "${package_path}")"
package_version="${package_file#DisposableGenerator.}"
package_version="${package_version%.nupkg}"

integration_source="${repository_root}/tests/PackageIntegration"
if find "${integration_source}" -name '*.csproj' -exec grep -H '<ProjectReference' {} + | grep -q .; then
    echo "Package integration projects must not contain ProjectReference items." >&2
    exit 1
fi

integration_work="$(mktemp -d)"
trap 'rm -rf "${integration_work}"' EXIT

cp -R "${integration_source}/." "${integration_work}/"
mkdir -p "${integration_work}/local-packages" "${integration_work}/package-cache"
cp "${package_path}" "${integration_work}/local-packages/"

projects=(SyncConsumer AsyncConsumer ConfiguredConsumer)
if [[ "${target_framework}" == "net10.0" ]]; then
    projects+=(ModernCSharpConsumer)
fi

cd "${integration_work}"
for project in "${projects[@]}"; do
    project_file="${integration_work}/${project}/${project}.csproj"
    echo "Running ${project} for ${target_framework} against DisposableGenerator ${package_version}."
    dotnet restore "${project_file}" \
        --configfile "${integration_work}/NuGet.Config" \
        --packages "${integration_work}/package-cache" \
        -p:DisposableGeneratorPackageVersion="${package_version}" \
        -p:IntegrationTargetFramework="${target_framework}"
    dotnet run --project "${project_file}" \
        --configuration Release \
        --framework "${target_framework}" \
        --no-restore \
        -p:DisposableGeneratorPackageVersion="${package_version}" \
        -p:IntegrationTargetFramework="${target_framework}"

    if find "${integration_work}/${project}/bin/Release/${target_framework}" -name 'DisposableGenerator.dll' -print -quit | grep -q .; then
        echo "${project} copied the compile-time-only generator into runtime output." >&2
        exit 1
    fi
done

diagnostics_project="${integration_work}/DiagnosticsConsumer/DiagnosticsConsumer.csproj"
diagnostics_log="${integration_work}/diagnostics-${target_framework}.log"
dotnet restore "${diagnostics_project}" \
    --configfile "${integration_work}/NuGet.Config" \
    --packages "${integration_work}/package-cache" \
    -p:DisposableGeneratorPackageVersion="${package_version}" \
    -p:IntegrationTargetFramework="${target_framework}"

set +e
dotnet build "${diagnostics_project}" \
    --configuration Release \
    --no-restore \
    -p:DisposableGeneratorPackageVersion="${package_version}" \
    -p:IntegrationTargetFramework="${target_framework}" >"${diagnostics_log}" 2>&1
diagnostics_status=$?
set -e

if [[ ${diagnostics_status} -eq 0 ]]; then
    echo "DiagnosticsConsumer unexpectedly built successfully." >&2
    exit 1
fi

for diagnostic in DISP001 DISP007 DISP009 DISP010 DISP020 DISP025; do
    if ! grep -q "${diagnostic}" "${diagnostics_log}"; then
        sed -n '1,240p' "${diagnostics_log}" >&2
        echo "DiagnosticsConsumer did not report ${diagnostic}." >&2
        exit 1
    fi
done

ownership_project="${integration_work}/OwnershipDiagnosticConsumer/OwnershipDiagnosticConsumer.csproj"
ownership_sarif="${integration_work}/ownership-${target_framework}.sarif"
dotnet restore "${ownership_project}" \
    --configfile "${integration_work}/NuGet.Config" \
    --packages "${integration_work}/package-cache" \
    -p:DisposableGeneratorPackageVersion="${package_version}" \
    -p:IntegrationTargetFramework="${target_framework}"

dotnet build "${ownership_project}" \
    --configuration Release \
    --no-restore \
    -p:DisposableGeneratorPackageVersion="${package_version}" \
    -p:IntegrationTargetFramework="${target_framework}" \
    -p:ErrorLog="${ownership_sarif}"

if [[ ! -f "${ownership_sarif}" ]] || ! grep -q "DISP006" "${ownership_sarif}"; then
    echo "OwnershipDiagnosticConsumer did not record DISP006 in compiler SARIF output." >&2
    exit 1
fi

echo "Passed packed-package integration tests for DisposableGenerator ${package_version} on ${target_framework}."
