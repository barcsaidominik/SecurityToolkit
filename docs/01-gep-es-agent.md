# 1. CI-gép előkészítése és az Azure DevOps agent

[← Tartalomjegyzék](README.md) · [Következő: 2. Sonar és teszt-SQL →](02-sonar-es-mssql.md)

Ebben a fejezetben egy üres Windows gépből működő **self-hosted Azure DevOps agent** lesz, amelyen
a SecurityToolkit CLI és a Docker is elérhető. Ez gépenként egyszer kell. **Linux CI-gépre** (vagy
Linuxos fejlesztői gépre) a [1.7](#17-linux-ci-gép-windows-helyett) írja le a különbségeket.

> **Miért self-hosted agent?** A generált pipeline a gépre telepített `security-toolkit` parancsot
> hívja, és a közös Sonar-szerver is ezen a gépen fut Dockerben. Microsoft-hosted (efemer) agenten
> ez minden futáskor újratelepítést igényelne, ezt a toolkit nem támogatja.

## 1.1 Hardver és operációs rendszer

| | Minimum | Ajánlott |
|---|---|---|
| OS | Windows 10/11 Pro vagy Windows Server, 64 bit | Windows 11 Pro / Enterprise |
| RAM | 16 GB | 16 GB vagy több |
| Lemez | 60 GB szabad | 100 GB SSD |
| CPU | 4 mag | 8 mag |
| Virtualizáció | BIOS-ban engedélyezve (WSL2 / Docker miatt) | |

> **8 GB RAM nem elég.** 8 GB-on a Docker WSL2 VM, a Sonar és egy .NET build együtt
> 300 MB körüli szabad memóriáig szorította a gépet. 16 GB a gyakorlati minimum.

**Windows Update:** a gép éjszaka újraindulhat, és egy épp futó
pipeline megszakad. Állítsd be a *Használati idő* (Active hours) sávot vagy egy karbantartási ablakot
úgy, hogy ne essen pipeline-futásra.

## 1.2 Rendszerszintű szoftverek

Minden szoftvert **gépi szintre** (minden felhasználónak) kell telepíteni, mert az agent egy
szolgáltatás-fiók (`NETWORK SERVICE`) alatt fut, és **nem látja** egy másik felhasználó saját
telepítéseit vagy felhasználói PATH-ját.

Rendszergazdai PowerShellben:

```powershell
winget install --id Git.Git --scope machine -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id OpenJS.NodeJS.LTS --scope machine -e
winget install --id Google.Chrome --scope machine -e
```

### PowerShell 7 – MSI-ből, ne a Store-ból vagy wingetről

A generált pipeline első lépése `pwsh`-t (PowerShell 7) hív. A Microsoft Store-os és a winget által
telepített változat egy felhasználói „App Execution Alias”-t kap, ami **nem kerül a gépi PATH-ra**, így
a szolgáltatásként futó agent nem találja. Ezért MSI-ből kell telepíteni:

1. Töltsd le a legfrissebb `PowerShell-<verzió>-win-x64.msi` fájlt a
   [PowerShell GitHub release-oldaláról](https://github.com/PowerShell/PowerShell/releases).
2. Rendszergazdaként:
   ```powershell
   msiexec /i PowerShell-<verzió>-win-x64.msi /quiet ADD_PATH=1 ENABLE_PSREMOTING=0
   ```

### Docker Desktop

1. Telepítsd a [Docker Desktopot](https://www.docker.com/products/docker-desktop/) a WSL2 backenddel.
   A telepítő kéri a WSL2-t, ha még nincs; utána újraindítás kell.
   > Licenc: a Docker Desktop 250 fő és 10 M USD éves árbevétel alatt ingyenes. Fölötte fizetős
   > előfizetés kell.
2. Docker Desktop → *Settings*:
   - *General* → **Start Docker Desktop when you sign in**: be.
   - *Software updates* → az automatikus frissítést kapcsold ki. Egy váratlan frissítés futás
     közben leállíthatja a konténereket.
3. **A szolgáltatás-fiók hozzáférése a Dockerhez.** A Docker named pipe-ot csak a `docker-users`
   csoport tagjai érik el. Rendszergazdaként:
   ```powershell
   Add-LocalGroupMember -Group "docker-users" -Member "NT AUTHORITY\NETWORK SERVICE"
   ```

### A Docker Desktop memórialimitje (`.wslconfig`)

A WSL2 VM alapból a gép RAM-jának felét veheti el. 16 GB-os gépen állítsd be kifejezetten. Hozd
létre a **Docker Desktopot futtató felhasználó** profiljában a `%UserProfile%\.wslconfig` fájlt:

```ini
[wsl2]
memory=8GB
```

Utána `wsl --shutdown`, majd indítsd újra a Docker Desktopot.

> A 8 GB nagyjából ennyiből áll össze: a Sonar `mem_limit`-je 4 GB, az MSSQL-é 3 GB, plusz a
> PostgreSQL és a scanner-konténerek. Ha nem futtatsz MSSQL-t, 6 GB is elég.

### Automatikus bejelentkezés (Autologon)

A Docker Desktop **felhasználói munkamenetben** fut, nem szolgáltatásként. Ha a gép újraindul, és
senki nem jelentkezik be, nincs Docker, így nincs Sonar és nincsenek scanner-konténerek sem. Ezért a
Docker Desktopot futtató felhasználónak automatikusan be kell jelentkeznie:

1. Töltsd le a [Sysinternals Autologont](https://learn.microsoft.com/sysinternals/downloads/autologon),
   futtasd rendszergazdaként, add meg a felhasználót és a jelszót, majd *Enable*.
2. Biztonsági okból zárold a képernyőt automatikusan a bejelentkezés után. Hozz létre egy
   ütemezett feladatot (*Task Scheduler*): eseményindító „At log on”, 1 perc késleltetéssel, művelet
   `rundll32.exe user32.dll,LockWorkStation`.

> **Ha ennek a felhasználónak megváltozik a jelszava, az Autologont újra be kell állítani.**
> Különben az újraindítás után nincs bejelentkezés, és nem indul a Docker.

**Ellenőrzés:** indítsd újra a gépet, ne nyúlj semmihez, majd kb. 3 perc múlva távolról (vagy
bejelentkezés után) futtasd: `docker ps`. A parancs hiba nélkül lefut.

## 1.3 A SecurityToolkit CLI telepítése (`install.bat`)

> Linuxon az `install.sh` a megfelelője, ld. [1.7](#17-linux-ci-gép-windows-helyett).

1. Klónozd a SecurityToolkit repót egy állandó helyre, pl.:
   ```powershell
   git clone https://dev.azure.com/<org>/<projekt>/_git/SecurityToolkit C:\Tools\SecurityToolkit
   ```
2. **Rendszergazdai** parancssorból, a repó gyökerében:
   ```bat
   cd C:\Tools\SecurityToolkit
   install.bat
   ```
   A script:
   - self-contained, egyfájlos exe-t publikál (`dotnet publish`), ehhez kell a .NET 10 SDK;
   - bemásolja ide: `C:\Program Files\SecurityToolkit\bin\security-toolkit.exe`;
   - hozzáadja ezt a mappát a **gépi** PATH-hoz;
   - megkérdezi, telepítse-e a gépi infrát (Sonar, teszt-SQL). Ezt a [2. fejezet](02-sonar-es-mssql.md)
     részletezi. CI-gépen mindkettőre **I** a válasz.

   Kérdések nélkül: `install.bat --sonar --mssql` (CI-gép), vagy `install.bat --no-infra`
   (fejlesztői gép, ahol a csapat közös Sonarját használjátok).

3. **Nyiss új terminált.** A régiben a PATH-változás nem látszik.

**Ellenőrzés:**
```powershell
security-toolkit --version
security-toolkit --help
```

> **Frissítés:** új toolkit-verziónál `git pull`, majd `install.bat` újra. A gépen lévő összes projekt
> hookja és pipeline-ja automatikusan az új verziót használja, mert mind a PATH-on lévő exe-t hívja.
> Utána **indítsd újra az agent-szolgáltatást** (lásd 1.5).

## 1.4 Agent pool létrehozása az Azure DevOps-ban

1. Azure DevOps → *Organization settings* → *Agent pools* → **Add pool**.
   - *Pool type*: **Self-hosted**
   - *Name*: pl. `SecurityToolkit`. Ugyanezt a nevet kell majd megadni a projekt-telepítéskor
     (`agentPool`).
   - *Grant access permission to all pipelines*: kényelmes, de biztonságosabb kikapcsolva hagyni.
     Ekkor az első futtatáskor pipeline-onként kell engedélyezni (lásd [4.3](04-pipeline.md#43-első-futtatás)).
2. Hozz létre egy **Personal Access Tokent** az agent regisztrálásához: jobb felső sarok →
   *User settings* → *Personal access tokens* → *New token*.
   - *Scopes*: **Agent Pools (Read & manage)**
   - Lejárat: rövid is elég, mert csak a regisztrációhoz kell.

## 1.5 Az agent telepítése Windows-szolgáltatásként

1. *Agent pools* → a pool → **New agent** → *Windows* → *Download*. Csomagold ki egy rövid útvonalra,
   pl. `C:\agent`. A hosszú útvonal a build közben path-length hibát okozhat.
2. Rendszergazdai PowerShellben:
   ```powershell
   cd C:\agent
   .\config.cmd
   ```
   A kérdésekre:

   | Kérdés | Válasz |
   |---|---|
   | Server URL | `https://dev.azure.com/<org>` |
   | Authentication type | `PAT` (Enter), majd a 1.4-ben létrehozott token |
   | Agent pool | `SecurityToolkit` |
   | Agent name | alapértelmezett (a gépnév) |
   | Work folder | alapértelmezett (`_work`) |
   | Run agent as service? | **Y** |
   | Enable SERVICE_SID_TYPE_UNRESTRICTED? | N (alapértelmezett) |
   | User account to use for the service | alapértelmezett: `NT AUTHORITY\NETWORK SERVICE` |

3. **Indítsd újra az agent-szolgáltatást**, mert a szolgáltatás a PATH-ot és a csoporttagságot
   (`docker-users`) csak induláskor olvassa be:
   ```powershell
   Get-Service vstsagent* | Restart-Service
   ```

> **Ezt minden PATH-változás után meg kell ismételni** (`install.bat`, új SDK/Node/PowerShell
> telepítése). Különben a pipeline a régi PATH-szal fut, és „`security-toolkit` is not recognized”
> vagy hasonló hibát ad.

**Ellenőrzés:**
1. *Agent pools* → a pool → *Agents*: az agent **Online**.
2. Futtass egy próba-pipeline-t bármelyik repóban (pl. egy ideiglenes `agent-check.yml`-lel):
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
       pwsh -v
       dotnet --version
       node -v
       git --version
     displayName: Agent környezet
   ```
   Minden parancs kiírja a verzióját, a `whoami` pedig `nt authority\network service`-t ad. A
   `docker version` mutatja a *Server* részt is, nem csak a *Client*-et. Ha a *Server* rész hiányzik,
   a `docker-users` csoporttagság vagy a szolgáltatás-újraindítás maradt ki.

## 1.6 Ellenőrzőlista

- [ ] 16 GB RAM, virtualizáció be, Windows Update ablak beállítva
- [ ] Git, .NET SDK, Node, Chrome gépi szinten, PowerShell 7 MSI-ből (`ADD_PATH=1`)
- [ ] Docker Desktop: indul bejelentkezéskor, auto-update ki, `.wslconfig` beállítva
- [ ] `NT AUTHORITY\NETWORK SERVICE` a `docker-users` csoportban
- [ ] Autologon + automatikus zárolás, újraindítás után a `docker ps` működik
- [ ] `install.bat` lefutott, új terminálban a `security-toolkit --version` működik
- [ ] Agent pool és Online agent, a szolgáltatás a PATH-változások után újraindítva
- [ ] A próba-pipeline minden parancsa lefut

## 1.7 Linux CI-gép (Windows helyett)

A CLI, a generált pipeline és a gépi infra Linuxon is ugyanúgy működik. Ubuntu 22.04/24.04 LTS-en
és Debian 12-n érdemes indulni, x64-en vagy arm64-en. Az 1.1 hardverigénye itt is érvényes. A
Windowsos változathoz képest több lépés **egyszerűbb**: a Docker Engine rendszerszolgáltatás, ezért
nincs Docker Desktop, WSL2-memórialimit és Autologon.

### Rendszerszintű szoftverek

A csomagokat a disztribúció csomagkezelőjével kell telepíteni. A toolkit `installPrerequisites`
kapcsolója (winget) Linuxon nem telepít, csak jelzi, mi hiányzik. Szükséges:

| Szoftver | Megjegyzés |
|---|---|
| Git | `apt install git` |
| .NET 10 SDK | [Microsoft-csomagtár vagy `dotnet-install.sh`](https://learn.microsoft.com/dotnet/core/install/linux) |
| Node.js LTS | csak frontendhez; pl. a NodeSource-csomagtárból |
| PowerShell 7 (`pwsh`) | a pipeline *Initialize* lépése és a `.security/*.ps1` scriptek miatt; [telepítés Linuxra](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux) |
| Docker Engine + Compose plugin | [docs.docker.com/engine/install](https://docs.docker.com/engine/install/), `docker-compose-plugin` csomaggal |
| Chrome vagy Chromium | csak a frontend-coverage-hez (`ng test`); más útvonalnál `CHROME_BIN` |

Minden a gépi PATH-ra (`/usr/bin`, `/usr/local/bin`) kerüljön, ne egy felhasználó saját mappájába:
az agent a saját fiókja alatt fut, és nem látja a többiekét.

### A CLI telepítése (`install.sh`)

A repó gyökerében:

```bash
sudo bash install.sh                  # kérdez a gépi infráról
sudo bash install.sh --sonar --mssql  # CI-gép, kérdések nélkül
sudo bash install.sh --no-infra       # fejlesztői gép
```

A script a hívó felhasználó nevében publikál (`linux-x64` vagy `linux-arm64`, self-contained),
így a `bin/` és `obj/` mappák nem lesznek root tulajdonúak. Ezután ide másolja a binárist:
`/opt/security-toolkit/bin/security-toolkit`, és a `/usr/local/bin/security-toolkit` linkkel teszi a
PATH-ra. A `/usr/local/bin` minden felhasználó és szolgáltatás alapértelmezett PATH-ján rajta van,
ezért itt nincs PATH-frissítés és terminál-újranyitás. Frissítés: `git pull`, majd újra
`sudo bash install.sh --no-infra`.

A gépi infra Linuxon a `/var/lib/security-toolkit/infra/` alá kerül (a `.env` `chmod 600`, root
tulajdonú). Az `infra` parancsokat ezért `sudo`-val kell futtatni, pl.
`sudo security-toolkit infra add-test-login myapp_test`.

### Az agent és a Docker

1. Hozz létre egy dedikált felhasználót az agentnek, és add a `docker` csoporthoz:
   ```bash
   sudo useradd -m -s /bin/bash azagent
   sudo usermod -aG docker azagent
   ```
   > A `docker` csoport tagsága gyakorlatilag root-jogot ad a gépen. Ezért legyen dedikált fiók, és
   > ne fusson rajta más.
2. *Agent pools* → a pool → **New agent** → *Linux* → töltsd le, és csomagold ki az agent
   felhasználó nevében, pl. `/home/azagent/agent` alá. Utána `./config.sh`, ugyanazokkal a
   válaszokkal, mint az 1.5 táblázatában (a szolgáltatásra vonatkozó kérdés itt nincs).
3. Szolgáltatásként telepítsd és indítsd:
   ```bash
   cd /home/azagent/agent
   sudo ./svc.sh install azagent
   sudo ./svc.sh start
   ```
4. **Az agent a PATH-ot a konfiguráláskor menti el** (`.path` fájl az agent mappájában). Ha
   később telepítesz új eszközt egy olyan helyre, ami akkor nem volt a PATH-on, írd bele, majd
   `sudo ./svc.sh stop && sudo ./svc.sh start`. A `docker` csoporttagság is csak újraindítás után
   érvényes.

A generált pipeline-on nem kell változtatni. A `script:` lépések Linuxon bash-ben futnak, az
*Initialize* lépés `pwsh`-ban, a toolkit pedig minden útvonalat platformfüggetlenül kezel. A
scanner-konténerek (Semgrep, Gitleaks, OSV) Linuxon az agent felhasználójának uid/gid-jével futnak.
Így a riportfájlok nem lesznek root tulajdonúak, és a következő checkout (`clean: true`) is le tudja
törölni őket.

**Ellenőrzés:** az 1.5 próba-pipeline-ja itt is használható. A `whoami` itt `azagent`-et ad, a
`docker version` *Server* része pedig akkor jelenik meg, ha a csoporttagság érvényes.

### Ellenőrzőlista (Linux)

- [ ] Git, .NET SDK, Node, `pwsh`, Docker Engine + Compose plugin, Chrome/Chromium gépi szinten
- [ ] `sudo bash install.sh` lefutott, a `security-toolkit --version` bármelyik felhasználóval működik
- [ ] Dedikált agent-felhasználó a `docker` csoportban, az agent `svc.sh`-val szolgáltatásként fut
- [ ] A próba-pipeline minden parancsa lefut

[Következő: 2. Sonar és teszt-SQL →](02-sonar-es-mssql.md)
