# 6. Helyi futtatás és teljes CLI-referencia

[English](../06-local-runs-and-cli.md) · **Magyar**

[← 5. Kiértékelés](05-kiertekeles.md) · [Tartalomjegyzék](README.md) · [Következő: 7. Hibaelhárítás →](07-hibaelharitas.md)

Minden, amit a pipeline csinál, a saját gépeden is futtatható ugyanazzal a CLI-vel. Így egy
javítást a push előtt ellenőrizhetsz.

## 6.0 Egy frissen klónozott repó előkészítése

A rögzített eszközök (`.security/bin/`) és a Docker-image-ek nincsenek a repóban, ezért egy új
gépen vagy klónban egyszer elő kell készíteni őket. Pontosan ugyanez fut a pipeline első lépésében is:

```powershell
cd C:\Work\<repó>
security-toolkit provision --project-root . --require-pinned
```

Feltétel: a CLI a PATH-on (`install.bat --no-infra`, Linuxon `sudo bash install.sh --no-infra`), a Docker fut, és van .NET SDK (frontendhez Node).

## 6.1 A `scan` parancs

```powershell
security-toolkit scan --project-root .                        # minden bekapcsolt eszköz, Report mód
security-toolkit scan --project-root . --tool semgrep         # csak egy eszköz
security-toolkit scan --project-root . --mode Gate            # blokkoló küszöbökkel
security-toolkit scan --project-root . --staged               # mint a pre-commit hook
```

Sonarral a **saját Sonar user tokenedet** add meg (nem a pipeline project analysis tokenjét), csak
erre a munkamenetre. Egy user token minden projektre jó, amelyen *Execute Analysis* jogod van; a
projektet a `sonar.projectKey` választja ki ([2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)). A token soha nem kerül fájlba, és
`setx`-szel se tárold tartósan:

```powershell
$env:SONAR_TOKEN = Read-Host 'Sonar user token'
security-toolkit scan --project-root . --tool sonar --include-sonar
```

A CLI a tokent a SonarScanner `begin` és `end` lépésének a `SONARQUBE_SCANNER_PARAMS` környezeti
változóban adja át, nem a parancssorukban, így a gép többi felhasználója nem látja a folyamatlistában.
Ha a `SONARQUBE_SCANNER_PARAMS`-ot magad is beállítod (JSON-objektum), a többi property-je megmarad.

Coverage-zel együtt a `sonar.testEnvironment` **környezeti változóit** helyben neked kell
beállítani (a pipeline-változók itt nem léteznek), pl.:

```powershell
$env:ConnectionStrings__Default = Read-Host 'Teszt connection string'
```

