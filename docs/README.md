# SecurityToolkit – telepítési és üzemeltetési útmutató

Ez az útmutató egy **üres Windows (vagy Linux) gépről és egy üres Azure DevOps projektből** indulva
végigvezet mindenen, ami egy zöld biztonsági pipeline-hoz kell. A fejezeteket sorrendben kell követni, mert
egymásra épülnek. Minden lépés végén van egy **Ellenőrzés** pont: csak akkor menj tovább, ha az
teljesül.

## Mi ez a rendszer?

A SecurityToolkit egy projektfüggetlen biztonsági scanner-csomag .NET és Angular repókhoz. Három
rétegből áll:

```
┌──────────────────────────────────── CI-gép ───────────────────────────────────────┐
│                                                                                   │
│  security-toolkit  (Windows: C:\Program Files\SecurityToolkit\bin,                │
│                     Linux: /opt/security-toolkit/bin; gépi PATH-on)               │
│        ▲                                                                          │
│        │ hívja                                                                    │
│  Azure DevOps self-hosted agent (Windows- vagy systemd-szolgáltatás)              │
│                                                                                   │
│  Docker ──► közös SonarQube + PostgreSQL   (minden projekt egy szervert           │
│         ──► közös teszt-SQL Server          használ, saját kulccsal/              │
│                                             saját loginnal)                       │
│         ──► scanner-konténerek (Semgrep, Gitleaks, OSV)                           │
└───────────────────────────────────────────────────────────────────────────────────┘

┌───────────────────────────── célprojekt (bármely repo) ───────────────────────────┐
│  .security/                    konfiguráció, lock, szabályok, init-scriptek       │
│  azure-security-toolkit.yml    generált pipeline                                  │
│  managed blokkok               .gitignore, .semgrepignore, Directory.Build.props, │
│                                .editorconfig                                      │
└───────────────────────────────────────────────────────────────────────────────────┘
```

1. **A CLI** (`security-toolkit`): gépenként egyszer telepítjük az `install.bat`-tal (Linuxon az
   `install.sh`-val). Ugyanez a
   CLI telepít a projektekbe, futtatja a scaneket és kezeli a gépi infrát.
2. **A gépi infra**: a közös SonarQube-szerver és egy teszt-SQL Server, Dockerben. Gépenként
   egyszer, szintén az `install.bat` / `install.sh` telepíti.
3. **A projekt-telepítés**: a `security-toolkit install` a célprojektbe generálja a `.security/`
   mappát és a pipeline-t. Ez repónként egyszer kell, utána commitolni.

A futtatható eszközök: .NET analyzerek, NuGet audit, npm audit, Semgrep, Gitleaks, OSV-Scanner,
DevSkim és SonarQube (coverage-dzsel). Részletesen: [06 – Helyi futtatás és CLI-referencia](06-helyi-futtatas-es-cli.md#62-a-8-scanner).

## Fejezetek

| # | Fejezet | Kinek | Gyakoriság |
|---|---|---|---|
| 1 | [CI-gép előkészítése és az Azure DevOps agent](01-gep-es-agent.md) | üzemeltető | gépenként egyszer |
| 2 | [A közös Sonar és a teszt-SQL Server](02-sonar-es-mssql.md) | üzemeltető | gépenként egyszer + karbantartás |
| 3 | [Telepítés egy célprojektbe](03-telepites-projektbe.md) | fejlesztő | repónként egyszer + frissítéskor |
| 4 | [A pipeline beállítása és indítása](04-pipeline.md) | fejlesztő | repónként egyszer |
| 5 | [Az eredmények kiértékelése és a triázs](05-kiertekeles.md) | biztonsági felelős, fejlesztő | minden futás után |
| 6 | [Helyi futtatás és teljes CLI-referencia](06-helyi-futtatas-es-cli.md) | fejlesztő | igény szerint |
| 7 | [Hibaelhárítás](07-hibaelharitas.md) | mindenki | ha valami nem megy |
| 8 | [LLM-támogatott kiértékelés és javítás](08-llm-tamogatott-javitas.md) | fejlesztő + kódoló agent | a találatok rendszeres feldolgozásakor |

## Gyors útvonal (a teljes folyamat egy oldalon)

1. CI-gép: Docker Desktop, .NET SDK, Node, PowerShell 7, Git, Chrome → `install.bat --sonar --mssql`
   rendszergazdaként → Azure DevOps agent telepítése szolgáltatásként → az agent-szolgáltatás
   újraindítása. Linuxon: Docker Engine, `pwsh`, `sudo bash install.sh --sonar --mssql`, agent
   `svc.sh`-val. ([1. fejezet](01-gep-es-agent.md))
2. Sonar: első belépés, jelszócsere, projekt létrehozása **kézzel**, project analysis token a
   pipeline-nak; a fejlesztőknek *Execute Analysis* jog, helyben mindenki a saját user tokenjét használja.
   Teszt-SQL: `security-toolkit infra add-test-login <projekt>_test`. ([2. fejezet](02-sonar-es-mssql.md))
3. Fejlesztői gép (Dockerrel): `install.bat --no-infra` (Linuxon `sudo bash install.sh --no-infra`), majd a repóban
   `security-toolkit install --project-root .` → a generált fájlok commitolása. ([3. fejezet](03-telepites-projektbe.md))
4. Azure DevOps: pipeline a `azure-security-toolkit.yml`-ből, `SONAR_TOKEN` és a többi secret
   felvétele, első futtatás, a pool-hozzáférés engedélyezése. ([4. fejezet](04-pipeline.md))
5. Eredmények: `security-results` artifact (`summary.md`), Scans fül, Sonar UI. ([5. fejezet](05-kiertekeles.md))

## Jelölések

- `<szögletes zárójel>`: a saját értékedre cserélendő (pl. `<org>` = az Azure DevOps szervezet neve).
- A parancsok PowerShellben futnak, ha nincs másképp jelölve (a `security-toolkit` parancsok
  Linuxon bash-ből ugyanígy). A **rendszergazdaként** jelölt lépésekhez emelt jogú terminál kell,
  Linuxon `sudo`.
- A példákban az agent pool neve `SecurityToolkit`, a Sonar-projekté `MyApp`. Ezek csak minták.
