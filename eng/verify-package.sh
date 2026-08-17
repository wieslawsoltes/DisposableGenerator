#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_path="${1:-}"

if [[ -z "${package_path}" ]]; then
    package_path="$(find "${repository_root}/artifacts/packages" -maxdepth 1 -name 'DisposableGenerator.*.nupkg' ! -name '*.symbols.nupkg' | sort | tail -n 1)"
fi

if [[ ! -f "${package_path}" ]]; then
    echo "Package not found: ${package_path}" >&2
    exit 1
fi

package_path="$(cd "$(dirname "${package_path}")" && pwd)/$(basename "${package_path}")"
package_file="$(basename "${package_path}")"
package_version="${package_file#DisposableGenerator.}"
package_version="${package_version%.nupkg}"

package_entries="$(unzip -Z1 "${package_path}")"
for required_entry in \
    "analyzers/dotnet/cs/DisposableGenerator.dll" \
    "buildTransitive/DisposableGenerator.props" \
    "README.md" \
    "CHANGELOG.md" \
    "LICENSE"; do
    if ! grep -Fxq "${required_entry}" <<<"${package_entries}"; then
        echo "Package is missing ${required_entry}" >&2
        exit 1
    fi
done

if grep -Eq '^(lib|ref|runtimes)/' <<<"${package_entries}"; then
    echo "Compile-time-only package contains a runtime or reference asset." >&2
    exit 1
fi

package_spec="$(unzip -p "${package_path}" '*.nuspec')"
if ! grep -Fq "<version>${package_version}</version>" <<<"${package_spec}"; then
    echo "Package manifest version does not match ${package_version}." >&2
    exit 1
fi

if ! grep -Fq '<projectUrl>https://github.com/wieslawsoltes/DisposableGenerator</projectUrl>' <<<"${package_spec}"; then
    echo "Package manifest is missing the project URL." >&2
    exit 1
fi

if ! grep -Fq "<releaseNotes>https://github.com/wieslawsoltes/DisposableGenerator/blob/v${package_version}/CHANGELOG.md</releaseNotes>" <<<"${package_spec}"; then
    echo "Package manifest release notes do not match ${package_version}." >&2
    exit 1
fi

if grep -q '<dependency ' <<<"${package_spec}"; then
    echo "Compile-time-only package exposes a NuGet dependency to consumers." >&2
    exit 1
fi

smoke_directory="$(mktemp -d)"
trap 'rm -rf "${smoke_directory}"' EXIT
cp "${repository_root}/eng/package-smoke/PackageSmoke.csproj" "${smoke_directory}/"
cp "${repository_root}/eng/package-smoke/Program.cs" "${smoke_directory}/"

dotnet restore "${smoke_directory}/PackageSmoke.csproj" \
    -p:DisposableGeneratorPackageVersion="${package_version}" \
    --packages "${smoke_directory}/packages" \
    --source "$(dirname "${package_path}")" \
    --source "https://api.nuget.org/v3/index.json"
dotnet run --project "${smoke_directory}/PackageSmoke.csproj" \
    --configuration Release \
    --no-restore \
    -p:DisposableGeneratorPackageVersion="${package_version}"

echo "Verified DisposableGenerator ${package_version} metadata, analyzer-only contents, build assets, generated API, and runtime behavior."
