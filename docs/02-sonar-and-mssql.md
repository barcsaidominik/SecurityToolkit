# 2. The shared Sonar and the test SQL Server

**English** · [Magyar](hu/02-sonar-es-mssql.md)

[← 1. CI machine and agent](01-machine-and-agent.md) · [Contents](README.md) · [Next: 3. Installing into a project →](03-project-installation.md)

The toolkit manages two **machine-level**, project-independent services in Docker:

| Service | What it is for | Access |
|---|---|---|
| **SonarQube + PostgreSQL** | static analysis, security hotspots, coverage, Quality Gate | `http://<machine>:9000`, also from the network |
| **Test SQL Server** (MSSQL 2022 Developer) | the projects' database tests for the Sonar coverage run | on the machine only: `127.0.0.1,14333` |

Both are **one** instance per machine. Every project uses the same Sonar, with its own project key, and
the same SQL Server, with its own login.

## 2.1 Installation

If you answered yes to both questions of `install.bat` (or ran it with `--sonar --mssql`), this is
already done. Separately, or later:

```powershell
security-toolkit infra install --sonar --mssql
```

Switches:

| Switch | Meaning |
|---|---|
| `--sonar` | install/start SonarQube + PostgreSQL |
| `--mssql` | install/start the test SQL Server |
| `--sonar-port <N>` | Sonar host port (default: the existing one, 9000 on the first install) |
| `--mssql-port <N>` | SQL Server loopback port (default: the existing one, 14333 on the first install) |
| `--upgrade` | update the images (see 2.5); without it, the pinned version stays |

The command is **idempotent**: you can rerun it any time, passwords and image versions do not change.

### What goes where

```
C:\ProgramData\SecurityToolkit\infra\
  sonar\
    compose.yml   ← the toolkit's template, overwritten on every run; do NOT edit it
    .env          ← created once: DB password, port, memory limit, digest-pinned images
  mssql\
    compose.yml
    .env          ← SA password, port, image pin
    sql\          ← the login creation script
```

- The `.env` is readable only by the user who created it, SYSTEM and the Administrators group. Opening it
  needs administrator rights.
- **On Linux** the same lives under `/var/lib/security-toolkit/infra/` (`sonar/`, `mssql/`); the `.env`
  is owned by root and `chmod 600`. Run the `infra` commands with `sudo`. Use this path instead of the
  `C:\ProgramData\SecurityToolkit\infra\...` paths below.
- **On Linux, Sonar needs `vm.max_map_count` ≥ 262144** (Sonar's built-in Elasticsearch does not start
  below it, and the container ends up in a restart loop). Ubuntu 26.04 sets 1048576 by default, many
  other distributions only 65530. `infra install --sonar` checks this before starting; with a value that
  is too low it writes nothing and stops (exit 2) with the command to set it. The toolkit does not
  change the system parameter on its own:
  ```bash
  sudo sysctl -w vm.max_map_count=262144
  echo 'vm.max_map_count=262144' | sudo tee /etc/sysctl.d/99-security-toolkit-sonar.conf
  ```
  On Windows, Docker Desktop runs its own VM and sets this itself; the toolkit does not check it.
- **The values in `.env` (passwords, image pins) never change on their own.** If you edit one by hand
  (e.g. `SONAR_MEM_LIMIT=6g`), the next `infra install` keeps it.
- The compose project names are fixed: `security-toolkit-sonar` and `security-toolkit-mssql`. Sonar's
  data lives in the Docker volumes named `security-toolkit-sonar_*`.

**Check:**
```powershell
docker ps --format "{{.Names}}  {{.Status}}  {{.Ports}}"
```
`security-toolkit-sonar-db-1` (healthy), `security-toolkit-sonar-sonarqube-1` and
`security-toolkit-mssql-mssql-1` (healthy) must be running. Sonar's first start takes 1–3 minutes.

## 2.2 Setting up Sonar for the first time

### First login

1. In a browser: `http://localhost:9000` (on the CI machine) or `http://<machine>:9000`.
2. User `admin`, password `admin`. Sonar asks for a new password right away. Store the new admin password
   in your password manager.

### Access from the network

Developers' machines need to reach Sonar too (for the browser and SonarLint). On the CI machine, as
administrator:

```powershell
New-NetFirewallRule -DisplayName "SonarQube 9000" -Direction Inbound -Protocol TCP -LocalPort 9000 -Action Allow -Profile Domain
```

If the machine has a DNS name (e.g. `ci-machine.<domain>`), use that as the Sonar URL in the projects.

> **TLS:** Sonar runs on plain HTTP here. That is acceptable on an internal network. If it were reachable
> from outside, put a reverse proxy (IIS/nginx) with a certificate in front of it. The toolkit does not
> install one.

