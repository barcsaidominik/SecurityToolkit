# SecurityToolkit

**English** · [Magyar](README.hu.md)

[![CI](https://github.com/barcsaidominik/SecurityToolkit/actions/workflows/ci.yml/badge.svg)](https://github.com/barcsaidominik/SecurityToolkit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A project-independent security scanning toolkit for .NET and Angular repositories, on Windows and
Linux. One CLI, `security-toolkit`, installs itself into any repository, runs the scanners
locally, in a pre-commit hook and in a generated CI pipeline, and manages the shared machine-level
infrastructure (a SonarQube server and a test SQL Server).

## What it runs

| Scanner | What it finds |
|---|---|
| .NET security analyzers | CA30xx/CA31xx security rules (SQL injection, path traversal, XSS, unsafe XML processing …) at build time |
| NuGet audit, npm audit | known vulnerable packages |
| Semgrep CE | static analysis of C# and JS/TS, with registry rules pinned into the repository |
| Gitleaks | secrets in the working tree, in staged changes (pre-commit) and in git history |
| OSV-Scanner | a second opinion on dependency vulnerabilities |
| DevSkim | additional security linting |
| SonarQube | server-side analysis with .NET and Angular (Karma or Vitest) test coverage |

Every scanner can be switched on or off per repository. Docker images are pinned by digest and
.NET tools by version in a committed lock file, so CI runs exactly what was reviewed.

## How it works

- **Machine level, once per machine:** `install.bat` (Windows) or `install.sh` (Linux) publishes
  the CLI as a self-contained binary onto the system PATH, and can install the shared SonarQube +
  PostgreSQL and a test SQL Server in Docker.
- **Project level, once per repository:** `security-toolkit install` writes a `.security/` folder
  (config, lock, rules), managed blocks in `.gitignore`, `.semgrepignore`, `Directory.Build.props`
  and `.editorconfig`, a Gitleaks pre-commit hook, and an Azure DevOps pipeline and/or GitHub
  Actions workflow. Every file it changes is backed up and can be restored.
- **Every run:** `security-toolkit scan` runs the enabled scanners and writes `status.json` and a
  Markdown `summary.md`. In **Gate** mode it fails on secrets, Semgrep errors and high/critical
  package vulnerabilities; with a committed baseline only *new* findings fail it.

## Exit codes and failure handling

| Exit | Meaning |
|---|---|
| `0` | every scanner that ran completed (in Report mode, with or without findings) |
| `1` | Gate mode (or the `--staged` pre-commit scan) found a blocking finding |
| `2` | a technical failure: a scanner could not run, exited with an unexpected code, or wrote a missing or unreadable report |

A scanner failure is never reported as a clean result: it is recorded as `failed` in `status.json`
and the run exits with `2` in both modes. Semgrep only counts as complete when every parse error
it reports is a warn-level partial parse; any other analysis error fails the step. In Gate mode a
failing SonarQube Quality Gate makes the Sonar step fail, so it shows up as exit `2`, not `1`.
Disabled tools are skipped, not reported.

## Scope and limitations

The toolkit runs and pins existing open-source scanners and turns their output into one report and
one gate decision. It is not a penetration test, it does not run dynamic (DAST) testing, and a clean
run does not mean the code is free of vulnerabilities:

- Only Gitleaks, Semgrep `ERROR` findings and High/Critical NuGet/npm advisories can block. The .NET
  analyzers, OSV-Scanner and DevSkim are inventory only and never block.
- The CA30xx taint rules see MVC controller action parameters only, not minimal API endpoints.
  Semgrep CE analyzes one file at a time, without cross-file data flow.
- Gitleaks is pattern-based, so a secret in an unrecognized format can pass, and the dependency
  audits only know about published advisories.
- A baseline entry for Gitleaks is keyed on the rule, file and redacted match, so a different secret
  written to exactly the same key in the same file counts as already known.

## Quick start

Requirements: the .NET SDK pinned in `global.json`, Git, Docker (for Semgrep, Gitleaks,
OSV-Scanner and the shared infra) and, for frontend scans, Node.js.

Windows, from an elevated command prompt in the repository root:

```bat
install.bat --no-infra
```

Linux, in the repository root:

```bash
sudo bash install.sh --no-infra
```

Then, from a new terminal, in the repository you want to protect:

```
security-toolkit install --project-root .
security-toolkit scan --project-root .
```

Drop `--no-infra` (or pass `--sonar` / `--mssql`) to also install the shared SonarQube and test
SQL Server. `security-toolkit --help` lists every command.

Releases (`v*` tags) publish self-contained binaries for `win-x64`, `linux-x64` and `linux-arm64`
with a `SHA256SUMS` file. A target project can pin one (`toolkitRelease` in
`.security/security.config.json`) so that its pipeline downloads and verifies the CLI on hosted,
ephemeral agents.

## Documentation

**[Installation and operations guide →](docs/README.md)**: from an empty machine to a green
pipeline: preparing the CI machine and agent, the shared Sonar and SQL Server, installing into a
project, the pipeline, triaging the results, the full CLI reference, troubleshooting and
LLM-assisted remediation. A Hungarian translation is in [docs/hu/](docs/hu/README.md).

## Building from source

```
dotnet build SecurityToolkit.slnx
dotnet test SecurityToolkit.slnx
```

## Security

See [SECURITY.md](SECURITY.md) for how to report a vulnerability.

## License

[MIT](LICENSE)