| Kapcsoló | Jelentés |
|---|---|
| `--project-root <útvonal>` | **kötelező**. A repó gyökere (ahol a `.security/` van). |
| `--mode Report\|Gate` | `Report` (alapértelmezés): találat miatt nem bukik. `Gate`: blokkoló küszöbök; `.security/baseline.json` mellett csak az új találat blokkol ([5.4](05-kiertekeles.md#54-report-vagy-gate-mód)). |
| `--update-baseline` | Report módban fut, és a futott eszközök mostani blokkoló találatait elfogadottként a `.security/baseline.json`-ba írja ([5.4](05-kiertekeles.md#baseline-csak-az-új-találat-blokkoljon)). `--staged`-del és `--mode Gate`-tel nem kombinálható. |
| `--tool <név>` | csak a megadott eszköz: `analyzers`, `nuget`, `npm`, `semgrep`, `gitleaks`, `osv`, `devskim`, `sonar` |
| `--include-sonar` | a Sonar-feltöltés engedélyezése; nélküle a Sonar `not-run` |
| `--sonar-only` | csak a Sonar (= `--tool sonar --include-sonar`) |
| `--history` | a Gitleaks a teljes elérhető git historyt vizsgálja, nem csak a munkaterületet |
| `--staged` | a Gitleaks csak a staged változásokat vizsgálja; csak Gitleaks fut, és mindig blokkol |
| `--secrets-only` | csak Gitleaks, a munkaterületen (index nélkül) |
| `--semgrep-scope repository\|frontend` | a Semgrep-hatókör egyszeri felülírása |
| `--config-file <útvonal>` | más config használata a `.security/security.config.json` helyett |
| `--run-id <id>` | ebbe a `results/<id>/` mappába írjon, és a meglévő `status.json`-nal fésülje össze. Így több `--tool` hívás eredménye egy riportba kerül, ahogy a pipeline-ban. |

Kimenet: `.security/results/<run-id>/` (`summary.md`, `status.json`, eszközönkénti riportok), és a
konzolon egy soros összefoglaló eszközönként. Exit kód: `0` / `1` / `2`, mint a pipeline-ban
([4.3](04-pipeline.md#a-pipeline-felépítése)).

### Lokális felülírás: `security.config.local.json`

Ha a saját gépeden valami eltér a csapat közös configjától (tipikusan egy helyi Sonar), hozd létre a
mintából:

```powershell
Copy-Item .security\security.config.local.json.example .security\security.config.local.json
```

```json
{
  "sonar": {
    "url": "http://127.0.0.1:9000"
  }
}
```

A `scan` ezt futáskor a `security.config.json` fölé fésüli (deep merge). A fájl gitignore-olt,
sosem kerül commitba. Ha nem létezik, a `scan` egyszerűen a közös configgal fut. Ez nem hiba.

## 6.2 A 8 scanner

| Eszköz | Mit vizsgál | Mit igényel | Kimenet | Gate-ben blokkol |
|---|---|---|---|---|
| **analyzers** | a .NET Roslyn biztonsági szabályai (CA30xx/CA31xx: injekció, XSS, deszerializáció, kriptó), plusz EF1002 (nyers SQL interpolált stringből) és NU1507 (több csomagforrás `packageSourceMapping` nélkül: dependency confusion), a build közben. **A CA30xx taint-szabályai csak MVC controller actionöket ismernek fel, minimal API-t (MapGet, Carter) nem**: ott a 0 nem jelent biztonságot. | .NET SDK | `dotnet-<n>-restore.log`, `dotnet-<n>-build.log` | soha (leltár) |
| **nuget** | ismert sebezhetőségű NuGet-csomagok, tranzitívakkal (`dotnet list package --vulnerable --include-transitive`) | .NET SDK, elérhető NuGet audit forrás | `dotnet-<n>-nuget.json` | High / Critical |
| **npm** | `npm audit` a frontend célokban | Node, `package-lock.json` vagy `npm-shrinkwrap.json` (yarn/pnpm nem támogatott) | `npm-<n>.json` | High / Critical |
| **semgrep** | Semgrep CE szabályok (`.security/rules/`): C#, TypeScript/JavaScript minták | Docker, a lockban rögzített image | `semgrep.json`, `semgrep.sarif`, `semgrep-coverage.json` | `ERROR` súlyosság |
| **gitleaks** | titkok a munkaterületen (vagy `--history`/`--staged` szerint), az alap szabályok + `.security/gitleaks.toml` | Docker, git-repó | `gitleaks.json`, `gitleaks.sarif` (redaktált) | bármely találat |
| **osv** | függőség-sebezhetőségek az OSV-adatbázisból, minden ökoszisztémára (kiegészítő) | Docker | `osv.json` | soha |
| **devskim** | Microsoft DevSkim biztonsági lint (kiegészítő); a `devskim.ignoreGlobs`/`ignoreRuleIds` kulccsal szűkíthető | a `.security/bin`-be rögzített DevSkim | `devskim.sarif` | soha |
| **sonar** | SonarQube statikus elemzés + opcionális coverage (`begin` → build → [tesztek] → `end`) | `SONAR_TOKEN` (helyben user token, a pipeline-ban project analysis token), `--include-sonar`, rögzített SonarScanner (+ `dotnet-coverage`, Chrome a coverage-hez) | `sonar-*.log`, `sonar-issues.json`, coverage-riportok | bukó Quality Gate |

> A Sonar-lépés garantáltan lefuttatja a lezáró `end` fázist akkor is, ha a build közben elbukik.
> Enélkül a SonarScanner egy gépszintű MSBuild-hookot hagyhatna maga után, ami minden más .NET buildbe
> belefolyna. Ezért **ne szakítsd meg** (Ctrl+C) a Sonar-lépést.

## 6.3 Teljes parancs-referencia

### `install`

```
security-toolkit install --project-root <útvonal> [kapcsolók]
```

| Kapcsoló | Jelentés |
|---|---|
| `--project-root` | **kötelező**. A célrepó gyökere. |
| `--config-file <json>` | profil, amely a meglévő/alapértelmezett config fölé fésülődik |
| `--non-interactive` | nincs kérdés; az alapértékek, a meglévő config és a `--config-file` számít |
| `--configure-only` | csak a fájlok generálása; nincs előfeltétel-telepítés és provisioning |
| `--dry-run` | a feloldott config kiírása, semmit nem ír |
| `--check` | eltérés-jelentés a generálandó állapothoz képest; nem ír semmit. Exit 0 = szinkronban, 1 = eltérés. |
| `--run-after-install` | sikeres telepítés után egy teljes Report-scan |
| `--semgrep-scope repository\|frontend` | a Semgrep-hatókör felülírása |

Részletek: [3. fejezet](03-telepites-projektbe.md).

### `provision`

```
security-toolkit provision --project-root <útvonal> [--require-pinned]
```

A `security.lock.json`-ban rögzített Docker-image-ek letöltése (és a digest ellenőrzése), valamint
a rögzített verziójú .NET-eszközök (DevSkim, SonarScanner, dotnet-coverage) telepítése a
`.security/bin/` alá. Az eredmény a `.security/results/setup-status.json`-ba kerül. A
`--require-pinned` elutasítja a nem rögzített image-et vagy verziót: a pipeline és egy friss klón így
fut (6.0). Nélküle a hiányzó pineket feloldja, és visszaírja a lockba. A teljes `install` a végén ugyanezt
futtatja. Exit 0 = minden eszköz kész, 2 = legalább egy eszköz nem sikerült.

### `scan`

Lásd 6.1.

### `report`

```
security-toolkit report --project-root <útvonal> [--run <run-id>]
```

Egy futás `status.json`-ját mutatja színes táblázatként. Alapból a legutóbbi futást.

### `restore`

```
security-toolkit restore --project-root <útvonal> --manifest <manifest.json> [--dry-run]
```

Egy telepítés visszavonása: minden fájlt visszaállít arra az állapotra, amelyet az adott telepítés
előtt mentett. A manifest: `.security/backups/<időbélyeg>/manifest.json`. Előtte `--dry-run`-nal
megnézheted, mit állítana vissza.

### `infra install`

```
security-toolkit infra install [--sonar] [--mssql] [--upgrade] [--sonar-port <N>] [--mssql-port <N>]
```

Gépi infra telepítése/frissítése. Részletek: [2. fejezet](02-sonar-es-mssql.md).

### `infra add-test-login`

```
security-toolkit infra add-test-login <login>
```

Projektlogin létrehozása a teszt-SQL Serveren, vagy újrafuttatva új jelszó generálása. Kiírja a
connection stringet ([2.3](02-sonar-es-mssql.md#23-teszt-sql-server-projektlogin-létrehozása)).

### Általános

```
security-toolkit --version
security-toolkit <parancs> --help
```

**Kilépési kódok** (minden parancsra, a pipeline-ok erre épülnek):

| Kód | Jelentés |
|---|---|
| `0` | siker (Report módban találatok mellett is) |
| `1` | Gate módban blokkoló találat; ill. hibás parancssor (ismeretlen kapcsoló, hiányzó kötelező paraméter), ilyenkor a súgó is megjelenik |
| `2` | technikai vagy konfigurációs hiba, pl. nincs `.security/security.config.json` (még nem futott `install`), hiányzó manifest, elérhetetlen Docker. Az ok egy `Error: …` sorban jelenik meg a hibakimeneten |

## 6.4 A `security.config.json` kulcsai

| Kulcs | Jelentés |
|---|---|
| `dotnetTargets` | a vizsgált .NET solutionök/projektek (relatív utak) |
| `frontendTargets` | a vizsgált frontend mappák |
| `tools.<eszköz>` | eszközönként be/ki (`analyzers`, `nuget`, `npm`, `semgrep`, `gitleaks`, `osv`, `devskim`, `sonar`) |
| `semgrepScope` | `repository` vagy `frontend` |
| `semgrepRulesets` | a letöltendő Semgrep registry-szabálykészletek (alapból `p/csharp`, `p/typescript`) |
| `downloadSemgrepRules` | a szabályok letöltése telepítéskor |
| `images.<eszköz>` | a Semgrep/Gitleaks/OSV forrás-image-e; a rögzített digest a lockban van |
| `versions.<eszköz>` | a DevSkim/SonarScanner/dotnet-coverage kért verziója; üres = a legújabb stabil, telepítéskor rögzítve |
| `sonar.url`, `sonar.projectKey`, `sonar.dotnetTarget` | a Sonar-kapcsolat és a buildelt solution |
| `sonar.coverage`, `sonar.testFilter`, `sonar.testEnvironment` | coverage-import ([3.5](03-telepites-projektbe.md#35-sonar-coverage)) |
| `devskim.ignoreGlobs` | további kizárt fájlminták a DevSkimnek, pl. `["**/*.Tests/**"]` (vessző nem lehet benne). Mindig kizárva: `.git`, `.security`, `bin`, `obj`, `node_modules`, `dist`, `.sonarqube` és a generált pipeline-fájlok |
| `devskim.ignoreRuleIds` | letiltott DevSkim-szabályok, pl. `["DS162092", "DS172411", "DS176209"]` (localhost, setTimeout, TODO) |
| `installHook`, `installVSCodeExtensions` | git hook, VS Code extensionök |
| `installPrerequisites`, `sdkChannel`, `nodeWingetId`, `nodeVersion` | hiányzó SDK/Node telepítése wingettel (csak Windowson; Linuxon csak jelzi a hiányt) |
| `generatePipeline`, `pipelineProviders`, `pipelineHistory` | generáljon-e pipeline-t, és melyik platformra (`azure`, `github`) |
| `pipelineSkipTools` | bekapcsolt eszközök, amelyeket a generált pipeline-ok kihagynak, pl. `["sonar"]`, ha a Sonar-szerver csak a fejlesztői gépekről érhető el. A lockban ugyanúgy rögzítve vannak, és helyben a szokásos módon futnak. |
| `pipelineMode` | `Report` (alapértelmezés) vagy `Gate`: ebben a módban futnak a generált pipeline scan-lépései ([5.4](05-kiertekeles.md#54-report-vagy-gate-mód)) |
| `agentPool`, `azureVmImage`, `privateNuGet`, `privateNpmRc` | az Azure-pipeline beállításai ([4.2](04-pipeline.md#42-pipeline-változók-és-secretek), [4.7](04-pipeline.md#47-efemer-vagy-hosted-agent-a-cli-letöltése-release-ből)) |
| `toolkitRelease.version`, `toolkitRelease.baseUrl` | a pipeline a megadott release-ből töltse le a CLI-t (efemer/hosted agent, [4.7](04-pipeline.md#47-efemer-vagy-hosted-agent-a-cli-letöltése-release-ből)); üres verzió = a gépi PATH-on lévő CLI |
| `githubRunsOn`, `githubCodeScanning`, `githubSchedule` | a GitHub Actions workflow beállításai ([4.6](04-pipeline.md#46-github-actions-az-azure-helyett-vagy-mellett)) |

Egy kulcs módosítása után: `install --project-root . --configure-only --non-interactive`. Ha egy
eszközt vagy a coverage-et kapcsolod be, teljes `install` kell.

## 6.5 A toolkit buildelése forrásból

A SecurityToolkit repóban (.NET 11 SDK):

```powershell
dotnet build SecurityToolkit.slnx
dotnet test SecurityToolkit.slnx
dotnet run --project src/SecurityToolkit.Cli -- scan --project-root C:\Work\<repó>   # forrásból, telepítés nélkül
dotnet publish src/SecurityToolkit.Cli/SecurityToolkit.Cli.csproj -c Release -r win-x64   # self-contained exe (Linuxon: -r linux-x64 / linux-arm64)
```

Éles használatra mindig az `install.bat`-ot (Linuxon az `install.sh`-t) használd: ez publikál, és a gépi PATH-ra telepít.

### Kiadás (release)

Efemer és hosted agentekhez a toolkit GitHub Release-ként is megjelenik
([4.7](04-pipeline.md#47-efemer-vagy-hosted-agent-a-cli-letöltése-release-ből)). Kiadni egy verziótaggel lehet:

```powershell
git tag -a v1.2.0 -m "v1.2.0"
git push origin v1.2.0
```

A `.github/workflows/release.yml` erre lefuttatja a teszteket, megépíti a `win-x64`, `linux-x64` és
`linux-arm64` self-contained binárist (`-p:Version=1.2.0`), és egy release-be teszi őket a
`SHA256SUMS` fájllal együtt. A kötőjeles tag (pl. `v1.3.0-rc.1`) pre-release lesz. Egy kiadott verziót
ne cserélj le: a célprojektek a checksumját rögzítették, és a pipeline-juk egy kicserélt fájlnál
elbukik. Javításhoz adj ki új verziót.

A forrásból buildelt CLI (`install.bat`/`install.sh`, `dotnet run`) verziója `0.0.0-dev`
(`security-toolkit --version`).

[Következő: 7. Hibaelhárítás →](07-hibaelharitas.md)
