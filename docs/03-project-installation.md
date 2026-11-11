# 3. Installing into a target project

**English** · [Magyar](hu/03-telepites-projektbe.md)

[← 2. Sonar and test SQL](02-sonar-and-mssql.md) · [Contents](README.md) · [Next: 4. Pipeline →](04-pipeline.md)

The project installation generates the `.security/` folder, the pipeline YAML and a few managed blocks in
any .NET and/or Angular (npm) repository. You need it once per repository, then commit the generated
files. After that, other developers and the CI have nothing to do with the repository.

## 3.1 Prerequisites on the machine doing the installation

Do the installation on a **developer machine** (not on the CI machine), which needs:

- Git, the .NET SDK (the one the project's `global.json` asks for), Node (if there is a frontend);
- **Docker (Desktop, or Docker Engine on Linux), running.** The full installation pulls the scanner
  images and pins them by digest in `security.lock.json`. Without Docker only `--configure-only` works,
  and that does not produce a runnable pipeline (see 3.4);
- the SecurityToolkit CLI: in a clone of the SecurityToolkit repository, `install.bat --no-infra` **as
  administrator**, then a new terminal ([1.3](01-machine-and-agent.md#13-installing-the-securitytoolkit-cli-installbat));
  on Linux `sudo bash install.sh --no-infra` ([1.7](01-machine-and-agent.md#17-linux-ci-machine-instead-of-windows));
- the Sonar URL and project key ([2.2](02-sonar-and-mssql.md#22-setting-up-sonar-for-the-first-time)), if
  you use Sonar too.

The working tree of the repository should be clean. A separate branch is a good idea:

```powershell
cd C:\Work\<repo>
git switch -c security-toolkit
```

## 3.2 Interactive installation

```powershell
security-toolkit install --project-root .
```

If there is no `.security/security.config.json` yet, the full wizard starts. For yes/no questions the
answer is `y`/`n`; Enter = the default, shown in capitals in the square brackets.

### Targets

| Question | What to do |
|---|---|
| **.NET solution/project targets** | pick which of the listed `.sln`/`.slnx`/`.csproj` to scan (numbers separated by commas; Enter = all; 0 = none). Usually the main solution. |
| **Frontend/npm target directories** | among the folders with a `package.json`, the real frontend(s), e.g. `src/Client`. |

### Tools

It asks about all 8 tools (`Enable …?`, [their descriptions](06-local-runs-and-cli.md#62-the-8-scanners)):

| Tool | Recommendation |
|---|---|
| .NET security analyzers | **yes**, if there is a .NET target |
| Semgrep CE | **yes** |
| Gitleaks | **yes** |
| NuGet Audit | **yes**, if there is a .NET target |
| npm audit | **yes**, if there is a frontend (needs a `package-lock.json`) |
| OSV-Scanner | optional, a second dependency check |
| DevSkim | optional, additional lint; worth it only when narrowed (`devskim.ignoreGlobs`/`ignoreRuleIds`, see [6.4](06-local-runs-and-cli.md#64-the-keys-of-securityconfigjson)) |
| SonarScanner for .NET | **yes**, if there is a shared Sonar |

### Further questions

| Question | Recommendation / explanation |
|---|---|
| Semgrep scope | `repository` = the whole repository, C# included. `frontend` = only the selected frontends, without `*.cs`. Semgrep CE analyzes modern C# incompletely (parser errors), so if Sonar runs too, `frontend` is the better choice. |
| Install missing system dependencies with winget? | convenient on a developer machine. If yes: SDK channel (e.g. `10.0`), Node winget ID and version. On Linux this question is skipped (see [1.7](01-machine-and-agent.md#17-linux-ci-machine-instead-of-windows)). |
| Download the vendor Semgrep registry rules and pin them locally? | **yes**. The `p/csharp` and `p/typescript` rulesets go under `.security/rules/` and get committed, so the CI runs without internet access, with pinned rules. |
| Sonar URL | the address of the shared Sonar, e.g. `http://ci-machine.<domain>:9000` |
| Sonar project key | the key created by hand in [2.2](02-sonar-and-mssql.md#creating-a-project--always-by-hand) |
| Single Sonar .NET target | Sonar builds and analyzes this solution |
| Measure test coverage and upload it to Sonar? | see [3.5](#35-sonar-coverage). If yes: a `dotnet test --filter` expression (empty = all tests), then the test env vars as `ENV_NAME=PipelineVariableName`, one per line, ended with an empty line. |
| Install the fast Gitleaks staged pre-commit hook? | **yes** (see 3.6) |
| Install the VS Code extensions of the selected tools? | yes, if the `code` command is available (SonarLint, DevSkim) |
| Generate a separate, manually run security pipeline? | **yes**, this becomes the pipeline; it then asks whether to generate Azure DevOps and/or GitHub Actions |
| Leave Sonar out of the generated pipeline (the Sonar server is reachable from developer machines only)? | `y` if the CI cannot reach your Sonar (e.g. a GitHub-hosted runner and a Sonar on your own machine). Sonar stays enabled and pinned, and runs locally with `--include-sonar` (`pipelineSkipTools`, see [6.4](06-local-runs-and-cli.md#64-the-keys-of-securityconfigjson)). |
| Azure self-hosted agent pool name | the pool created in [1.4](01-machine-and-agent.md#14-creating-an-agent-pool-in-azure-devops), e.g. `SecurityToolkit` |
| Should Gitleaks scan the whole reachable git history? | `n` = the working tree only. `y` = every earlier commit too. Useful once at the first rollout, noisy afterwards. |
| Add a NuGetAuthenticate task for a private feed? | **yes**, if the project uses a private Azure Artifacts NuGet feed |
| Relative path of the private npm .npmrc | empty if there is no private npm feed |

### Local settings and finishing

- **Override the Sonar URL locally?** Only `y` if you use a different (e.g. local) Sonar on your own
  machine. This goes into the gitignored `.security/security.config.local.json` and does not touch the
  committed config.
- **Run a full Report scan after the installation?** Useful as a test on the first installation, but the
  Sonar upload does not run then (that needs `--include-sonar` and your own **user token** in
  `SONAR_TOKEN`, see [2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)).

### What the installer does

1. Writes the `.security/` folder: config, lock, `gitleaks.toml`, `SecurityToolkit.props`.
2. Writes managed blocks (between the `# BEGIN SECURITY-TOOLKIT` … `# END SECURITY-TOOLKIT` markers; it
   never touches content outside the block):
   - `.gitignore`: excludes the runtime files;
   - `.semgrepignore`: build output, `node_modules`, `.security/`;
   - the nearest `Directory.Build.props`: imports `.security/SecurityToolkit.props` (analyzers + NuGet
     audit, active only in the toolkit's builds);
   - the project folders' `.editorconfig`: the CA30xx/CA31xx security rules at `warning` level.
3. Downloads the Semgrep rules under `.security/rules/`.
4. **Provisioning:** pulls the Docker images, pins the digests in `security.lock.json`, and installs/pins
   the .NET tools (DevSkim, SonarScanner, dotnet-coverage) under `.security/bin/`.
5. Installs the git hook and the VS Code extensions (if requested).
6. Generates `azure-security-toolkit.yml` (and/or `.github/workflows/security-toolkit.yml` on GitHub, see
   `pipelineProviders`, [4.6](04-pipeline.md#46-github-actions-instead-of-or-next-to-azure)).
7. Prints the remaining manual steps to the console and to `.security/pending-actions.md`.

Every changed file is backed up under `.security/backups/<timestamp>/` (see 3.7, `restore`).

**Check:**
```powershell
security-toolkit install --project-root . --check
```
Exit code 0 and an "In sync" message. Read `.security/pending-actions.md` too.

## 3.3 What gets committed

| File / folder | Commit? | Note |
|---|---|---|
| `.security/security.config.json` | **yes** | the team's shared settings |
| `.security/security.lock.json` | **yes** | the tools' pinned versions/digests; the CI relies on it with `provision --require-pinned` |
| `.security/SecurityToolkit.props`, `gitleaks.toml` | **yes** | files managed ("owned") by the toolkit, do not edit them by hand |
| `.security/rules/` | **yes** | the pinned Semgrep rules |
| `.security/security.config.local.json.example` | **yes** | a template for the local override |
| `azure-security-toolkit.yml`, `.github/workflows/security-toolkit.yml` | **yes** | the generated pipeline (whichever you asked for); do not edit by hand |
| `.gitignore`, `.semgrepignore`, `Directory.Build.props`, `.editorconfig` | **yes** | changed because of the managed blocks |
| `.security/baseline.json` | **yes**, if present | the accepted (known) blocking findings; written by `scan --update-baseline` ([5.4](05-evaluation.md#baseline-block-only-on-new-findings)) |
| `.security/security.config.local.json` | no (gitignored) | machine-specific override |
| `.security/results/`, `bin/`, `backups/`, `npm-tools/`, `vendor/` | no (gitignored) | runtime data |
| `.security/install-state.json`, `pending-actions.md` | no (gitignored) | local installation state |

```powershell
git add -A
git status          # check that only the files above are in it
git commit -m "Install SecurityToolkit"
git push -u origin security-toolkit
```

If the pre-commit hook is already installed, a Gitleaks scan runs before the commit (it needs Docker).
If it finds something, the commit is not created (see 3.6).

## 3.4 Unattended installation (`--non-interactive`)

From a script, or when the answers are already in a profile:

```powershell
security-toolkit install --project-root . --non-interactive --config-file .\security-profile.json
```

An example `security-profile.json` (only the keys that differ from the defaults are needed; the installer
discovers the targets by itself if you leave them out):

```json
{
  "dotnetTargets": [ "src/MyApp.sln" ],
  "frontendTargets": [ "src/Client" ],
  "tools": { "analyzers": true, "nuget": true, "npm": true, "semgrep": true, "gitleaks": true, "sonar": true },
  "semgrepScope": "frontend",
  "installHook": true,
  "agentPool": "SecurityToolkit",
  "privateNuGet": true,
  "sonar": {
    "url": "http://ci-machine.example.com:9000",
    "projectKey": "MyApp",
    "dotnetTarget": "src/MyApp.sln"
  }
}
```

> **`--configure-only` alone is not enough for a new project.** It only writes the files; it does not pin
> the Docker image digests and tool versions, so the pipeline's `Initialize pinned security tools` step
> fails with "Pin … first". For a new project, or when you turn on a new tool, a full installation
> (without `--configure-only`) is always needed once.

## 3.5 Sonar coverage

By default Sonar uploads static analysis only. With the coverage import, the Sonar step runs the tests
between the build and the upload, and uploads the coverage too. The Quality Gate's new-code coverage
condition needs this.

1. The interactive installer asks about it after the Sonar questions (see 3.2), or by hand in the `sonar`
   section of `.security/security.config.json`:
   ```json
   "sonar": {
     "url": "http://ci-machine.example.com:9000",
     "projectKey": "MyApp",
     "dotnetTarget": "src/MyApp.sln",
     "coverage": true,
     "testFilter": "",
     "testEnvironment": {
       "ConnectionStrings__Default": "TestDbConnectionString"
     }
   }
   ```
   - `coverage`: on/off.
   - `testFilter`: an optional `dotnet test --filter` expression, e.g. `Category!=Slow`.
   - `testEnvironment`: **environment variable name → Azure pipeline variable NAME** (not its value!).
     The pipeline writes it into the Sonar step's `env:` block: `ConnectionStrings__Default: $(TestDbConnectionString)`.
     Set the value (e.g. the connection string from [2.3](02-sonar-and-mssql.md#23-test-sql-server-creating-a-project-login))
     in the pipeline's secret variable ([4.2](04-pipeline.md#42-pipeline-variables-and-secrets)).
     The names must be identifiers (letters, digits, `_`), and `SONAR_TOKEN` cannot be overridden.
2. A **full** installation, because the `dotnet-coverage` version has to be pinned too:
   ```powershell
   security-toolkit install --project-root . --non-interactive
   ```
3. Commit the changes of `security.config.json`, `security.lock.json` and the generated pipeline file(s).

What it runs:

- **.NET:** `dotnet-coverage collect … dotnet test <dotnetTarget> --no-build [--filter …]`.
- **Angular:** in every frontend target with an `angular.json`, `npm ci` (if there is no `node_modules`),
  then a separate `ng test` for every project of the `angular.json` that has a `test` target:
  - `@angular/build:karma` (or another `…:karma`) builder: `--code-coverage --karma-config=<generated>`,
    with headless Chrome;
  - `@angular/build:unit-test` builder with `runner: karma`: `--coverage --runner-config=<generated>`,
    with headless Chrome;
  - `@angular/build:unit-test` builder with Vitest (its default runner; new Angular projects are created
    this way): `--coverage --coverage-reporters=lcovonly`. The project's own Vitest settings (e.g.
    `browsers`, `runnerConfig`) stay in effect. The lcov goes to Angular's default `coverage/<project>/`
    folder, and the toolkit moves it from there into the run's results folder (deleting a report left
    there by an earlier local run first). **The project needs a coverage provider** as a dev
    dependency: `npm i -D @vitest/coverage-v8` (or `@vitest/coverage-istanbul`); otherwise `ng test`
    stops with an error, and it becomes a `no report` note. If the project's own Vitest config sets a
    different `coverage.reportsDirectory`, the toolkit does not find the report;
  - any other builder (e.g. Jest): skipped with a warning.
  - Karma finds Chrome by itself. Point to a browser elsewhere with the `CHROME_BIN` environment variable.

> **Coverage never fails the Sonar step.** If a test fails, there is no report, or a tool is missing, the
> step stays `Completed`, and only a `Coverage: …` note in the result shows the problem
> ([5.3](05-evaluation.md#coverage-notes)).

## 3.6 Git hook and VS Code

**Pre-commit hook** (`installHook: true`): `security-toolkit scan --staged` runs before every
`git commit`. It scans only the changes to be committed (staged) with Gitleaks, and **always blocks** if
it finds a secret. The hook calls the CLI on PATH, so the machine needs the CLI (`install.bat`) and a
running Docker. If the repository already had a pre-commit hook, it keeps it as
`pre-commit.security-toolkit-original` and runs it first.

The hook lives under `.git/hooks`, so it runs on every branch. If the current branch has no
`.security/security.config.json` (e.g. an older branch, or a source master without the toolkit), it skips
the scan with a warning, and the commit goes through.

**Husky:** if the repository's hooks are managed by Husky (`core.hooksPath` points to a `.husky/_` or
`.husky` folder), the installer **writes no hook**. Husky regenerates the `.husky/_` folder on every
`npm install`, and `.husky/pre-commit` is a committed script that applies to the whole team. Instead the
installer prints which file to add this line to:
```sh
security-toolkit scan --project-root "$(git rev-parse --show-toplevel)" --staged
```
The team decides on and commits that. Afterwards everyone needs the CLI and Docker, otherwise the commit
fails.

> Do not bypass the hook with `--no-verify`. If the hook finds a real secret, take it out of the commit.
> If it is a false positive, handle it as described in [5.5](05-evaluation.md#55-triage-and-false-positives).

**VS Code extensions** (`installVSCodeExtensions: true`, if `code` is on PATH): *SonarQube for IDE*
(SonarLint) for Sonar, the DevSkim extension for DevSkim.

## 3.7 Maintenance and updates

| Command | What for |
|---|---|
| `install --project-root . --check` | Only reports which managed files differ from what the current toolkit would generate. Writes nothing. Exit 0 = in sync, 1 = drift. |
| `install --project-root . --dry-run` | Prints the resolved configuration, writes nothing. |
| `install --project-root . --configure-only --non-interactive` | Regenerates the files from the current config, without provisioning (e.g. after a config change, if you did not turn on a new tool). |
| `install --project-root . --non-interactive` | Full reinstallation without questions, from the existing config. |
| `restore --project-root . --manifest .security\backups\<timestamp>\manifest.json [--dry-run]` | Undoes an installation from the backup. |

**Rolling out a new toolkit version in a project:**

1. In the SecurityToolkit repository, `git pull`, then `install.bat --no-infra` as administrator (on the
   CI machine `install.bat`, then restart the agent service). On Linux `sudo bash install.sh --no-infra`.
2. In the project: `security-toolkit install --project-root . --check`. It shows what will change.
3. `security-toolkit install --project-root . --non-interactive`, then commit the changes.
4. **The installer deletes retired toolkit files.** These are the PowerShell scripts of earlier versions
   (`.security/Initialize-SecurityTools.ps1`, `Restore-SecurityToolkit.ps1`, `Toolkit.Common.ps1`,
   `Invoke-SecurityScan.ps1`), replaced today by `security-toolkit provision` and `restore`. It deletes
   only the ones not changed by hand, with a backup (`restore` brings them back); the ones changed by hand
   stay and are listed in `pending-actions.md`. Commit the deletion too.

> **Manual change to an owned file.** The toolkit manages `.security/SecurityToolkit.props`,
> `gitleaks.toml`, the lock and the pipeline YAML, and protects them with hashes. If someone changed one
> by hand, the installation **does not overwrite that one file and silently leaves it as it was**,
> updates the rest, and lists the skipped file among the manual steps (`pending-actions.md`). `--check`
> lists them as `ManualChangesDetected`. Fix: either revert the manual change (`git checkout -- <file>`)
> and run the installation again, or move the change into the toolkit's source and release a new
> version. The safest is to always start an update with `--check`.
>
> The protection relies on the hashes in the gitignored `.security/install-state.json`. **In a fresh
> clone or on a new machine** that file does not exist yet, so the committed owned files count as the
> baseline state, and the installation may overwrite them. The change then shows in `git diff`; review it
> before committing.

[Next: 4. Pipeline →](04-pipeline.md)
