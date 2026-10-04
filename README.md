# Shared GitHub Actions

Keep common .NET build steps in one repository. Call these workflows and actions from C# repositories owned by `glennawatson`.

## Build and test

A reusable workflow runs a job from another repository. Call the build workflow once for each runner in your matrix.

```yaml
jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        os: [windows-latest, ubuntu-latest, macos-latest]
    uses: glennawatson/actions-common/.github/workflows/workflow-common-setup-and-build.yml@main
    with:
      runner: ${{ matrix.os }}
      solutionFile: MyProject.slnx
      srcFolder: src
```

The caller controls triggers, permissions and concurrency. The shared workflow checks out the caller. It installs the SDKs, stamps a MinVer version, builds the solution and runs tests. MinVer computes a version from Git tags. Tests use Microsoft.Testing.Platform with Cobertura coverage reports.

Set `stampVersion: false` for a repository that does not use MinVer. Set `srcFolder: '.'` for a solution at the repository root. Set `dotnetVersions` to match the repository's `global.json`. Include a .NET 11 SDK for the shared C# scripts.

Use `minverTagPrefix`, `minverMinimumMajorMinor` and `minverAutoIncrement` to match the repository's version settings. Use `testPreparationFile` to run a repository's C# test preparation file with the `test` argument. Repositories install their own special test dependencies before calling shared actions.

## Shared workflows

| Workflow | Purpose |
| --- | --- |
| `workflow-common-setup-and-build.yml` | Build and test on the caller's chosen runner. |
| `workflow-common-codeql-actions.yml` | Scan GitHub Actions with CodeQL. |

The CodeQL caller must grant `security-events: write`, `contents: read` and `actions: read`.

## Shared actions

A composite action groups steps within an existing job. Use these actions for workflows with their own build or release steps.

```yaml
- uses: glennawatson/actions-common/.github/actions/setup-sdk@main
  with:
    global-json-file: global.json
```

| Action | Purpose |
| --- | --- |
| `setup-sdk` | Install SDK versions or the SDK from `global.json`. |
| `run-csharp` | Run a C# file through .NET without a shell script. |
| `run-command` | Run a command with separate arguments through .NET. |
| `restore-roslyn-slots` | Restore analyzer projects for each Roslyn version. |
| `dotnet-environment` | Install SDKs and stamp a MinVer version. |
| `dotnet-build` | Restore, build and optionally pack a solution. |
| `dotnet-test` | Run tests and upload coverage and diagnostic logs. |
| `minver` | Set version outputs and build environment variables. |
| `compute-version-and-tag` | Compute a release version from existing tags. |
| `certum-sign` | Sign NuGet packages inside the existing Certum signer image. |
| `sonarcloud` | Begin or end SonarCloud analysis. |

Action inputs and outputs are defined in each `action.yml`. Pass secrets through the calling job's environment. The signing action needs `CERTUM_USER_ID`, `CERTUM_OTP_URI` and optionally `CERTUM_CERT_FINGERPRINT`. SonarCloud needs `SONAR_TOKEN`.

The signing image stays at `ghcr.io/reactiveui/certum-signer`. This repository shares the action that drives it. It does not build or publish that image.

## C# scripts

Each action keeps its C# files in a `scripts` folder beside `action.yml`. GitHub downloads a remote action before running it. `github.action_path` gives the path to that downloaded folder.

The `run-csharp` action starts .NET in the runner's temporary folder. It runs the requested file with `dotnet run --file`. Inputs and secrets stay in environment variables. No Bash or Python script runs in this repository.

Use `shared-script` to run a file from `run-csharp/scripts`. Use `script-file` for a file beside another shared action or in the caller checkout. Use `run-command` for a single command. Its `arguments` input has one argument per line.

Run the repository checks on Linux:

```text
dotnet run --file tools/Verify.cs
```

The checks compile every action script with warnings treated as errors. They check shared action inputs and workflow syntax. They run the C# launcher to check remote paths, argument handling and child exit codes. The repository's CI runs the same checks.

## Updates

Callers use `@main`, as the reference shared repository does. A change to `main` reaches callers on their next run. Check workflow syntax and action input compatibility before publishing changes.

The consumer repositories retain small forwarding actions. These keep existing local action paths and their repository-specific defaults. The implementations live here.

## Publication order

1. Publish this repository as `glennawatson/actions-common` with a `main` branch. It must be accessible to every caller.
2. Push the consumer workflow changes.
3. Check a build in each consumer. Check Windows and macOS on their hosted runners.

GitHub's [workflow reuse guide](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows) describes caller permissions and repository access.
