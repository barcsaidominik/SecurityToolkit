# 5. Az eredmények kiértékelése és a triázs

[← 4. Pipeline](04-pipeline.md) · [Tartalomjegyzék](README.md) · [Következő: 6. Helyi futtatás és CLI →](06-helyi-futtatas-es-cli.md)

Egy futás után az eredményt három helyen látod:

| Hol | Mit mutat | Mikor nézd |
|---|---|---|
| **`security-results` artifact → `summary.md`** | minden eszköz egy táblázatban, találatszámmal | **mindig ezzel kezdj** |
| **Scans fül** (a futás oldalán) | a Semgrep és a Gitleaks találatai fájlra/sorra linkelve | a kód-szintű találatokhoz |
| **Sonar UI** | issue-k, security hotspotok, coverage, Quality Gate | a Sonar-találatokhoz és a trendekhez |
| **Code Coverage fül** (a futás oldalán) | a .NET és az Angular lefedettség fájlonként, HTML-ben | ha a coverage-import be van kapcsolva ([3.5](03-telepites-projektbe.md#35-sonar-coverage)) |

## 5.1 A `security-results` artifact

A futás oldalán: *Summary* → *Related* → *published* → **security-results** → letöltés. Szerkezete:

```
security-results/
  <build-szám>/                 ← egy futás (a közös --run-id)
    summary.md                  ← emberi olvasásra
    status.json                 ← gépi formátum, ugyanaz a tartalom
    <eszközönkénti riportok és logok>
  latest-status.json            ← a legutóbbi futás status.json-ja
  setup-status.json             ← az „Initialize pinned security tools” lépés eredménye
```

### `summary.md`

```markdown
# Security scan summary

| Tool | Target | Status | Findings | Blocking | Message |
|---|---|---|---|---|---|
| analyzers | src/MyApp.sln | completed | 0 | no | Build warning inventory; not a differential gate. |
| nuget | src/MyApp.sln | completed | 0 | no | |
| npm | src/Client | completed | 5 | no | |
| semgrep | frontend | completed | 2 | no | ERROR is rule severity; not a universal High/Critical risk label. |
| gitleaks | dir | completed | 1 | no | |
| sonar | MyApp | completed | 10 | no | Coverage: .NET report imported; src/Client:web ok |

## Sonar vulnerability severities
...
```

- **Status:** `completed` = az eszköz lefutott. `failed` = technikai hiba, az eszköz nem tudott
  végigfutni; az oka a *Message* oszlopban van. `not-run` = nem futott (pl. a Sonar
  `--include-sonar` nélkül).
- **Findings:** a találatok száma. A `completed` + `0` a tiszta eredmény.
- **Blocking:** csak Gate módban lehet `yes` (ld. 5.4).

Ugyanezt a táblázatot helyben is meg tudod jeleníteni színesen:

```powershell
security-toolkit report --project-root .                 # a legutóbbi futás
security-toolkit report --project-root . --run <run-id>  # egy adott futás
```

Ha egy letöltött artifactot akarsz így nézni, másold a `<build-szám>` mappát a repó
`.security/results/` mappájába, és add meg `--run`-nal.

## 5.2 Eszközönként: hol a részlet, mit jelent

| Eszköz | Riport a mappában | Mit számol | Hogyan olvasd |
|---|---|---|---|
| **analyzers** | `dotnet-<n>-build.log` | a build CA30xx/CA31xx, EF1002 és NU1507 biztonsági warningjai; a *Message* kódonként bontja (pl. `CA3001: 1, NU1507: 14`) | keresd a `warning CA3`, `warning EF1002`, `warning NU1507` sorokat; a CA-szabályok leírása: `https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca<szám>`. A NU1507 ellenszere a `nuget.config` `packageSourceMapping`-je. |
| **nuget** | `dotnet-<n>-nuget.json` | ismert sebezhetőségű NuGet-csomagok, tranzitívakkal együtt | csomag, verzió, súlyosság, advisory-link |
| **npm** | `npm-<n>.json` | `npm audit` találatai | `vulnerabilities` csomagonként, súlyossággal; a `fixAvailable` mutatja a javítást |
| **semgrep** | `semgrep.json`, `semgrep.sarif`, `semgrep-coverage.json` | Semgrep-szabálytalálatok | Scans fül, vagy `semgrep.json` → `results[]`: `check_id`, `path`, `start.line`, `extra.severity` |
| **gitleaks** | `gitleaks.json`, `gitleaks.sarif` | lehetséges titkok (jelszó, kulcs, token, connection string) | Scans fül, vagy `gitleaks.json`: `RuleID`, `File`, `StartLine`, `Fingerprint`. A titok értéke **mindig redaktálva** van. |
| **osv** | `osv.json` | függőség-sebezhetőségek az OSV-adatbázisból (kiegészítő) | `results[].packages[].vulnerabilities[]` |
| **devskim** | `devskim.sarif` | biztonsági lint-szabályok (kiegészítő) | SARIF-nézővel, vagy a Scans fülön |
| **sonar** | `sonar-issues.json`, `sonar-begin/build/end.log`, `sonar-test-*.log`, `dotnet-coverage.xml`, `frontend-coverage-<n>/` | a Sonar-projekt nyitott **Vulnerability** típusú issue-i | a részletek a Sonar UI-ban (5.3) |

> A Semgrepnél az `ERROR` a **szabály** súlyossága, nem egy általános „kritikus” címke. Egy `ERROR`
> is lehet téves riasztás, és egy `WARNING` is lehet valódi hiba.

> Ha a Semgrep `failed` státuszú, de vannak találatai, a vizsgálat hiányos volt: néhány fájlt nem
> tudott értelmezni (parse-hiba). A `semgrep-coverage.json` mutatja, melyeket. A C#-nál ez gyakori,
> ezért javasolt a `frontend` hatókör ([3.2](03-telepites-projektbe.md#további-kérdések)).

## 5.3 Sonar UI

A projekt oldala: `http://<sonar>:9000/dashboard?id=<projectKey>`.

- **Issues** → *Type: Vulnerability*: ezt a számot mutatja a `summary.md`. A `summary.md` a 10
  legsúlyosabbat szabállyal és fájl:sor hellyel is kilistázza (*Top … Sonar vulnerabilities*), így
  a triázs onnan indulhat. Itt van a kódrészlet, a
  szabály magyarázata és a javítási javaslat.
- **Security Hotspots:** olyan kódhelyek, ahol emberi döntés kell (pl. „ez a regex ReDoS-ra
  hajlamos lehet”). A toolkit ezeket **nem** számolja bele a találatokba, a Sonar UI-ban kell őket
  egyenként átnézni (*Review*: *Safe*, *Fixed* vagy *Acknowledged*).
- **Measures** → *Coverage*: a coverage-import eredménye (.NET és frontend együtt).
- **Quality Gate:** a dashboard tetején; *Passed* vagy *Failed*, a bukó feltételekkel.

### Coverage-megjegyzések

Ha a coverage-import be van kapcsolva, a Sonar-sor *Message* oszlopában egy `Coverage: …` rész
jelenik meg:

| Megjegyzés | Jelentés | Teendő |
|---|---|---|
| `.NET report imported` | a .NET coverage feltöltve | – |
| `.NET report imported, but tests failed (exit 1; see sonar-test-dotnet.log)` | van coverage, de egyes tesztek elbuktak, ezért a lefedettség alacsonyabb a valósnál | a `sonar-test-dotnet.log`-ban keresd a `Failed` sorokat; a tesztjavítás a projekt csapatáé |
| `<cél>:<projekt> ok` | az Angular-projekt lcov-ja feltöltve | – |
| `<cél>:<projekt> no report (exit 1; see sonar-test-frontend-<n>.log)` | az Angular-tesztek nem futottak le (gyakran fordítási hiba) | a megnevezett log |
| `<cél>:<projekt> skipped (unsupported test builder …)` | a projekt nem Karma-alapú (pl. Vitest) | jelenleg nem támogatott |
| `<cél> skipped (not an Angular workspace)` | a frontend célban nincs `angular.json` | csak Angular-frontend coverage támogatott |
| `.NET skipped (dotnet-coverage is not provisioned; rerun install)` | a lockban nincs rögzítve az eszköz | teljes `install` ([3.5](03-telepites-projektbe.md#35-sonar-coverage)) |
| `.NET failed (…)` / `<cél>:<projekt> failed (…)` | a tesztfuttatás el sem tudott indulni | a zárójelben lévő hibaüzenet |

## 5.4 Report vagy Gate mód

| | Report (alapértelmezés) | Gate |
|---|---|---|
| Találat esetén | exit 0, a lépés zöld | **exit 1** blokkoló találatnál |
| Technikai hiba | exit 2 | exit 2 |
| Sonar | csak feltölt | megvárja a Quality Gate-et; ha bukik, a lépés is elbukik |
| Hol használd | CI-pipeline (a generált pipeline alapból ezt használja), rendszeres riport | helyi ellenőrzés kiadás előtt, a generált pipeline `pipelineMode: "Gate"` mellett, egyedi pipeline-ok |

**Mi blokkol Gate módban** (baseline nélkül abszolút küszöb, ld. lent):

| Eszköz | Blokkol, ha |
|---|---|
| gitleaks | bármilyen találat |
| semgrep | legalább egy `ERROR` súlyosságú találat |
| nuget | legalább egy High vagy Critical |
| npm | legalább egy High vagy Critical |
| analyzers, osv, devskim | soha (csak leltár) |
| sonar | bukó Quality Gate (a lépés elbukik) |

A **`--staged`** (a pre-commit hook) módtól függetlenül mindig blokkol, ha titkot talál.

### Baseline: csak az új találat blokkoljon

Egy régebbi projektben a Gate mód addig piros, amíg a meglévő adósság el nem fogy. Hogy már most
bekapcsolható legyen, a meglévő blokkoló találatokat elfogadottként rögzítheted a
**`.security/baseline.json`**-ban. Ha ez a fájl létezik, a Gate mód (és a pre-commit hook) **csak a
baseline-ban nem szereplő** találatra blokkol. A riport ettől még minden találatot listáz, és a
`Message` oszlopban jelzi, mennyi volt ismert és mennyi új.

```powershell
security-toolkit scan --project-root . --update-baseline --history
```

- Report módban fut, és a futott eszközök **mostani blokkoló találatait** írja a baseline-ba,
  eszközönként felülírva. A `--tool`-lal egyetlen eszközé frissíthető, a többié marad.
- Egy eszköz, amelyik a futás során elbukott, és nem tudta listázni a találatait, nem frissül, a korábbi
  bejegyzése marad.
- **Ugyanazokkal a kapcsolókkal készítsd, mint ahogy a Gate fut.** A pipeline a Gitleaks-et
  `--history`-val futtatja (`pipelineHistory`), ezért a baseline-t is így kell felvenni. Enélkül a
  history-ban talált titkok mind újnak számítanak.
- **Commitold** a `baseline.json`-t: így a csapat és a pipeline ugyanazt látja, és egy új elfogadás egy
  review-zható diff. A fájl csak hash-eket tartalmaz, se titkot, se forráskódot.
- Csak tudatosan frissítsd. Ha egy új találatot elfogadsz, előbb a [5.5](#55-triázs-és-téves-riasztások)
  szerint döntsd el, hogy nem kell-e inkább javítani vagy szűken elnyomni.

Mi számít ugyanannak a találatnak (az ujjlenyomat):

| Eszköz | Ujjlenyomat | Következmény |
|---|---|---|
| semgrep | szabály + fájl + az érintett kódsorok (a szóközök nélkül) | a kód áthelyezése a fájlon belül nem tesz újjá egy ismert találatot; a sor módosítása igen |
| gitleaks | szabály + fájl + commit (csak `--history`-nál) + a Gitleaks redaktált egyezése (pl. `"ApiKey": "REDACTED"`) | a titok értéke nincs benne, ezért ha **ugyanarra a kulcsra** ugyanabban a fájlban egy másik titok kerül, az ismertnek számít |
| nuget | target + csomag + advisory | egy verziófrissítés, ami ugyanattól az advisorytól még érintett, ismert marad; egy új advisory új |
| npm | target + csomag + advisory | mint a NuGetnél |

A baseline nélküli projekt viselkedése nem változik. Az analyzers, az osv és a devskim továbbra sem
blokkol. A Sonar a saját Quality Gate-jét használja, ami a *Sonar way* profilban amúgy is az új kódot
nézi. A hibás vagy olvashatatlan `baseline.json` technikai hiba (exit 2), nem blokkolás.

## 5.5 Triázs és téves riasztások

Minden találatra három döntés lehetséges: **javítani**, **tudatosan elfogadni** (indoklással),
vagy **téves riasztásként elnyomni**. Az elnyomást mindig a lehető legszűkebben tedd (egy sor, egy
szabály), és mindig írd mellé az indoklást.

| Eszköz | Javítás | Elnyomás (téves riasztásnál) |
|---|---|---|
| **gitleaks** | a titok eltávolítása a kódból, a titok **cseréje** (ami egyszer a repóba került, kompromittáltnak számít), és helyette user-secrets / Key Vault / pipeline secret | soron belül: egy `gitleaks:allow` komment a sor végén. Vagy: a találat `Fingerprint`-je a repó gyökerében egy `.gitleaksignore` fájlba (soronként egy). A `.security/gitleaks.toml`-t **ne** szerkeszd, azt a toolkit kezeli. |
| **semgrep** | a kód javítása a szabály ajánlása szerint | soron belül: `// nosemgrep: <check_id>`. Egy egész fájl vagy mappa: a `.semgrepignore`-ba, **a managed blokkon kívülre**. |
| **nuget** | a csomag frissítése a javított verzióra (tranzitívnál a közvetlen függőség frissítése vagy explicit `PackageReference`) | ha nincs javítás, és a sebezhetőség nem érint: `Directory.Build.props`-ba `<ItemGroup><NuGetAuditSuppress Include="<advisory-URL>" /></ItemGroup>` |
| **npm** | `npm audit fix`, vagy a függőség frissítése; tranzitívnál `overrides` a `package.json`-ban | nincs beépített elnyomás; dokumentáld a döntést |
| **analyzers** | a kód javítása | `#pragma warning disable CA3xxx // indoklás` a lehető legkisebb blokkra, vagy `[SuppressMessage(..., Justification = "...")]` |
| **sonar** | a kód javítása | a Sonar UI-ban az issue-n: *Accept* (tudatos elfogadás) vagy *False positive*, mindig kommenttel. Csak a projekt adminja teheti meg. |
| **osv / devskim** | mint a nuget/npm, illetve az analyzers | kiegészítő eszközök, a triázsuk opcionális |

**Javasolt menet egy új projekt első futása után:**

1. **Gitleaks:** minden találatot azonnal nézz át. Egy valódi titkot cserélni kell, nem elég
   kitörölni, mert a git historyban megmarad.
2. **NuGet/npm High/Critical:** csomagfrissítés.
3. **Sonar Vulnerabilities és Security Hotspots:** a Sonar UI-ban.
4. **Semgrep `ERROR`:** a Scans fülön.
5. Ezután a cél a stabil alapállapot: minden futás után csak az **új** találatokat kell nézni
   (a `summary.md` számainak változása, és a Sonar *New Code* nézete).

[Következő: 6. Helyi futtatás és CLI →](06-helyi-futtatas-es-cli.md)
