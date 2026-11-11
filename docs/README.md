# SecurityToolkit – installation and operations guide

**English** · [Magyar](hu/README.md)

Starting from **an empty Windows (or Linux) machine and an empty Azure DevOps project**, this guide walks
you through everything a green security pipeline needs. Follow the chapters in order, as each builds on
the previous one. Every step ends with a **Check** item: move on only once it holds.

## What is this system?

SecurityToolkit is a project-independent security scanning toolkit for .NET and Angular repositories. It
has three layers:

```
┌──────────────────────────────────── CI machine ───────────────────────────────────┐
│                                                                                   │
│  security-toolkit  (Windows: C:\Program Files\SecurityToolkit\bin,                │
│                     Linux: /opt/security-toolkit/bin; on the machine PATH)        │
│        ▲                                                                          │
│        │ calls                                                                    │
│  Azure DevOps self-hosted agent (Windows or systemd service)                      │
│                                                                                   │
│  Docker ──► shared SonarQube + PostgreSQL  (every project uses one server,        │
│         ──► shared test SQL Server          with its own key / its own login)     │
│         ──► scanner containers (Semgrep, Gitleaks, OSV)                           │
└───────────────────────────────────────────────────────────────────────────────────┘

┌───────────────────────────── target project (any repo) ───────────────────────────┐
│  .security/                    configuration, lock, rules                         │
│  azure-security-toolkit.yml    generated pipeline                                 │
│  managed blocks                .gitignore, .semgrepignore, Directory.Build.props, │
│                                .editorconfig                                      │
└───────────────────────────────────────────────────────────────────────────────────┘
```

1. **The CLI** (`security-toolkit`): installed once per machine with `install.bat` (`install.sh` on
   Linux). The same CLI installs into projects, runs the scans and manages the machine-level infra.
2. **The machine-level infra**: the shared SonarQube server and a test SQL Server, in Docker. Once per
   machine, also installed by `install.bat` / `install.sh`.
3. **The project installation**: `security-toolkit install` generates the `.security/` folder and the
   pipeline in the target project. Once per repository, then commit it.

The tools it runs: .NET analyzers, NuGet audit, npm audit, Semgrep, Gitleaks, OSV-Scanner, DevSkim and
SonarQube (with coverage). In detail: [06 – Local runs and CLI reference](06-local-runs-and-cli.md#62-the-8-scanners).

## Chapters

| # | Chapter | For | How often |
|---|---|---|---|
| 1 | [Preparing the CI machine and the Azure DevOps agent](01-machine-and-agent.md) | operator | once per machine |
| 2 | [The shared Sonar and the test SQL Server](02-sonar-and-mssql.md) | operator | once per machine + maintenance |
| 3 | [Installing into a target project](03-project-installation.md) | developer | once per repository + on updates |
| 4 | [Setting up and running the pipeline](04-pipeline.md) | developer | once per repository |
| 5 | [Evaluating the results and triage](05-evaluation.md) | security owner, developer | after every run |
| 6 | [Local runs and the full CLI reference](06-local-runs-and-cli.md) | developer | as needed |
| 7 | [Troubleshooting](07-troubleshooting.md) | everyone | when something does not work |
| 8 | [LLM-assisted evaluation and remediation](08-llm-assisted-remediation.md) | developer + coding agent | when working through findings regularly |

## Quick path (the whole process on one page)

1. CI machine: Docker Desktop, .NET SDK, Node, Git, Chrome → `install.bat --sonar --mssql` as
   administrator → install the Azure DevOps agent as a service → restart the agent service. On Linux:
   Docker Engine, `sudo bash install.sh --sonar --mssql`, agent with `svc.sh`.
   ([Chapter 1](01-machine-and-agent.md))
2. Sonar: first login, password change, create the project **by hand**, a project analysis token for the
   pipeline; developers get *Execute Analysis*, and locally everyone uses their own user token.
   Test SQL: `security-toolkit infra add-test-login <project>_test`. ([Chapter 2](02-sonar-and-mssql.md))
3. Developer machine (with Docker): `install.bat --no-infra` (on Linux `sudo bash install.sh --no-infra`),
   then in the repository `security-toolkit install --project-root .` → commit the generated files.
   ([Chapter 3](03-project-installation.md))
4. Azure DevOps: a pipeline from `azure-security-toolkit.yml`, add `SONAR_TOKEN` and the other secrets,
   first run, permit the pool access. On GitHub the same with an Actions workflow.
   ([Chapter 4](04-pipeline.md))
5. Results: the `security-results` artifact (`summary.md`), the Scans tab, the Sonar UI.
   ([Chapter 5](05-evaluation.md))

## Conventions

- `<angle brackets>`: replace with your own value (e.g. `<org>` = the name of the Azure DevOps
  organization).
- Commands run in PowerShell unless marked otherwise (the `security-toolkit` commands work the same from
  bash on Linux). Steps marked **as administrator** need an elevated terminal, `sudo` on Linux.
- In the examples the agent pool is called `SecurityToolkit` and the Sonar project `MyApp`. These are
  only examples.
