# Release process

DisposableGenerator releases are built from immutable tags by GitHub Actions. The
release workflow validates, packs, tests, and publishes the exact tagged source; do
not upload locally built packages to NuGet.

## One-time repository setup

Create a granular NuGet API key that can push only the `DisposableGenerator`
package, then store it as the `NUGET_API_KEY` GitHub Actions repository secret:

```bash
gh secret set NUGET_API_KEY
```

Confirm that the secret name exists without exposing its value:

```bash
gh secret list --app actions
```

## Prepare a release

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Move newly introduced diagnostics from
   `AnalyzerReleases.Unshipped.md` into a versioned section in
   `AnalyzerReleases.Shipped.md`.
3. Add the version and release date to `CHANGELOG.md`, and update its comparison
   links.
4. Run the complete validation sequence from the repository `AGENTS.md`.
5. Merge the release-preparation pull request and wait for both CI and NuGet
   Integration to succeed on `main`.

For the first release, these files already declare version `1.0.0` and diagnostics
`DISP001` through `DISP026` as shipped.

## Publish

Start from an up-to-date, clean `main` branch. The release workflow accepts only a
stable `vMAJOR.MINOR.PATCH` tag, requires the tagged commit to be contained in
`origin/main`, and requires the tag version to equal `VersionPrefix`.

```bash
git fetch origin
git switch main
git pull --ff-only
git tag -a v1.0.0 -m "DisposableGenerator 1.0.0"
git push origin v1.0.0
```

The tag triggers the Release workflow. It first runs the reusable packed-package
integration matrix, then repeats release-mode build, test, sample, package, and
package-smoke validation before publishing to NuGet and creating the GitHub
release.

## Verify

After the workflow succeeds:

1. Confirm the GitHub release contains the `.nupkg` artifact and generated notes.
2. Confirm version `1.0.0` is visible at
   <https://www.nuget.org/packages/DisposableGenerator/1.0.0> after NuGet indexing
   completes.
3. Install the published package into a clean consumer and build it once from the
   public NuGet source.

NuGet package versions and release tags are immutable. Never move or reuse a
published tag. If publication must be corrected, unlist the affected package when
appropriate and prepare a new patch version. The NuGet push uses
`--skip-duplicate`, so a workflow rerun can safely recover when the package was
published but the later GitHub release step failed.
