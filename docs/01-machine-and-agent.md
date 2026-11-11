# 1. Preparing the CI machine and the Azure DevOps agent

**English** · [Magyar](hu/01-gep-es-agent.md)

[← Contents](README.md) · [Next: 2. Sonar and test SQL →](02-sonar-and-mssql.md)

This chapter turns an empty Windows machine into a working **self-hosted Azure DevOps agent** that has
the SecurityToolkit CLI and Docker. You need this once per machine. For a **Linux CI machine** (or a
Linux developer machine), [1.7](#17-linux-ci-machine-instead-of-windows) describes the differences.

> **Why a self-hosted agent?** The generated pipeline calls the `security-toolkit` command installed on
> the machine, and the shared Sonar server runs on the same machine in Docker. On a Microsoft-hosted
> (ephemeral) agent, the pipeline can download a pinned CLI release instead (`toolkitRelease`, see
> [4.7](04-pipeline.md#47-ephemeral-or-hosted-agent-downloading-the-cli-from-a-release)), but the shared
> Sonar server is usually not reachable from there.

## 1.1 Hardware and operating system

| | Minimum | Recommended |
|---|---|---|
| OS | Windows 10/11 Pro or Windows Server, 64-bit | Windows 11 Pro / Enterprise |
| RAM | 16 GB | 16 GB or more |
| Disk | 60 GB free | 100 GB SSD |
| CPU | 4 cores | 8 cores |
| Virtualization | enabled in the BIOS (for WSL2 / Docker) | |

> **8 GB of RAM is not enough.** On 8 GB, the Docker WSL2 VM, Sonar and a .NET build together squeezed
> the machine down to about 300 MB of free memory. 16 GB is the practical minimum.

**Windows Update:** the machine may restart at night and break a running pipeline. Set the *Active
hours* or a maintenance window so that it does not overlap with pipeline runs.

## 1.2 System-wide software

Install every piece of software **machine-wide** (for all users): the agent runs under a service account
(`NETWORK SERVICE`) and **cannot see** another user's own installations or user PATH.

In an elevated PowerShell:

```powershell
winget install --id Git.Git --scope machine -e
winget install --id Microsoft.DotNet.SDK.11 -e
winget install --id OpenJS.NodeJS.LTS --scope machine -e
winget install --id Google.Chrome --scope machine -e
```

### Docker Desktop

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the WSL2 backend. The
   installer asks for WSL2 if it is missing; a restart follows.
   > License: Docker Desktop is free below 250 employees and USD 10 M annual revenue. Above that, a paid
   > subscription is required.
2. Docker Desktop → *Settings*:
   - *General* → **Start Docker Desktop when you sign in**: on.
   - *Software updates* → turn automatic updates off. An unexpected update can stop the containers in
     the middle of a run.
3. **Docker access for the service account.** Only members of the `docker-users` group can reach the
   Docker named pipe. As administrator:
   ```powershell
   Add-LocalGroupMember -Group "docker-users" -Member "NT AUTHORITY\NETWORK SERVICE"
   ```

### The memory limit of Docker Desktop (`.wslconfig`)

By default the WSL2 VM can take half of the machine's RAM. On a 16 GB machine, set it explicitly.
Create `%UserProfile%\.wslconfig` in the profile of the **user who runs Docker Desktop**:

```ini
[wsl2]
memory=8GB
```

Then run `wsl --shutdown` and restart Docker Desktop.

> The 8 GB roughly adds up like this: Sonar's `mem_limit` is 4 GB, MSSQL's 3 GB, plus PostgreSQL and
> the scanner containers. Without MSSQL, 6 GB is enough.

### Automatic sign-in (Autologon)

Docker Desktop runs in a **user session**, not as a service. If the machine restarts and nobody signs in,
there is no Docker, so no Sonar and no scanner containers. The user running Docker Desktop therefore has
to sign in automatically:

1. Download [Sysinternals Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon), run
   it as administrator, enter the user and password, then *Enable*.
2. For security, lock the screen automatically after sign-in. Create a scheduled task (*Task
   Scheduler*): trigger "At log on" with a 1-minute delay, action
   `rundll32.exe user32.dll,LockWorkStation`.

> **If this user's password changes, set up Autologon again.** Otherwise nobody signs in after a
> restart, and Docker does not start.

**Check:** restart the machine, touch nothing, and after about 3 minutes run `docker ps` remotely (or
after signing in). The command runs without errors.

## 1.3 Installing the SecurityToolkit CLI (`install.bat`)

> On Linux, `install.sh` is the counterpart, see [1.7](#17-linux-ci-machine-instead-of-windows).

1. Clone the SecurityToolkit repository to a permanent location, e.g.:
   ```powershell
   git clone https://github.com/barcsaidominik/SecurityToolkit C:\Tools\SecurityToolkit
   ```
2. From an **elevated** command prompt, in the repository root:
   ```bat
   cd C:\Tools\SecurityToolkit
   install.bat
   ```
   The script:
   - publishes a self-contained, single-file exe (`dotnet publish`), which needs the .NET 11 SDK;
   - copies it to `C:\Program Files\SecurityToolkit\bin\security-toolkit.exe`;
   - adds that folder to the **machine** PATH;
   - asks whether to install the machine-level infra (Sonar, test SQL), detailed in
     [chapter 2](02-sonar-and-mssql.md). On a CI machine, answer **Y** to both.

   Without questions: `install.bat --sonar --mssql` (CI machine), or `install.bat --no-infra` (a
   developer machine that uses the team's shared Sonar).

3. **Open a new terminal.** The old one does not see the PATH change.

**Check:**
```powershell
security-toolkit --version
security-toolkit --help
```

> **Updating:** for a new toolkit version, `git pull`, then run `install.bat` again. The hooks and
> pipelines of every project on the machine use the new version automatically, because they all call the
> exe on PATH. Then **restart the agent service** (see 1.5).

## 1.4 Creating an agent pool in Azure DevOps

1. Azure DevOps → *Organization settings* → *Agent pools* → **Add pool**.
   - *Pool type*: **Self-hosted**
   - *Name*: e.g. `SecurityToolkit`. You enter the same name during the project installation
     (`agentPool`).
   - *Grant access permission to all pipelines*: convenient, but safer left off. Then each pipeline has
     to be permitted on its first run (see [4.3](04-pipeline.md#43-first-run)).
2. Create a **Personal Access Token** to register the agent: top-right corner → *User settings* →
   *Personal access tokens* → *New token*.
   - *Scopes*: **Agent Pools (Read & manage)**
   - Expiry: a short one is enough, it is only needed for the registration.

## 1.5 Installing the agent as a Windows service

1. *Agent pools* → the pool → **New agent** → *Windows* → *Download*. Extract it to a short path, e.g.
   `C:\agent`. A long path can cause path-length errors during builds.
2. In an elevated PowerShell:
   ```powershell
   cd C:\agent
   .\config.cmd
   ```
   The answers:

   | Question | Answer |
   |---|---|
   | Server URL | `https://dev.azure.com/<org>` |
   | Authentication type | `PAT` (Enter), then the token created in 1.4 |
   | Agent pool | `SecurityToolkit` |
   | Agent name | default (the machine name) |
   | Work folder | default (`_work`) |
   | Run agent as service? | **Y** |
   | Enable SERVICE_SID_TYPE_UNRESTRICTED? | N (default) |
   | User account to use for the service | default: `NT AUTHORITY\NETWORK SERVICE` |

3. **Restart the agent service**: the service reads PATH and its group membership (`docker-users`) only
   when it starts:
   ```powershell
   Get-Service vstsagent* | Restart-Service
   ```

> **Repeat this after every PATH change** (`install.bat`, a new SDK/Node installation). Otherwise the
> pipeline keeps running with the old PATH and fails with "`security-toolkit` is not recognized" or
> similar.

**Check:**
1. *Agent pools* → the pool → *Agents*: the agent is **Online**.
2. Run a test pipeline in any repository (e.g. with a temporary `agent-check.yml`):
   ```yaml
   trigger: none
   pool:
     name: 'SecurityToolkit'
   steps:
   - checkout: none
   - script: |
       whoami
       security-toolkit --version
       docker version
       dotnet --version
       node -v
       git --version
     displayName: Agent environment
   ```
   Every command prints its version, and `whoami` prints `nt authority\network service`. `docker
   version` shows the *Server* part too, not only the *Client*. If the *Server* part is missing, the
   `docker-users` membership or the service restart was skipped.

## 1.6 Checklist

- [ ] 16 GB of RAM, virtualization on, Windows Update window set
- [ ] Git, .NET SDK, Node, Chrome machine-wide
- [ ] Docker Desktop: starts at sign-in, auto-update off, `.wslconfig` set
- [ ] `NT AUTHORITY\NETWORK SERVICE` in the `docker-users` group
- [ ] Autologon + automatic lock, `docker ps` works after a restart
- [ ] `install.bat` ran, `security-toolkit --version` works in a new terminal
- [ ] Agent pool and an Online agent, the service restarted after PATH changes
- [ ] Every command of the test pipeline runs

## 1.7 Linux CI machine (instead of Windows)

The CLI, the generated pipeline and the machine-level infra work the same on Linux. Ubuntu 22.04/24.04
LTS and Debian 12 are good starting points, on x64 or arm64. The hardware requirements of 1.1 apply here
too. Several steps are **simpler** than on Windows: Docker Engine is a system service, so there is no
Docker Desktop, no WSL2 memory limit and no Autologon.

### System-wide software

Install the packages with the distribution's package manager. The toolkit's `installPrerequisites`
switch (winget) does not install anything on Linux, it only reports what is missing. Required:

| Software | Note |
|---|---|
| Git | `apt install git` |
| .NET 11 SDK | [Microsoft package repository or `dotnet-install.sh`](https://learn.microsoft.com/dotnet/core/install/linux) |
| Node.js LTS | only for frontends; e.g. from the NodeSource repository |
| Docker Engine + Compose plugin | [docs.docker.com/engine/install](https://docs.docker.com/engine/install/), with the `docker-compose-plugin` package |
| Chrome or Chromium | only for Karma-based frontend coverage (`ng test`); `CHROME_BIN` for another path. Vitest runs without a browser by default |

Put everything on the machine PATH (`/usr/bin`, `/usr/local/bin`), not in a user's own folder: the agent
runs under its own account and cannot see the others'.

### Installing the CLI (`install.sh`)

In the repository root:

```bash
sudo bash install.sh                  # asks about the machine-level infra
sudo bash install.sh --sonar --mssql  # CI machine, no questions
sudo bash install.sh --no-infra       # developer machine
```

The script publishes as the calling user (`linux-x64` or `linux-arm64`, self-contained), so the `bin/`
and `obj/` folders do not end up owned by root. It then copies the binary to
`/opt/security-toolkit/bin/security-toolkit` and puts it on PATH with the
`/usr/local/bin/security-toolkit` link. `/usr/local/bin` is on the default PATH of every user and
service, so there is no PATH update and no need to reopen terminals. Updating: `git pull`, then
`sudo bash install.sh --no-infra` again.

On Linux the machine-level infra goes under `/var/lib/security-toolkit/infra/` (the `.env` is
`chmod 600`, owned by root). Run the `infra` commands with `sudo`, e.g.
`sudo security-toolkit infra add-test-login myapp_test`.

### The agent and Docker

1. Create a dedicated user for the agent and add it to the `docker` group:
   ```bash
   sudo useradd -m -s /bin/bash azagent
   sudo usermod -aG docker azagent
   ```
   > Membership of the `docker` group is effectively root on the machine. Use a dedicated account, and
   > run nothing else under it.
2. *Agent pools* → the pool → **New agent** → *Linux* → download it, and extract it as the agent user,
   e.g. under `/home/azagent/agent`. Then `./config.sh`, with the same answers as in the 1.5 table (the
   service question is not asked here).
3. Install and start it as a service:
   ```bash
   cd /home/azagent/agent
   sudo ./svc.sh install azagent
   sudo ./svc.sh start
   ```
4. **The agent saves PATH when it is configured** (the `.path` file in the agent folder). If you later
   install a tool to a location that was not on PATH then, add it there, then
   `sudo ./svc.sh stop && sudo ./svc.sh start`. The `docker` group membership also takes effect only
   after a restart.

The generated pipeline needs no changes: every step is a `security-toolkit` call (`script:`, bash on
Linux), no PowerShell is needed, and the toolkit handles every path platform-independently. On Linux the
scanner containers (Semgrep, Gitleaks, OSV) run with the agent user's uid/gid, so the report files are
not owned by root, and the next checkout (`clean: true`) can delete them.

**Check:** the test pipeline of 1.5 works here too. `whoami` prints `azagent`, and the *Server* part of
`docker version` appears once the group membership is in effect.

### Checklist (Linux)

- [ ] Git, .NET SDK, Node, Docker Engine + Compose plugin, Chrome/Chromium machine-wide
- [ ] `sudo bash install.sh` ran, `security-toolkit --version` works for any user
- [ ] A dedicated agent user in the `docker` group, the agent runs as a service with `svc.sh`
- [ ] Every command of the test pipeline runs

[Next: 2. Sonar and test SQL →](02-sonar-and-mssql.md)
