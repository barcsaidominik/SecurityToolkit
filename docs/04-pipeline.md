# 4. Setting up and running the pipeline

**English** · [Magyar](hu/04-pipeline.md)

[← 3. Installing into a project](03-project-installation.md) · [Contents](README.md) · [Next: 5. Evaluation →](05-evaluation.md)

The installer generates `azure-security-toolkit.yml` in the repository root. Create an Azure DevOps
pipeline from it, set the secrets, and run it. You need this once per repository. For a repository on
GitHub, the same is available as a GitHub Actions workflow, see
[4.6](#46-github-actions-instead-of-or-next-to-azure).

## 4.0 One-time organization setting: the SARIF Scans tab

The pipeline also shows the Semgrep and Gitleaks findings on a **Scans** tab. For that, install the
**SARIF SAST Scans Tab** extension (Microsoft DevLabs, `sariftools.scans`) once in the organization:
*Organization settings* → *Extensions* → *Browse marketplace*. This needs organization admin rights.
Without the extension the pipeline works the same, only the Scans tab is missing.

## 4.1 Creating the pipeline

1. Azure DevOps → *Pipelines* → **New pipeline**.
2. *Where is your code?* → **Azure Repos Git** → pick the repository.
3. *Configure your pipeline* → **Existing Azure Pipelines YAML file**.
4. *Branch*: the branch you committed the toolkit to. *Path*: `/azure-security-toolkit.yml` → *Continue*.
5. With the arrow next to the *Run* button, choose **Save** (do not run it yet, the secrets come first).
6. Rename it: in the pipeline list … → *Rename/move*, e.g. `<repo> Security`.

> **Do not edit the YAML by hand.** The toolkit manages it: every installation regenerates it, and a
> manual change shows up as drift ([3.7](03-project-installation.md#37-maintenance-and-updates)). If you
> need something different, change `security.config.json` and install again.

## 4.2 Pipeline variables and secrets

In the pipeline editor: *Edit* → **Variables** → *New variable*:

| Name | Value | Secret? | When needed |
|---|---|---|---|
| `SONAR_TOKEN` | the **project analysis token** generated in [2.2](02-sonar-and-mssql.md#project-analysis-token) (not the personal user token) | **yes** (*Keep this value secret*) | if Sonar is on |
| every *value* of `sonar.testEnvironment` (e.g. `TestDbConnectionString`) | e.g. the connection string from [2.3](02-sonar-and-mssql.md#23-test-sql-server-creating-a-project-login) | **yes** | with the coverage import, if the tests need it |

> **Why does the YAML need the `env:` block?** Azure DevOps does **not** pass *secret* variables to the
> scripts' environment automatically. The generated YAML therefore maps them explicitly in the Sonar
> step:
> ```yaml
> env:
>   SONAR_TOKEN: $(SONAR_TOKEN)
>   ConnectionStrings__Default: $(TestDbConnectionString)
> ```
> So adding a new secret to the pipeline is not enough: add it to `sonar.testEnvironment` too, and
> regenerate the YAML ([3.5](03-project-installation.md#35-sonar-coverage)).

> The pipeline and the developer machine use **different tokens** under the same name: the pipeline uses
> the project's analysis token (a separate secret variable per project), the developer locally their own
> user token ([2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)). Do not copy the pipeline's
> token to a developer machine.

Secrets never get into the logs or the reports: the toolkit replaces every occurrence of `SONAR_TOKEN`
with `[REDACTED]` in its output.

### Private NuGet / npm feed

If you chose `privateNuGet: true` during installation, the YAML starts with a `NuGetAuthenticate@1` step.
It needs read access to the feed for the project's build identity: *Artifacts* → the feed → *Feed
settings* → *Permissions* → *Add users/groups* → **`<Project> Build Service (<org>)`**, role *Feed
Reader*. For private npm (`privateNpmRc`) the same, plus an `npmAuthenticate@0` step for the given
`.npmrc`.

## 4.3 First run

1. *Run pipeline* → pick the branch → *Run*.
2. On the first run you may see: *"This pipeline needs permission to access a resource before this run
   can continue"*. → *View* → **Permit**. This allows this pipeline to use the agent pool. If *Grant
   access permission to all pipelines* was on for the pool, it does not appear.
3. A run typically takes 10–20 minutes; the Sonar step with coverage is the longest.

### The structure of the pipeline

| Step | What it does |
|---|---|
| `checkout` | full clone (`fetchDepth: 0`), so Gitleaks and Sonar see the history |
| `NuGetAuthenticate` | only with a private NuGet feed |
| **Initialize pinned security tools** | `security-toolkit provision --require-pinned`: pulls the Docker images with the digests pinned in the lock, installs the .NET tools of the pinned versions under `.security/bin/`. If something is not pinned, it fails here ([3.4](03-project-installation.md#34-unattended-installation---non-interactive)). |
| **Security scan: `<tool>`** | a separate `security-toolkit scan --tool <tool> --mode Report --run-id $(securityRunId)` step for every enabled tool, with `condition: always()` |
| `PublishCodeCoverageResults@2` | only with `sonar.coverage: true`: the .NET (`dotnet-coverage.xml`) and Angular (`lcov.info`) coverage → the run's **Code Coverage** tab |
| `PublishPipelineArtifact` | `.security/results` → the **`security-results`** artifact |
| `CopyFiles` + `PublishBuildArtifacts` | the `*.sarif` files → the `CodeAnalysisLogs` artifact (this becomes the Scans tab) |

- **Every tool is a separate step**, running with `condition: always()`. If one fails, the rest still
  run.
- **A shared `--run-id`** (the build number). Every step writes into the same `results/<build number>/`
  folder, and `status.json` is merged per tool. At the end of the run there is a single, complete
  `status.json` and `summary.md`.
- **Exit codes per step**, always computed from the step's own tool only:

  | Exit | Meaning | The step |
  |---|---|---|
  | `0` | the tool ran (with or without findings) | green |
  | `1` | a blocking finding in Gate mode | red |
  | `2` | a technical error (the tool could not run) | red |

- The generated pipeline runs in **Report mode** by default: it never turns red because of a finding,
  only because of a technical error. With `"pipelineMode": "Gate"` every scan step runs in Gate mode and
  turns red on a blocking finding (exit 1). The other steps still run. In an older project, record a
  baseline first, so that only new findings fail the run ([5.4](05-evaluation.md#54-report-or-gate-mode)).

**Check:**
- every step ran, and the `Security scan: …` steps are green;
- the run's *Summary* page has a **`security-results`** artifact (and a `CodeAnalysisLogs` one);
- with Sonar: the analysis appeared on the Sonar project page, with the time of the run;
- with the extension: the run has a **Scans** tab;
- with the coverage import on: the coverage report is on the **Code Coverage** tab.

## 4.4 Scheduling

The YAML has `trigger: none` and `pr: none`: the pipeline does not start by itself. For scheduled runs
(e.g. at night, outside the Windows Update window), set it in the pipeline UI: *Edit* → … → **Triggers**
→ *Scheduled* → *Add*. A schedule set in the UI overrides the YAML, so the YAML needs no change.

## 4.5 Checklist

- [ ] (once per organization) the SARIF SAST Scans Tab extension
- [ ] Pipeline created from `azure-security-toolkit.yml` and renamed
- [ ] `SONAR_TOKEN` and the `testEnvironment` variables added as secrets
- [ ] With a private feed: Feed Reader rights for the Build Service
- [ ] First run: pool access permitted, every step ran
- [ ] `security-results` artifact and Scans tab, the analysis shows in Sonar
- [ ] Schedule set (if needed)

## 4.6 GitHub Actions (instead of or next to Azure)

If the repository is on GitHub, the installer can generate a GitHub Actions workflow instead of (or next
to) the Azure pipeline: `.github/workflows/security-toolkit.yml`. The `pipelineProviders` list in
`security.config.json` decides:

| `pipelineProviders` | What gets generated |
|---|---|
| `["azure"]` (default) | only `azure-security-toolkit.yml`, as before |
| `["github"]` | only the GitHub workflow |
| `["azure", "github"]` | both |

If you remove a platform from the list, its previously generated file stays, it is just no longer
updated. The installer says so in a message; deleting it is your decision.

The workflow has the same structure as the Azure one: `provision --require-pinned`, then a separate step
for every enabled tool, with a shared `--run-id` (`<run_id>-<run_attempt>`, so a re-run does not mix with
the previous one), and at the end `.security/results` → the **`security-results`** artifact. The steps run
with `if: ${{ !cancelled() }}`: one tool's failure does not stop the others, but a manual cancellation is
respected. The mode here also depends on `pipelineMode` (**Report** by default).

### Runner

There are two options:

- **GitHub-hosted runner** (e.g. `githubRunsOn: ["ubuntu-latest"]`): this needs `toolkitRelease.version`;
  the workflow then downloads the CLI as its first step and verifies its checksum, see
  [4.7](#47-ephemeral-or-hosted-agent-downloading-the-cli-from-a-release). The `ubuntu-latest` image has
  Docker and the .NET SDK, nothing else is needed. A hosted runner usually cannot reach the shared Sonar,
  though, so turn the Sonar tool off there, or make Sonar reachable from the internet.
- **Self-hosted runner** with `security-toolkit` on the machine PATH (the same machine and the same
  `install.bat` / `install.sh` as for the Azure agent, see [chapter 1](01-machine-and-agent.md)). Its
  setup is below.

> **Manual runs work only from the default branch.** The workflow starts with `workflow_dispatch` (and
> optionally `githubSchedule`). GitHub offers the *Run workflow* button only once the file is on the
> default branch (e.g. `main`) too; a workflow on a feature branch cannot be started that way. So the
> first run comes after the merge.

Setting up a self-hosted runner:

1. Repository → *Settings* → *Actions* → *Runners* → **New self-hosted runner**, and follow GitHub's
   installation steps. Install the runner as a service (when `config.cmd` asks on Windows, with
   `./svc.sh install` on Linux).
2. The runner's service account has to see Docker (on Linux it must be in the `docker` group).
3. After `install.bat` / `install.sh`, **restart the runner service**; otherwise it keeps running with the
   old PATH.

`githubRunsOn` sets the `runs-on` labels (default `["self-hosted"]`). If you have several self-hosted
runners, give the security machine its own label, e.g. `["self-hosted", "security-toolkit"]`.

The workflow does not set a shell, because the steps are simple CLI calls. On Linux bash runs, on Windows
pwsh, or the built-in Windows PowerShell if pwsh is not installed. So nothing extra needs to be
installed.

### Secrets

Repository → *Settings* → *Secrets and variables* → *Actions* → **New repository secret**:

| Name | When needed |
|---|---|
| `SONAR_TOKEN` | if Sonar is on (the project's analysis token, as on Azure) |
| every *value* of `sonar.testEnvironment` | with the coverage import, if the tests need it |

GitHub secret names cannot contain a dot and cannot start with `GITHUB_`. If the GitHub workflow is
generated too, the installer validates the `testEnvironment` values accordingly. For a name shared by both
platforms, choose one without a dot, e.g. `TEST_DB_CONNECTION`.

Private NuGet/npm feed authentication (`privateNuGet`, `privateNpmRc`) adds a step to the Azure pipeline
only. On GitHub, set up feed access on the runner machine (e.g. in the user's `nuget.config`).

### Code scanning and scheduling

- `githubCodeScanning: true`: at the end of the run, the SARIF reports (Semgrep, Gitleaks, DevSkim) are
  also uploaded to the repository's **Security → Code scanning** page. In a private repository this needs
  a **GitHub Advanced Security** (Code Security) license, without which this step fails. That is why it
  is off by default. The SARIF files are in the `security-results` artifact anyway.
- `githubSchedule`: a five-field cron expression **in UTC**, e.g. `"0 2 * * 1-5"` (weekdays at 02:00
  UTC). When empty, the workflow only starts manually (*Actions* → *Security Toolkit* → **Run
  workflow**). GitHub has no UI schedule like Azure, hence the separate key.

Every action in the workflow is pinned to a full commit SHA (not a tag), for the same reason the scanner
images are pinned by digest. Action updates come with toolkit updates. If Dependabot's `github-actions`
updates are on in the repository and they rewrite this file too, the installer sees that as a manual
change: the next `install` skips the file and says so
([3.7](03-project-installation.md#37-maintenance-and-updates)). So do not merge Dependabot PRs for this
file: close them, or leave the `github-actions` ecosystem out of `dependabot.yml`.

**Check:**
- the **Security Toolkit** workflow appears on the *Actions* tab, and started manually, every step runs;
- the run's *Summary* page has the **`security-results`** artifact;
- if enabled: the findings appear on the *Security → Code scanning* page.

## 4.7 Ephemeral or hosted agent: downloading the CLI from a release

By default both pipelines expect `security-toolkit` to be on PATH on the agent already (`install.bat` /
`install.sh`, [chapter 1](01-machine-and-agent.md)). An agent that starts fresh for every run
(Microsoft-hosted, GitHub-hosted, scale set) does not have it. In that case, set in `security.config.json`
which released version the pipeline downloads:

```json
"toolkitRelease": { "version": "1.2.0" }
```

Then run a **full** install (without `--configure-only`). The installer downloads the release's
`SHA256SUMS`, pins the hashes of the three binaries in `security.lock.json`, and writes an install step
into the pipeline before the provision:

- on a Linux agent (x64 and arm64), bash: downloads the binary with `curl`, verifies the pinned hash with
  `sha256sum`, and puts it at the front of PATH;
- on a Windows agent (x64), cmd: the same with `curl.exe` and `certutil`. No PowerShell here either.

If the downloaded file's hash does not match the pinned one (the release was replaced afterwards, or
someone tampered with the download), the step fails, and the pipeline does not run the file. If the
hashes are not pinned yet (only `--configure-only` ran), the step stops with a message saying so.

**Changing the version:** change `toolkitRelease.version`, run a full install, and commit the changes of
the config, the lock and the pipeline. If the machines use a CLI installed with `install.bat`, leave
`version` empty: then the pipeline downloads nothing.

**Your own mirror:** if the agent cannot reach GitHub, `toolkitRelease.baseUrl` can point to an internal
server with the same layout (`<baseUrl>/v<version>/<file>`, with `SHA256SUMS`). Only `https://` URLs are
accepted.

**Choosing a hosted agent:**
- Azure: `"azureVmImage": "ubuntu-latest"`. The YAML then gets `pool: vmImage:`, and `agentPool` does not
  matter.
- GitHub: `"githubRunsOn": ["ubuntu-latest"]`.

With a hosted agent, mind these:
- **Pick a Linux image.** Semgrep, Gitleaks and OSV run in Linux containers, while the Docker of the
  Windows hosted agents runs Windows containers only.
- **The Sonar server has to be reachable** from the agent. A cloud agent cannot reach a Sonar on an
  internal network; in that case turn the Sonar step off, or stay with the self-hosted agent.
- The provision pulls the images and the .NET tools again on every run, so the run is slower than on a
  warm self-hosted machine.

**Check:** in the run log the *Install security-toolkit &lt;version&gt;* step is green, and the next step
(*Initialize pinned security tools*) finds the `security-toolkit` command.

[Next: 5. Evaluation →](05-evaluation.md)
