# 7. Hibaelhárítás

[English](../07-troubleshooting.md) · **Magyar**

[← 6. Helyi futtatás és CLI](06-helyi-futtatas-es-cli.md) · [Tartalomjegyzék](README.md) · [Következő: 8. LLM-támogatott javítás →](08-llm-tamogatott-javitas.md)

A tünet szerint keresd ki a hibát. Szinte mindegyik élesben előfordult már.

## Agent és gép

| Tünet | Ok | Megoldás |
|---|---|---|
| A pipeline-ban: `'security-toolkit' is not recognized…` | az agent-szolgáltatás még a régi PATH-szal fut | `Get-Service vstsagent* \| Restart-Service` ([1.5](01-gep-es-agent.md#15-az-agent-telepítése-windows-szolgáltatásként)). Terminálban: nyiss újat. |
| `permission denied … npipe:////./pipe/docker_engine`, vagy a `docker version`-ben nincs *Server* rész | a `NETWORK SERVICE` nem tagja a `docker-users` csoportnak | `Add-LocalGroupMember -Group docker-users -Member "NT AUTHORITY\NETWORK SERVICE"`, majd az agent újraindítása |
| `error during connect` / a Docker nem fut újraindítás után | nincs bejelentkezett felhasználó, ezért a Docker Desktop nem indult el | Autologon ([1.2](01-gep-es-agent.md#automatikus-bejelentkezés-autologon)). Jelszóváltozás után újra be kell állítani. |
| Éjszakai futás félbeszakad | a Windows Update újraindította a gépet | a Használati idő / karbantartási ablak és az ütemezés összehangolása ([4.4](04-pipeline.md#44-ütemezés)) |
| A gép belassul, a Sonar konténer leáll vagy újraindul | kevés memória | 16 GB RAM, `.wslconfig` ([1.2](01-gep-es-agent.md#a-docker-desktop-memórialimitje-wslconfig)), a `SONAR_MEM_LIMIT` ellenőrzése |
| Linux: `permission denied while trying to connect to the Docker daemon socket` | az agent (vagy a te) felhasználód nem tagja a `docker` csoportnak, vagy a tagság még nem él | `sudo usermod -aG docker <felhasználó>`, majd új bejelentkezés, ill. `sudo ./svc.sh stop && sudo ./svc.sh start` ([1.7](01-gep-es-agent.md#az-agent-és-a-docker)) |
| Linux: `security-toolkit: command not found` a pipeline-ban | az agent PATH-ja a konfiguráláskori állapotot tükrözi (`.path`), és abban nincs `/usr/local/bin` | a `/usr/local/bin` felvétele az agent `.path` fájljába, majd az agent újraindítása |
| Linux: `pre-commit: /bin/sh^M: bad interpreter` | CRLF sorvégű hook egy régi toolkit-verzióból | toolkit-frissítés, majd `security-toolkit install --project-root .` (a hook mostantól LF) |
| Linux: `UnauthorizedAccessException` / `Access to the path '/var/lib/security-toolkit/...' is denied` | az `infra` parancs root jogot kér | `sudo security-toolkit infra ...` |
| Linux: a `.security/results/` alatt root tulajdonú fájlok, a checkout `clean` nem tudja törölni | régi toolkit-verzió vagy rootként futtatott scan | `sudo rm -rf .security/results`, toolkit-frissítés; a scant ne futtasd rootként |

## Telepítés és a pipeline *Initialize* lépése

| Tünet | Ok | Megoldás |
|---|---|---|
| `Error: Could not find a part of the path '…/.security/security.config.json'` (exit 2) | ebben a repóban (vagy ezen az ágon) még nincs telepítve a toolkit | `security-toolkit install --project-root .` ([3. fejezet](03-telepites-projektbe.md)) |
| `Image must be pinned first by the installer.` | a lockban nincs digest (csak `--configure-only` futott, vagy egy új eszközt kapcsoltak be) | teljes `security-toolkit install --project-root . --non-interactive` egy Dockeres gépen, a `security.lock.json` commitolása ([3.4](03-telepites-projektbe.md#34-telepítés-felügyelet-nélkül---non-interactive)) |
| `Pin the .NET tool version first.` | ugyanez a DevSkim / SonarScanner / dotnet-coverage esetén | ugyanaz |
| A telepítés lefut, de egy fájl nem frissül | az owned fájlt valaki kézzel módosította, ezért a telepítő kihagyta | `install --check` → `ManualChangesDetected`; a kézi módosítás visszavonása ([3.7](03-telepites-projektbe.md#37-karbantartás-és-frissítés)) |
| Egy régi `.security/*.ps1` (pl. `Toolkit.Common.ps1`) az install után is megmaradt | kézzel módosították, ezért a telepítő nem törölte (a `pending-actions.md` jelzi) | ha nem kell, kézzel törölni és commitolni |
| A régi config `toolkit.json` / `toolkit.lock.json` nevű | egy korábbi, sosem éles iteráció maradványa, nincs migráció | törölni, és újratelepíteni |

## Scannerek

| Tünet | Ok | Megoldás |
|---|---|---|
| `sonar … not-run` | nincs `--include-sonar` | a pipeline magától átadja; helyben add meg ([6.1](06-helyi-futtatas-es-cli.md#61-a-scan-parancs)) |
| `Set the SONAR_TOKEN environment variable …` | nincs token a lépés környezetében | pipeline: `SONAR_TOKEN` secret változó ([4.2](04-pipeline.md#42-pipeline-változók-és-secretek)); helyben: `$env:SONAR_TOKEN` a saját user tokeneddel ([2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)) |
| A Sonar `begin` lefut, az `end` „not authorized” vagy „project not found” hibát ad | (a) rossz `sonar.projectKey`: a projekt „With Azure DevOps” opcióval jött létre, belső GUID-os kulccsal; (b) a token tulajdonosának nincs *Execute Analysis* joga (helyben: a te felhasználódnak); (c) helyben egy másik projekt project analysis tokenje van a `SONAR_TOKEN`-ben | (a) Sonar: *Project Settings* → *Update Key*, vagy `curl -u "<token>:" "<sonar>/api/components/search?qualifiers=TRK"` a valós kulcsért; (b) a jogosultság megadása ([2.2](02-sonar-es-mssql.md#project-analysis-token)); (c) a saját user tokened használata ([2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)) |
| DevSkim: `DS173237` a `.github/workflows/security-toolkit.yml`-ben vagy az `azure-security-toolkit.yml`-ben | régi toolkit-verzió (a generált pipeline `toolkitRelease`-checksumjai kulcsnak tűntek) | toolkit-frissítés (0.1.3 vagy újabb); egy kerülőként felvett `devskim.ignoreGlobs`-bejegyzés kivehető |
| Gitleaks: sok találat a `.sonarqube/` alól | régi toolkit-verzió (a Sonar munkamappája nem volt kizárva) | toolkit-frissítés, majd `install` ([3.7](03-telepites-projektbe.md#37-karbantartás-és-frissítés)) |
| Scans fül: „File not found” egy Gitleaks-találatnál | `--history` módban egy régi commitban lévő, azóta áthelyezett fájlra mutat | a találat valós; a fájl a commit-azonosító alapján a historyban kereshető |
| Nincs Scans fül | nincs telepítve a SARIF SAST Scans Tab extension | [4.0](04-pipeline.md#40-egyszeri-szervezeti-beállítás-sarif-scans-fül) |
| npm: `npm audit requires package-lock.json or npm-shrinkwrap.json` | a frontend nem npm-mel kezelt (yarn/pnpm), vagy nincs lockfájl | `npm install` a lockfájl létrehozásához, vagy az npm eszköz kikapcsolása |
| Semgrep `failed`, de vannak találatok | egy fájlt egyáltalán nem tudott értelmezni (gyakran C#), vagy timeout/szabályhiba; a csak részleges parse-hiba (`Partially parsed`) nem buktat | `frontend` hatókör, ha Sonar is fut ([5.2](05-kiertekeles.md#52-eszközönként-hol-a-részlet-mit-jelent)) |
| Egy korábbi lépés hibája miatt a későbbi lépések is pirosak | régi toolkit-verzió (az összefésült eredményből számolt exit kódot) | toolkit-frissítés |

## Coverage

| Tünet | Ok | Megoldás |
|---|---|---|
| DB-tesztek: `actively refused` vagy `The wait operation timed out` | a connection stringben `localhost` szerepel; a konténer csak IPv4-en figyel | `Server=127.0.0.1,14333;…` ([2.3](02-sonar-es-mssql.md#23-teszt-sql-server-projektlogin-létrehozása)) |
| DB-tesztek: `Login failed for user` | az SQL Server konténert újra létrehozták (`down`/`up`), a loginok elvesztek | `infra add-test-login <login>` újra, és a pipeline secret frissítése |
| Sok teszt bukik ezzel: `The entry point exited without ever building an IHost` | az alkalmazás induláskor elhasal (pl. egy config hiányzik, ami a fejlesztőknél user-secretsben van). A `Program.cs` elkapja a kivételt, ezért a teszt csak ennyit lát. | a host helyi indítása ugyanazzal a környezettel megmutatja a valódi hibát. Javítás az alkalmazás oldalán: a teszt-fixture adjon meg tesztértéket, vagy a hiányzó értéket a `sonar.testEnvironment` és egy pipeline-változó adja át. |
| Angular: `Schema validation failed … codeCoverage` | régi toolkit-verzió egy `@angular/build:unit-test` projektet `--code-coverage`-dzsel futtatott | toolkit-frissítés |
| Angular: `no report (exit 1)`, a logban fordítási hiba (pl. `JSON does not support trailing commas`) | a frontend nem fordul | a hiba javítása a frontendben (a projekt csapatáé) |
| Angular: nem talál böngészőt | nincs Chrome a gépen | Chrome telepítése gépi szinten, vagy `CHROME_BIN` |
| Sonar: Quality Gate *Failed* a coverage miatt | nincs coverage-import, vagy sok teszt bukik | [3.5](03-telepites-projektbe.md#35-sonar-coverage), illetve a tesztek javítása |

## Infra

| Tünet | Ok | Megoldás |
|---|---|---|
| `infra install` megtagadja: a `security-toolkit-sonar_postgres_data` volume létezik, de nincs `.env` | elveszett a `.env`, és új jelszóval a Sonar kizárná magát a saját adatbázisából | a `.env` visszaállítása a mentésből, vagy (minden adat elvesztésével) a volume törlése ([2.5](02-sonar-es-mssql.md#25-frissítés-mentés-átköltözés)) |
| Linuxon `infra install --sonar`: `vm.max_map_count = 65530 …` | a kernel-limit kisebb, mint amit a Sonar Elasticsearch-e kér | a hibaüzenetben lévő `sysctl` parancs, majd az infra telepítésének újrafuttatása ([2.1](02-sonar-es-mssql.md#21-telepítés)) |
| Frissítés után a Sonar karbantartási módban van | adatbázis-migráció kell | `http://<sonar>:9000/setup` |
| `docker compose up failed` | foglalt port, kevés memória, vagy nem fut a Docker | `docker compose logs` a compose-mappában ([2.4](02-sonar-es-mssql.md#24-napi-üzemeltetés)) |

## Git és repó

| Tünet | Ok | Megoldás |
|---|---|---|
| Commitnál: `security-toolkit: not found` | a git a CLI telepítése előtt indított shellből vagy GUI-ból fut | új terminál, illetve a git-kliens (IDE) újraindítása. Ne használj `--no-verify`-t. |
| Commitnál a hook figyelmeztet, hogy nincs `.security/security.config.json`, és kihagyja a vizsgálatot | a hook minden ágon lefut, és ezen az ágon nincs telepítve a toolkit | ez várt viselkedés, a commit átmegy. Ha az ágat is vizsgálni kell, telepítsd rá a toolkitet. |

## Ha egyik sem illik

1. Nézd meg a `security-results` artifactban az adott eszköz logját (`<eszköz>*.log`). A
   `summary.md` *Message* oszlopa megnevezi a hibát.
2. Futtasd ugyanazt helyben, csak azt az egy eszközt: `security-toolkit scan --project-root . --tool <eszköz>`.
3. A `.security/results/setup-status.json` az *Initialize* lépés hibáit mutatja.
