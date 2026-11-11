# 6. Local runs and the full CLI reference

**English** · [Magyar](hu/06-helyi-futtatas-es-cli.md)

[← 5. Evaluation](05-evaluation.md) · [Contents](README.md) · [Next: 7. Troubleshooting →](07-troubleshooting.md)

Everything the pipeline does can run on your own machine with the same CLI, so you can check a fix before
pushing it.

## 6.0 Preparing a freshly cloned repository

The pinned tools (`.security/bin/`) and the Docker images are not in the repository, so on a new machine or
in a new clone they have to be prepared once. Exactly this runs in the pipeline's first step too:

```powershell
cd C:\Work\<repo>
security-toolkit provision --project-root . --require-pinned
```

Prerequisites: the CLI on PATH (`install.bat --no-infra`, on Linux `sudo bash install.sh --no-infra`),
Docker running, and a .NET SDK (Node for frontends).

## 6.1 The `scan` command

```powershell
security-toolkit scan --project-root .                        # every enabled tool, Report mode
security-toolkit scan --project-root . --tool semgrep         # a single tool
security-toolkit scan --project-root . --mode Gate            # with the blocking thresholds
security-toolkit scan --project-root . --staged               # like the pre-commit hook
```

With Sonar, use **your own Sonar user token** (not the pipeline's project analysis token), for this
session only. One user token works for every project you have *Execute Analysis* on; the project is
selected by `sonar.projectKey` ([2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)). The
token never goes into a file; do not store it permanently with `setx` either:

```powershell
$env:SONAR_TOKEN = Read-Host 'Sonar user token'
security-toolkit scan --project-root . --tool sonar --include-sonar
```

The CLI hands the token to the SonarScanner's `begin` and `end` steps in the `SONARQUBE_SCANNER_PARAMS`
environment variable, not on their command line, so other users on the machine cannot see it in the
process list. If you already set `SONARQUBE_SCANNER_PARAMS` yourself (a JSON object), its other
properties are kept.

With coverage, you have to set the **environment variables** of `sonar.testEnvironment` locally yourself
(the pipeline variables do not exist here), e.g.:

```powershell
$env:ConnectionStrings__Default = Read-Host 'Test connection string'
```

| Switch | Meaning |
|---|---|
| `--project-root <path>` | **required**. The repository root (where `.security/` is). |
| `--mode Report\|Gate` | `Report` (default): never fails because of findings. `Gate`: blocking thresholds; with `.security/baseline.json` only new findings block ([5.4](05-evaluation.md#54-report-or-gate-mode)). |
| `--update-baseline` | Runs in Report mode and writes the current blocking findings of the tools that ran into `.security/baseline.json` as accepted ([5.4](05-evaluation.md#baseline-block-only-on-new-findings)). Cannot be combined with `--staged` or `--mode Gate`. |
| `--tool <name>` | only the given tool: `analyzers`, `nuget`, `npm`, `semgrep`, `gitleaks`, `osv`, `devskim`, `sonar` |
| `--include-sonar` | allows the Sonar upload; without it, Sonar is `not-run` |
| `--sonar-only` | Sonar only (= `--tool sonar --include-sonar`) |
| `--history` | Gitleaks scans the whole reachable git history, not just the working tree |
| `--staged` | Gitleaks scans only the staged changes; only Gitleaks runs, and it always blocks |
| `--secrets-only` | Gitleaks only, on the working tree (without the index) |
| `--semgrep-scope repository\|frontend` | a one-time override of the Semgrep scope |
| `--config-file <path>` | use another config instead of `.security/security.config.json` |
| `--run-id <id>` | write into this `results/<id>/` folder and merge with the existing `status.json`. This puts the results of several `--tool` calls into one report, as in the pipeline. The id is a single folder name: 1–128 letters, digits, `.`, `_` or `-`, starting with a letter or digit; anything else exits with `2` (the same rule applies to `report --run`). |

Output: `.security/results/<run-id>/` (`summary.md`, `status.json`, per-tool reports), plus a one-line
summary per tool on the console. Exit code: `0` / `1` / `2`, as in the pipeline
([4.3](04-pipeline.md#the-structure-of-the-pipeline)).

### Local override: `security.config.local.json`

If something on your own machine differs from the team's shared config (typically a local Sonar), create
it from the template:

```powershell
Copy-Item .security\security.config.local.json.example .security\security.config.local.json
```

```json
{
  "sonar": {
    "url": "http://127.0.0.1:9000"
  }
}
```

`scan` merges this over `security.config.json` at run time (deep merge). The file is gitignored and never
gets committed. If it does not exist, `scan` simply runs with the shared config. That is not an error.

## 6.2 The 8 scanners

| Tool | What it checks | What it needs | Output | Blocks in Gate |
|---|---|---|---|---|
| **analyzers** | the .NET Roslyn security rules (CA30xx/CA31xx: injection, XSS, deserialization, crypto), plus EF1002 (raw SQL from an interpolated string) and NU1507 (several package sources without `packageSourceMapping`: dependency confusion), during the build. **The CA30xx taint rules recognize MVC controller actions only, not minimal APIs (MapGet, Carter)**: there, 0 does not mean secure. | .NET SDK | `dotnet-<n>-restore.log`, `dotnet-<n>-build.log` | never (inventory) |
| **nuget** | NuGet packages with known vulnerabilities, transitive ones included (`dotnet list package --vulnerable --include-transitive`) | .NET SDK, a reachable NuGet audit source | `dotnet-<n>-nuget.json` | High / Critical |
| **npm** | `npm audit` in the frontend targets | Node, `package-lock.json` or `npm-shrinkwrap.json` (yarn/pnpm not supported) | `npm-<n>.json` | High / Critical |
| **semgrep** | Semgrep CE rules (`.security/rules/`): C#, TypeScript/JavaScript patterns | Docker, the image pinned in the lock | `semgrep.json`, `semgrep.sarif`, `semgrep-coverage.json` | `ERROR` severity |
| **gitleaks** | secrets in the working tree (or per `--history`/`--staged`), the default rules + `.security/gitleaks.toml` | Docker, a git repository | `gitleaks.json`, `gitleaks.sarif` (redacted) | any finding |
| **osv** | dependency vulnerabilities from the OSV database, for every ecosystem (supplementary) | Docker | `osv.json` | never |
| **devskim** | Microsoft DevSkim security lint (supplementary); can be narrowed with the `devskim.ignoreGlobs`/`ignoreRuleIds` keys | DevSkim pinned into `.security/bin` | `devskim.sarif` | never |
| **sonar** | SonarQube static analysis + optional coverage (`begin` → build → [tests] → `end`) | `SONAR_TOKEN` (a user token locally, a project analysis token in the pipeline), `--include-sonar`, a pinned SonarScanner (+ `dotnet-coverage`, Chrome for coverage) | `sonar-*.log`, `sonar-issues.json`, coverage reports | a failing Quality Gate |

> The Sonar step is guaranteed to run the closing `end` phase even if the build fails in between. Without
> that, SonarScanner could leave a machine-wide MSBuild hook behind that would leak into every other .NET
> build. So **do not interrupt** (Ctrl+C) the Sonar step.

## 6.3 Full command reference

### `install`

```
security-toolkit install --project-root <path> [switches]
```

| Switch | Meaning |
|---|---|
| `--project-root` | **required**. The target repository's root. |
| `--config-file <json>` | a profile merged over the existing/default config |
| `--non-interactive` | no questions; the defaults, the existing config and `--config-file` decide |
| `--configure-only` | only generates the files; no prerequisite installation and no provisioning |
| `--dry-run` | prints the resolved config, writes nothing |
| `--check` | a drift report against the state it would generate; writes nothing. Exit 0 = in sync, 1 = drift. |
| `--run-after-install` | a full Report scan after a successful installation |
| `--semgrep-scope repository\|frontend` | overrides the Semgrep scope |

Details: [chapter 3](03-project-installation.md).

### `provision`

```
security-toolkit provision --project-root <path> [--require-pinned]
```

Pulls the Docker images pinned in `security.lock.json` (and verifies the digest), and installs the .NET
tools of the pinned versions (DevSkim, SonarScanner, dotnet-coverage) under `.security/bin/`. The result
goes into `.security/results/setup-status.json`. `--require-pinned` rejects an image or version that is
not pinned: this is how the pipeline and a fresh clone run (6.0). Without it, missing pins are resolved
and written back into the lock. A full `install` runs the same at its end. Exit 0 = every tool ready,
2 = at least one tool failed.

### `scan`

See 6.1.

### `report`

```
security-toolkit report --project-root <path> [--run <run-id>]
```

Shows a run's `status.json` as a colored table. By default the latest run.

### `restore`

```
security-toolkit restore --project-root <path> --manifest <manifest.json> [--dry-run]
```

Undoes an installation: restores every file to the state that installation backed up before changing
it. The manifest: `.security/backups/<timestamp>/manifest.json`. Check first with `--dry-run` what it
would restore.

### `infra install`

```
security-toolkit infra install [--sonar] [--mssql] [--upgrade] [--sonar-port <N>] [--mssql-port <N>]
```

Installs/updates the machine-level infra. Details: [chapter 2](02-sonar-and-mssql.md).

### `infra add-test-login`

```
security-toolkit infra add-test-login <login>
```

Creates a project login on the test SQL Server, or when run again, generates a new password. Prints the
connection string ([2.3](02-sonar-and-mssql.md#23-test-sql-server-creating-a-project-login)).

### General

```
security-toolkit --version
security-toolkit <command> --help
```

**Exit codes** (for every command; the pipelines rely on them):

| Code | Meaning |
|---|---|
| `0` | success (in Report mode even with findings) |
| `1` | a blocking finding in Gate mode; or an invalid command line (unknown switch, missing required parameter), in which case the help is shown too |
| `2` | a technical or configuration error, e.g. no `.security/security.config.json` (`install` has not run yet), a missing manifest, Docker unreachable. The reason appears on an `Error: …` line on stderr |

## 6.4 The keys of `security.config.json`

| Key | Meaning |
|---|---|
| `dotnetTargets` | the .NET solutions/projects to scan (relative paths) |
| `frontendTargets` | the frontend folders to scan |
| `tools.<tool>` | on/off per tool (`analyzers`, `nuget`, `npm`, `semgrep`, `gitleaks`, `osv`, `devskim`, `sonar`) |
| `semgrepScope` | `repository` or `frontend` |
| `semgrepRulesets` | the Semgrep registry rulesets to download (`p/csharp`, `p/typescript` by default) |
| `downloadSemgrepRules` | download the rules during installation |
| `images.<tool>` | the source image of Semgrep/Gitleaks/OSV; the pinned digest is in the lock |
| `versions.<tool>` | the requested version of DevSkim/SonarScanner/dotnet-coverage; empty = the latest stable, pinned during installation |
| `sonar.url`, `sonar.projectKey`, `sonar.dotnetTarget` | the Sonar connection and the solution it builds |
| `sonar.coverage`, `sonar.testFilter`, `sonar.testEnvironment` | coverage import ([3.5](03-project-installation.md#35-sonar-coverage)) |
| `devskim.ignoreGlobs` | additional file patterns excluded from DevSkim, e.g. `["**/*.Tests/**"]` (no commas allowed). Always excluded: `.git`, `.security`, `bin`, `obj`, `node_modules`, `dist`, `.sonarqube` and the generated pipeline files |
| `devskim.ignoreRuleIds` | disabled DevSkim rules, e.g. `["DS162092", "DS172411", "DS176209"]` (localhost, setTimeout, TODO) |
| `installHook`, `installVSCodeExtensions` | git hook, VS Code extensions |
| `installPrerequisites`, `sdkChannel`, `nodeWingetId`, `nodeVersion` | install a missing SDK/Node with winget (Windows only; on Linux it only reports what is missing) |
| `generatePipeline`, `pipelineProviders`, `pipelineHistory` | whether to generate a pipeline, and for which platform (`azure`, `github`) |
| `pipelineSkipTools` | enabled tools the generated pipelines leave out, e.g. `["sonar"]` when the Sonar server is reachable from developer machines only. They stay pinned in the lock and run locally as usual. |
| `pipelineMode` | `Report` (default) or `Gate`: the mode of the generated pipeline's scan steps ([5.4](05-evaluation.md#54-report-or-gate-mode)) |
| `agentPool`, `azureVmImage`, `privateNuGet`, `privateNpmRc` | the Azure pipeline's settings ([4.2](04-pipeline.md#42-pipeline-variables-and-secrets), [4.7](04-pipeline.md#47-ephemeral-or-hosted-agent-downloading-the-cli-from-a-release)) |
| `toolkitRelease.version`, `toolkitRelease.baseUrl` | the pipeline downloads the CLI from the given release (ephemeral/hosted agent, [4.7](04-pipeline.md#47-ephemeral-or-hosted-agent-downloading-the-cli-from-a-release)); an empty version = the CLI on the machine PATH |
| `githubRunsOn`, `githubCodeScanning`, `githubSchedule` | the GitHub Actions workflow's settings ([4.6](04-pipeline.md#46-github-actions-instead-of-or-next-to-azure)) |

After changing a key: `install --project-root . --configure-only --non-interactive`. When you turn on a
tool or the coverage, a full `install` is needed.

## 6.5 Building the toolkit from source

In the SecurityToolkit repository (.NET 11 SDK):

```powershell
dotnet build SecurityToolkit.slnx
dotnet test SecurityToolkit.slnx
dotnet run --project src/SecurityToolkit.Cli -- scan --project-root C:\Work\<repo>   # from source, without installing
dotnet publish src/SecurityToolkit.Cli/SecurityToolkit.Cli.csproj -c Release -r win-x64   # self-contained exe (on Linux: -r linux-x64 / linux-arm64)
```

For real use, always use `install.bat` (`install.sh` on Linux): it publishes and installs onto the machine
PATH.

### Releasing

For ephemeral and hosted agents, the toolkit is also published as a GitHub Release
([4.7](04-pipeline.md#47-ephemeral-or-hosted-agent-downloading-the-cli-from-a-release)). A version tag
releases it:

```powershell
git tag -a v1.2.0 -m "v1.2.0"
git push origin v1.2.0
```

`.github/workflows/release.yml` then runs the tests, builds the `win-x64`, `linux-x64` and `linux-arm64`
self-contained binaries (`-p:Version=1.2.0`), and puts them into a release with the `SHA256SUMS` file. A
tag with a hyphen (e.g. `v1.3.0-rc.1`) becomes a pre-release. Never replace a released version: the target
projects pinned its checksum, and their pipelines fail on a replaced file. Release a new version for a
fix.

The version of a CLI built from source (`install.bat`/`install.sh`, `dotnet run`) is `0.0.0-dev`
(`security-toolkit --version`).

[Next: 7. Troubleshooting →](07-troubleshooting.md)