### Creating a project – always by hand

1. *Projects* → *Create project* → **Create a local project** (*Manually*).
2. *Project display name* and **Project key**: e.g. `MyApp`. Note the key down, it becomes the project's
   `sonar.projectKey`.
3. *Main branch name*: the repository's main branch (`master`/`main`).
4. *New code*: "Previous version" or "Number of days", as the team decides.

> **Do not choose "With Azure DevOps".** It generates an internal key that differs from the display
> name (`<AzureProject>_<repo>_<GUID>`). The toolkit will not upload with that key, and the error looks
> like a permission error. If it was created that way anyway: *Project Settings* → *Update Key* → change
> it to a human-readable key.

### Project analysis token

1. On the project page: *Project Settings* → *Permissions*. Check that the user generating the token has
   **Execute Analysis**. Sonar administrator rights do **not** grant it automatically.
2. Top-right corner → *My Account* → *Security* → *Generate Tokens*:
   - *Type*: **Project Analysis Token**, *Project*: the project above;
   - *Expires in*: per your organization's policy (e.g. 1 year).
3. The token is shown only once. It becomes the pipeline's `SONAR_TOKEN` secret
   ([chapter 4](04-pipeline.md#42-pipeline-variables-and-secrets)). **It goes into the pipeline only**,
   never onto a developer machine: locally everyone uses their own user token (see below).

**Check** (from any machine that reaches Sonar):
```powershell
$token = Read-Host 'Token'
curl.exe -s -u "${token}:" "http://<machine>:9000/api/authentication/validate"
```
Answer: `{"valid":true}`.

### Personal user token (for local runs)

The two tokens have different roles and are not interchangeable:

| | Project analysis token | User token |
|---|---|---|
| For | the pipeline (one per project) | the developer (one per person) |
| Scope | a single project, analysis upload only | every project the user has rights to; upload **and** API reads (issues, hotspots) |
| Where it lives | in the pipeline's `SONAR_TOKEN` secret variable | on the developer's machine, in the `SONAR_TOKEN` environment variable, per session |

Since there can be only one `SONAR_TOKEN` locally, work with **a single user token** in every project.
The project is selected by `sonar.projectKey` in `security.config.json`, not by the token.

1. For every project the developer uploads to locally, the Sonar admin grants them (or the developers'
   group) **Execute Analysis**: *Project Settings* → *Permissions*. For reading only (e.g. the session of
   [chapter 8](08-llm-assisted-remediation.md)), *Browse* is enough.
2. The developer: *My Account* → *Security* → *Generate Tokens* → *Type*: **User Token**, *Expires in*:
   per your organization's policy.
3. Usage: per session, `$env:SONAR_TOKEN = Read-Host 'Sonar user token'`
   ([6.1](06-local-runs-and-cli.md#61-the-scan-command)).

> **Do not store it with `setx`.** `setx` writes the token unencrypted into the registry
> (`HKCU\Environment`), and every later process inherits it. If you do not want to type it every time,
> load it into the session from a secret store (e.g. the PowerShell `SecretManagement` module).

> **Do not use the project analysis token locally.** It works for one project only: in another project it
> fails with "not authorized", and with several projects you would keep swapping it.

### Quality Gate

The default *Sonar way* Quality Gate sets conditions on **new code** (coverage ≥ 80%, duplication,
reliability/security ratings). The coverage condition can only pass if the coverage import is on
([3.5](03-project-installation.md#35-sonar-coverage)).

- **In Report mode** (the default) the pipeline does not wait for the Quality Gate, it only uploads.
- **In Gate mode** the Sonar step waits for the Quality Gate result, and a failing Quality Gate fails the
  step ([5.4](05-evaluation.md#54-report-or-gate-mode)).

## 2.3 Test SQL Server: creating a project login

Every project runs with its own SQL login. It gets `CREATE ANY DATABASE`, so its tests can create and drop
their own databases. It is deliberately **not** `dbcreator`, because that could drop any other project's
database too.

On the CI machine:

```powershell
security-toolkit infra add-test-login myapp_test
```

- The login name starts with a letter and contains only letters, digits and `_` (3–64 characters).
- The command prints the full connection string, password included. **This is the only time you see the
  password.** Copy it into the pipeline secret right away ([4.2](04-pipeline.md#42-pipeline-variables-and-secrets)).
- Running it again generates a **new password**; the old one stops working.

The printed connection string looks like this:

```
Server=127.0.0.1,14333;User Id=myapp_test;Password=<password>;Encrypt=True;TrustServerCertificate=True
```

> **`127.0.0.1`, not `localhost`.** The container port listens on the IPv4 loopback only. `localhost` can
> also resolve to IPv6 `::1`, where nobody listens, so the SQL client fails with "actively refused" or
> "The wait operation timed out", and the database tests fail en masse.

**Check** (in Windows PowerShell 5.1, on the CI machine):
```powershell
$cs = Read-Host 'Connection string'
$c = New-Object System.Data.SqlClient.SqlConnection "$cs;Connect Timeout=15"; $c.Open(); $c.ServerVersion; $c.Close()
```
It prints a version number (e.g. `16.00.xxxx`).

> **The SQL Server has no data volume, because test data is disposable.** A plain restart (`docker
> restart`, a machine restart) keeps the logins. A `docker compose down` + `up` deletes everything,
> though: afterwards **every project's login has to be created again**, and the pipeline secrets updated.

## 2.4 Day-to-day operation

Work in the compose folder (elevated PowerShell):

```powershell
cd C:\ProgramData\SecurityToolkit\infra\sonar
docker compose ps                    # status
docker compose logs -f sonarqube     # live Sonar log
docker compose restart sonarqube     # restart Sonar (data is kept)
```

Both stacks are `restart: unless-stopped`, so they start by themselves when Docker (Desktop, or the Engine
service on Linux) starts.

**Memory:** Sonar's limit is `SONAR_MEM_LIMIT` in the `.env` (default `4g`), SQL Server's is
`MSSQL_MEMORY_LIMIT_MB` (default `2048`) plus a 3 GB container limit. After a change:
`security-toolkit infra install --sonar` (or `--mssql`).

**Port:** `security-toolkit infra install --sonar --sonar-port 9100`. This is a machine setting; the
projects' `sonar.url` has to be changed too.

## 2.5 Updating, backup, moving

### Image update

```powershell
security-toolkit infra install --sonar --upgrade
```

This pulls the source tags again (`sonarqube:community`, `postgres:17`) and pins the new digest in the
`.env`. **Take a backup first** (below). After a larger Sonar version jump, Sonar may start in
maintenance mode. Then open `http://<machine>:9000/setup` and start the database migration.

> The toolkit pins the `postgres` major version (`17`). A PostgreSQL major upgrade needs a
> dump/restore, which `--upgrade` does not do.

### Backup (recommendation)

Sonar's whole state (projects, tokens, triage decisions) is in the PostgreSQL database:

```powershell
docker exec security-toolkit-sonar-db-1 pg_dump -U sonar -d sonar -Fc -f /tmp/sonar.dump
docker cp security-toolkit-sonar-db-1:/tmp/sonar.dump D:\Backup\sonar-$(Get-Date -Format yyyyMMdd).dump
```

Schedule it weekly (Task Scheduler), and move the backup to another machine. Save
`C:\ProgramData\SecurityToolkit\infra\sonar\.env` alongside it: it holds the DB password.

### Moving to a new machine (recommendation; not yet tried in production)

1. On the new machine, go through [chapter 1](01-machine-and-agent.md), but run `install.bat` with
   **`--no-infra`**.
2. Copy the old machine's `sonar\.env` to the new machine, to the same place:
   `C:\ProgramData\SecurityToolkit\infra\sonar\.env`. This keeps the DB password and the image version.
3. `security-toolkit infra install --sonar`. It starts with an empty database.
4. Stop Sonar, restore the dump, and start it again:
   ```powershell
   cd C:\ProgramData\SecurityToolkit\infra\sonar
   docker compose stop sonarqube
   docker cp D:\Backup\sonar.dump security-toolkit-sonar-db-1:/tmp/sonar.dump
   docker exec security-toolkit-sonar-db-1 pg_restore -U sonar -d sonar --clean --if-exists /tmp/sonar.dump
   docker compose start sonarqube
   ```
5. Simply reinstall the test SQL Server (`infra install --mssql`) and create the logins again. They get
   new passwords, so the pipeline secrets have to be updated.

> **If the `.env` is lost but the `security-toolkit-sonar_postgres_data` volume still exists**,
> `infra install` deliberately refuses to install. With a new password, Sonar would lock itself out of
> its own database. Either restore the `.env` from the backup, or delete the volume on purpose
> (`docker volume rm security-toolkit-sonar_postgres_data`). The latter deletes every Sonar data and
> token, for all projects.

## 2.6 Checklist

- [ ] All three containers are running (`docker ps`)
- [ ] Sonar: admin password changed, firewall rule, reachable from developer machines
- [ ] Sonar project created **by hand**, key noted down
- [ ] Project analysis token (for the pipeline), `api/authentication/validate` → `valid: true`
- [ ] Developers running locally have *Execute Analysis* on the project; everyone uses their own **user token**
- [ ] Test SQL login created, the connection string (127.0.0.1) stored, the test connection works
- [ ] Weekly Sonar backup scheduled, the `.env` saved

[Next: 3. Installing into a project →](03-project-installation.md)
