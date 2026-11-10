# 4. A pipeline beállítása és indítása

[← 3. Telepítés projektbe](03-telepites-projektbe.md) · [Tartalomjegyzék](README.md) · [Következő: 5. Kiértékelés →](05-kiertekeles.md)

A telepítő a repó gyökerébe generálja az `azure-security-toolkit.yml`-t. Ebből egy Azure DevOps
pipeline-t kell létrehozni, a secreteket megadni, és elindítani. Ez repónként egyszer kell. GitHubon
lévő repóhoz ugyanez GitHub Actions workflow-ként is kérhető, ld. [4.6](#46-github-actions-az-azure-helyett-vagy-mellett).

## 4.0 Egyszeri szervezeti beállítás: SARIF Scans fül

A Semgrep- és a Gitleaks-találatokat a pipeline egy **Scans** fülön is megmutatja. Ehhez a szervezetbe
egyszer telepíteni kell a **SARIF SAST Scans Tab** extensiont (Microsoft DevLabs, `sariftools.scans`):
*Organization settings* → *Extensions* → *Browse marketplace*. Ehhez szervezeti admin jog kell.
Extension nélkül a pipeline ugyanígy működik, csak a Scans fül hiányzik.

## 4.1 A pipeline létrehozása

1. Azure DevOps → *Pipelines* → **New pipeline**.
2. *Where is your code?* → **Azure Repos Git** → válaszd a repót.
3. *Configure your pipeline* → **Existing Azure Pipelines YAML file**.
4. *Branch*: az az ág, amelyre a toolkitet commitoltad. *Path*: `/azure-security-toolkit.yml` → *Continue*.
5. A *Run* gomb melletti nyílnál válaszd a **Save** opciót (még ne futtasd, előbb jönnek a secretek).
6. Nevezd át: a pipeline listájában … → *Rename/move*, pl. `<repó> Security`.

> **A YAML-t ne szerkeszd kézzel.** A toolkit kezeli: minden telepítés újragenerálja, a kézi módosítás
> eltérésként jelenik meg ([3.7](03-telepites-projektbe.md#37-karbantartás-és-frissítés)). Ha más
> kell, a `security.config.json`-t módosítsd, és telepíts újra.

## 4.2 Pipeline-változók és secretek

A pipeline szerkesztőjében: *Edit* → **Variables** → *New variable*:

| Név | Érték | Secret? | Mikor kell |
|---|---|---|---|
| `SONAR_TOKEN` | a [2.2](02-sonar-es-mssql.md#project-analysis-token)-ben generált **project analysis token** (nem a személyes user token) | **igen** (*Keep this value secret*) | ha a Sonar be van kapcsolva |
| a `sonar.testEnvironment` minden *értéke* (pl. `TestDbConnectionString`) | pl. a [2.3](02-sonar-es-mssql.md#23-teszt-sql-server-projektlogin-létrehozása) connection stringje | **igen** | ha coverage-import van, és a teszteknek kell |

> **Miért kell a YAML-ben az `env:` blokk?** Az Azure DevOps a *secret* változókat **nem** adja át
> automatikusan a scriptek környezetének. A generált YAML ezért a Sonar-lépésnél kifejezetten
> leképezi őket:
> ```yaml
> env:
>   SONAR_TOKEN: $(SONAR_TOKEN)
>   ConnectionStrings__Default: $(TestDbConnectionString)
> ```
> Új secretet tehát nem elég a pipeline-ba felvenni: a `sonar.testEnvironment`-be is fel kell venni,
> és újra kell generálni a YAML-t ([3.5](03-telepites-projektbe.md#35-sonar-coverage)).

> A pipeline és a fejlesztői gép **más tokent** használ ugyanazon a néven: a pipeline a projekt
> analysis tokenjét (projektenként külön secret változó), a fejlesztő helyben a saját user tokenjét
> ([2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)). A pipeline tokenjét ne másold fejlesztői gépre.

A titkok soha nem kerülnek a logokba vagy a riportokba: a toolkit a `SONAR_TOKEN` minden előfordulását
`[REDACTED]`-re cseréli a kimenetekben.

### Privát NuGet / npm feed

Ha a telepítéskor `privateNuGet: true`-t választottál, a YAML egy `NuGetAuthenticate@1` lépéssel
kezdődik. Ehhez a projekt build-identitásának olvasási joga kell a feedhez: *Artifacts* → a feed →
*Feed settings* → *Permissions* → *Add users/groups* → **`<Projekt> Build Service (<org>)`**, szerepkör
*Feed Reader*. Privát npm-nél (`privateNpmRc`) ugyanez, plusz egy `npmAuthenticate@0` lépés a
megadott `.npmrc`-re.

## 4.3 Első futtatás

1. *Run pipeline* → válaszd az ágat → *Run*.
2. Első futáskor megjelenhet ez: *„This pipeline needs permission to access a resource before this run
   can continue”*. → *View* → **Permit**. Ez az agent pool használatát engedélyezi ennek a pipeline-nak.
   Ha a poolnál a *Grant access permission to all pipelines* be volt kapcsolva, ez nem jelenik meg.
3. Egy futás jellemzően 10–20 perc, a Sonar-lépés coverage-dzsel a leghosszabb.

### A pipeline felépítése

| Lépés | Mit csinál |
|---|---|
| `checkout` | teljes klón (`fetchDepth: 0`), hogy a Gitleaks és a Sonar lássa a historyt |
| `NuGetAuthenticate` | csak privát NuGet feednél |
| **Initialize pinned security tools** | `security-toolkit provision --require-pinned`: a lockban rögzített digestű Docker-image-ek letöltése, a rögzített verziójú .NET-eszközök telepítése a `.security/bin/` alá. Ha valami nincs rögzítve, itt bukik el ([3.4](03-telepites-projektbe.md#34-telepítés-felügyelet-nélkül---non-interactive)). |
| **Security scan: `<eszköz>`** | minden bekapcsolt eszközre egy külön `security-toolkit scan --tool <eszköz> --mode Report --run-id $(securityRunId)` lépés, `condition: always()`-szel |
| `PublishCodeCoverageResults@2` | csak `sonar.coverage: true` mellett: a .NET (`dotnet-coverage.xml`) és az Angular (`lcov.info`) coverage → a futás **Code Coverage** füle |
| `PublishPipelineArtifact` | a `.security/results` → **`security-results`** artifact |
| `CopyFiles` + `PublishBuildArtifacts` | a `*.sarif` fájlok → `CodeAnalysisLogs` artifact (ebből lesz a Scans fül) |

- **Minden eszköz külön lépés**, és `condition: always()`-szel fut. Ha az egyik elbukik, a többi ettől
  még lefut.
- **Közös `--run-id`** (a build száma). Minden lépés ugyanabba a `results/<build-szám>/` mappába ír, és
  a `status.json`-t eszközönként összefésülik. A futás végén egyetlen, teljes `status.json` és
  `summary.md` van.
- **Exit kódok lépésenként**, mindig csak az adott lépés saját eszközéből számolva:

  | Exit | Jelentés | A lépés |
  |---|---|---|
  | `0` | az eszköz lefutott (találatokkal vagy anélkül) | zöld |
  | `1` | Gate módban blokkoló találat | piros |
  | `2` | technikai hiba (az eszköz nem tudott lefutni) | piros |

- A generált pipeline **mindig Report módban** fut: találat miatt nem lesz piros, csak technikai hiba
  miatt. A Gate mód a helyi és az egyedi használatra való ([5.4](05-kiertekeles.md#54-report-vagy-gate-mód)).

**Ellenőrzés:**
- minden lépés lefutott, és a `Security scan: …` lépések zöldek;
- a futás *Summary* oldalán van egy **`security-results`** artifact (és egy `CodeAnalysisLogs`);
- ha van Sonar: a Sonar-projekt oldalán megjelent az elemzés, a futás időpontjával;
- ha van extension: a futásnál ott a **Scans** fül;
- ha a coverage-import be van kapcsolva: a **Code Coverage** fülön ott a lefedettségi riport.

## 4.4 Ütemezés

A YAML-ben `trigger: none` és `pr: none` van: a pipeline magától nem indul. Ütemezett futtatáshoz
(pl. éjszakai, a Windows Update ablakon kívül) a pipeline UI-ban kell beállítani: *Edit* → … →
**Triggers** → *Scheduled* → *Add*. A UI-ban megadott ütemezés felülírja a YAML-t, ezért a YAML-hez
nem kell nyúlni.

## 4.5 Ellenőrzőlista

- [ ] (szervezetenként egyszer) SARIF SAST Scans Tab extension
- [ ] Pipeline létrehozva az `azure-security-toolkit.yml`-ből, és átnevezve
- [ ] `SONAR_TOKEN` és a `testEnvironment` változói secretként felvéve
- [ ] Privát feed esetén a Build Service Feed Reader joga
- [ ] Első futás: a pool-hozzáférés engedélyezve, minden lépés lefutott
- [ ] `security-results` artifact és Scans fül, a Sonarban látszik az elemzés
- [ ] Ütemezés beállítva (ha kell)

## 4.6 GitHub Actions (az Azure helyett vagy mellett)

Ha a repó GitHubon van, a telepítő Azure-pipeline helyett (vagy mellé) egy GitHub Actions workflow-t
is tud generálni: `.github/workflows/security-toolkit.yml`. Ezt a `security.config.json`
`pipelineProviders` listája dönti el:

| `pipelineProviders` | Mi jön létre |
|---|---|
| `["azure"]` (alapérték) | csak az `azure-security-toolkit.yml`, ugyanaz, mint eddig |
| `["github"]` | csak a GitHub workflow |
| `["azure", "github"]` | mindkettő |

Ha egy platformot kiveszel a listából, a korábban generált fájlja megmarad, csak nem frissül többé. A
telepítő ezt üzenetben jelzi, a törlés a te döntésed.

A workflow felépítése ugyanaz, mint az Azure-é: `provision --require-pinned`, utána minden bekapcsolt
eszköz külön lépés, közös `--run-id`-val (`<run_id>-<run_attempt>`, így egy újrafuttatás nem keveredik az
előzővel), a végén a `.security/results` → **`security-results`** artifact. A lépések
`if: ${{ !cancelled() }}` feltétellel futnak: egy eszköz hibája nem állítja meg a többit, de a kézi
leállítást tiszteletben tartják. A pipeline itt is mindig **Report** módban fut.

### Runner

A workflow **self-hosted runnert** vár, amin a `security-toolkit` a gépi PATH-on van (ugyanaz a gép és
ugyanaz az `install.bat` / `install.sh`, mint az Azure agentnél, ld. [1. fejezet](01-gep-es-agent.md)).
GitHub-hosted runneren nincs ott a CLI, ott a workflow első lépése elbukik.

1. Repó → *Settings* → *Actions* → *Runners* → **New self-hosted runner**, és kövesd a GitHub
   telepítési lépéseit. A runnert szolgáltatásként telepítsd (Windowson a `config.cmd` kérdésére,
   Linuxon `./svc.sh install`).
2. A runner szolgáltatásfiókjának látnia kell a Dockert (Linuxon a `docker` csoport tagja legyen).
3. Az `install.bat` / `install.sh` után **indítsd újra a runner-szolgáltatást**, különben a régi PATH-szal
   fut tovább.

A `runs-on` címkéit a `githubRunsOn` adja meg (alapból `["self-hosted"]`). Ha több self-hosted runnered
is van, adj a security-gépnek saját címkét, pl. `["self-hosted", "security-toolkit"]`.

Shellt a workflow nem ír elő, mert a lépések egyszerű CLI-hívások. Linuxon bash fut, Windowson a pwsh,
ha nincs telepítve, akkor a beépített Windows PowerShell. Külön telepíteni tehát semmit nem kell.

### Secretek

Repó → *Settings* → *Secrets and variables* → *Actions* → **New repository secret**:

| Név | Mikor kell |
|---|---|
| `SONAR_TOKEN` | ha a Sonar be van kapcsolva (a projekt analysis tokenje, mint az Azure-nál) |
| a `sonar.testEnvironment` minden *értéke* | ha coverage-import van, és a teszteknek kell |

A GitHub secretek nevében nem lehet pont, és nem kezdődhetnek `GITHUB_`-bal. Ha a GitHub workflow is
generálódik, a telepítő a `testEnvironment` értékeit e szerint ellenőrzi. Közös névnek (mindkét
platformra) tehát pont nélküli nevet válassz, pl. `TEST_DB_CONNECTION`.

A privát NuGet/npm feed hitelesítése (`privateNuGet`, `privateNpmRc`) csak az Azure-pipeline-ba ír
lépést. GitHubon a feed elérését a runner gépén kell beállítani (pl. a felhasználói `nuget.config`-ban).

### Code Scanning és ütemezés

- `githubCodeScanning: true`: a futás végén a SARIF-riportok (Semgrep, Gitleaks, DevSkim) a repó
  **Security → Code scanning** oldalára is felkerülnek. Privát repóban ehhez **GitHub Advanced Security**
  (Code Security) licenc kell, nélküle ez a lépés elbukik. Ezért alapból ki van kapcsolva. A SARIF-fájlok
  a `security-results` artifactban enélkül is benne vannak.
- `githubSchedule`: ötmezős cron-kifejezés **UTC-ben**, pl. `"0 2 * * 1-5"` (hétköznap 02:00 UTC). Üresen
  a workflow csak kézzel indul (*Actions* → *Security Toolkit* → **Run workflow**). GitHubon nincs
  UI-ban beállítható ütemezés, mint az Azure-nál, ezért van erre külön kulcs.

A workflow-ban minden action teljes commit-SHA-ra van rögzítve (nem tagre), ugyanazért, amiért a
scanner-image-ek digestre. Az action-verziókat a toolkit frissítése hozza. Ha a repóban be van
kapcsolva a Dependabot `github-actions` frissítése, és az ezt a fájlt is átírja, a telepítő ezt kézi
módosításnak látja: a következő `install` kihagyja a fájlt, és szól róla
([3.7](03-telepites-projektbe.md#37-karbantartás-és-frissítés)). Ezért ennél a fájlnál a Dependabot
PR-jait ne olvaszd be: zárd le őket, vagy a `dependabot.yml`-ben hagyd ki a `github-actions` ökoszisztémát.

**Ellenőrzés:**
- az *Actions* fülön megjelent a **Security Toolkit** workflow, és kézzel elindítva minden lépés lefut;
- a futás *Summary* oldalán ott a **`security-results`** artifact;
- ha be van kapcsolva: a *Security → Code scanning* oldalon megjelentek a találatok.

[Következő: 5. Kiértékelés →](05-kiertekeles.md)
