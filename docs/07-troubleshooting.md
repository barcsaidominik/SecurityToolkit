# 7. Troubleshooting

**English** · [Magyar](hu/07-hibaelharitas.md)

[← 6. Local runs and CLI](06-local-runs-and-cli.md) · [Contents](README.md) · [Next: 8. LLM-assisted remediation →](08-llm-assisted-remediation.md)

Look the problem up by its symptom. Almost all of these have happened in real use.

## Agent and machine

| Symptom | Cause | Fix |
|---|---|---|
| In the pipeline: `'security-toolkit' is not recognized…` | the agent service still runs with the old PATH | `Get-Service vstsagent* \| Restart-Service` ([1.5](01-machine-and-agent.md#15-installing-the-agent-as-a-windows-service)). In a terminal: open a new one. |
| `permission denied … npipe:////./pipe/docker_engine`, or `docker version` has no *Server* part | `NETWORK SERVICE` is not a member of the `docker-users` group | `Add-LocalGroupMember -Group docker-users -Member "NT AUTHORITY\NETWORK SERVICE"`, then restart the agent |
| `error during connect` / Docker is not running after a restart | no user is signed in, so Docker Desktop did not start | Autologon ([1.2](01-machine-and-agent.md#automatic-sign-in-autologon)). Set it up again after a password change. |
| A night run is interrupted | Windows Update restarted the machine | align the Active hours / maintenance window with the schedule ([4.4](04-pipeline.md#44-scheduling)) |
| The machine slows down, the Sonar container stops or restarts | not enough memory | 16 GB of RAM, `.wslconfig` ([1.2](01-machine-and-agent.md#the-memory-limit-of-docker-desktop-wslconfig)), check `SONAR_MEM_LIMIT` |
| Linux: `permission denied while trying to connect to the Docker daemon socket` | the agent's (or your) user is not in the `docker` group, or the membership is not in effect yet | `sudo usermod -aG docker <user>`, then sign in again, or `sudo ./svc.sh stop && sudo ./svc.sh start` ([1.7](01-machine-and-agent.md#the-agent-and-docker)) |
| Linux: `security-toolkit: command not found` in the pipeline | the agent's PATH reflects the state at configuration time (`.path`), and it lacks `/usr/local/bin` | add `/usr/local/bin` to the agent's `.path` file, then restart the agent |
| Linux: `pre-commit: /bin/sh^M: bad interpreter` | a hook with CRLF line endings from an old toolkit version | update the toolkit, then `security-toolkit install --project-root .` (the hook is LF now) |
| Linux: `UnauthorizedAccessException` / `Access to the path '/var/lib/security-toolkit/...' is denied` | the `infra` command needs root | `sudo security-toolkit infra ...` |
| Linux: root-owned files under `.security/results/`, the checkout's `clean` cannot delete them | an old toolkit version, or a scan run as root | `sudo rm -rf .security/results`, update the toolkit; do not run the scan as root |

## Installation and the pipeline's *Initialize* step

| Symptom | Cause | Fix |
|---|---|---|
| `Error: Could not find a part of the path '…/.security/security.config.json'` (exit 2) | the toolkit is not installed in this repository (or on this branch) yet | `security-toolkit install --project-root .` ([chapter 3](03-project-installation.md)) |
| `Image must be pinned first by the installer.` | there is no digest in the lock (only `--configure-only` ran, or a new tool was turned on) | a full `security-toolkit install --project-root . --non-interactive` on a machine with Docker, then commit `security.lock.json` ([3.4](03-project-installation.md#34-unattended-installation---non-interactive)) |
| `Pin the .NET tool version first.` | the same for DevSkim / SonarScanner / dotnet-coverage | the same |
| The installation runs, but a file is not updated | someone changed the owned file by hand, so the installer skipped it | `install --check` → `ManualChangesDetected`; revert the manual change ([3.7](03-project-installation.md#37-maintenance-and-updates)) |
| An old `.security/*.ps1` (e.g. `Toolkit.Common.ps1`) is still there after the install | it was changed by hand, so the installer did not delete it (`pending-actions.md` says so) | if not needed, delete it by hand and commit |
| The old config is called `toolkit.json` / `toolkit.lock.json` | a leftover of an earlier iteration that never went live; there is no migration | delete it and install again |

## Scanners

| Symptom | Cause | Fix |
|---|---|---|
| `sonar … not-run` | no `--include-sonar` | the pipeline passes it by itself; add it locally ([6.1](06-local-runs-and-cli.md#61-the-scan-command)) |
| `Set the SONAR_TOKEN environment variable …` | no token in the step's environment | pipeline: the `SONAR_TOKEN` secret variable ([4.2](04-pipeline.md#42-pipeline-variables-and-secrets)); locally: `$env:SONAR_TOKEN` with your own user token ([2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)) |
| Sonar `begin` runs, `end` fails with "not authorized" or "project not found" | (a) wrong `sonar.projectKey`: the project was created with the "With Azure DevOps" option, with an internal GUID key; (b) the token's owner has no *Execute Analysis* (locally: your user); (c) locally, another project's analysis token is in `SONAR_TOKEN` | (a) Sonar: *Project Settings* → *Update Key*, or `curl -u "<token>:" "<sonar>/api/components/search?qualifiers=TRK"` for the real key; (b) grant the permission ([2.2](02-sonar-and-mssql.md#project-analysis-token)); (c) use your own user token ([2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)) |
| Gitleaks: many findings under `.sonarqube/` | an old toolkit version (Sonar's working folder was not excluded) | update the toolkit, then `install` ([3.7](03-project-installation.md#37-maintenance-and-updates)) |
| Scans tab: "File not found" for a Gitleaks finding | in `--history` mode it points to a file in an old commit that has moved since | the finding is real; look the file up in the history by the commit ID |
| No Scans tab | the SARIF SAST Scans Tab extension is not installed | [4.0](04-pipeline.md#40-one-time-organization-setting-the-sarif-scans-tab) |
| npm: `npm audit requires package-lock.json or npm-shrinkwrap.json` | the frontend is not managed with npm (yarn/pnpm), or there is no lock file | `npm install` to create the lock file, or turn the npm tool off |
| Semgrep `failed`, but there are findings | a file could not be parsed at all (often C#), or a timeout/rule error happened; a merely partial parse error (`Partially parsed`) does not fail it | the `frontend` scope, if Sonar runs too ([5.2](05-evaluation.md#52-per-tool-where-the-details-are-what-they-mean)) |
| Because of an earlier step's failure, the later steps are red too | an old toolkit version (it computed the exit code from the merged result) | update the toolkit |

## Coverage

| Symptom | Cause | Fix |
|---|---|---|
| Database tests: `actively refused` or `The wait operation timed out` | the connection string has `localhost`; the container listens on IPv4 only | `Server=127.0.0.1,14333;…` ([2.3](02-sonar-and-mssql.md#23-test-sql-server-creating-a-project-login)) |
| Database tests: `Login failed for user` | the SQL Server container was recreated (`down`/`up`), the logins were lost | `infra add-test-login <login>` again, and update the pipeline secret |
| Many tests fail with `The entry point exited without ever building an IHost` | the application fails at startup (e.g. a config value is missing that developers keep in user-secrets). `Program.cs` catches the exception, so the test sees only this. | starting the host locally with the same environment shows the real error. Fix on the application side: the test fixture provides a test value, or the missing value comes through `sonar.testEnvironment` and a pipeline variable. |
| Angular: `Schema validation failed … codeCoverage` | an old toolkit version ran an `@angular/build:unit-test` project with `--code-coverage` | update the toolkit |
| Angular: `no report (exit 1)`, a compile error in the log (e.g. `JSON does not support trailing commas`) | the frontend does not compile | fix the error in the frontend (the project team's job) |
| Angular: no browser found | there is no Chrome on the machine | install Chrome machine-wide, or `CHROME_BIN` |
| Sonar: Quality Gate *Failed* because of coverage | no coverage import, or many tests fail | [3.5](03-project-installation.md#35-sonar-coverage), or fix the tests |

## Infra

| Symptom | Cause | Fix |
|---|---|---|
| `infra install` refuses: the `security-toolkit-sonar_postgres_data` volume exists, but there is no `.env` | the `.env` was lost, and with a new password Sonar would lock itself out of its own database | restore the `.env` from the backup, or (losing all data) delete the volume ([2.5](02-sonar-and-mssql.md#25-updating-backup-moving)) |
| On Linux, `infra install --sonar`: `vm.max_map_count = 65530 …` | the kernel limit is lower than what Sonar's Elasticsearch needs | the `sysctl` command in the error message, then run the infra installation again ([2.1](02-sonar-and-mssql.md#21-installation)) |
| Sonar is in maintenance mode after an update | a database migration is needed | `http://<sonar>:9000/setup` |
| `docker compose up failed` | a port in use, not enough memory, or Docker is not running | `docker compose logs` in the compose folder ([2.4](02-sonar-and-mssql.md#24-day-to-day-operation)) |

## Git and repository

| Symptom | Cause | Fix |
|---|---|---|
| On commit: `security-toolkit: not found` | git runs from a shell or GUI started before the CLI was installed | a new terminal, or restart the git client (IDE). Do not use `--no-verify`. |
| On commit, the hook warns that there is no `.security/security.config.json` and skips the scan | the hook runs on every branch, and this branch has no toolkit installed | expected; the commit goes through. Install the toolkit on the branch if it should be scanned. |

## If none of these fits

1. Look at the tool's log in the `security-results` artifact (`<tool>*.log`). The *Message* column of
   `summary.md` names the error.
2. Run the same locally, that one tool only: `security-toolkit scan --project-root . --tool <tool>`.
3. `.security/results/setup-status.json` shows the errors of the *Initialize* step.
