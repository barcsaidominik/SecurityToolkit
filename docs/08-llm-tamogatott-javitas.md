# 8. LLM-támogatott kiértékelés és javítás

[← 7. Hibaelhárítás](07-hibaelharitas.md) · [Tartalomjegyzék](README.md)

Ez a fejezet azt írja le, hogyan dolgozza fel egy fejlesztő és egy kódoló LLM-agent **együtt** egy projekt összes nyitott scanner- és Sonar-találatát: lekérdezés, egységes
listába rendezés, triázs, javítás kis csomagokban, ellenőrzés. Projektfüggetlen: bármely
célprojektre alkalmazható, amelyben a toolkit telepítve van és a pipeline már lefutott.

A fejezet két olvasónak szól:

- **Fejlesztő:** mit kell előkészítenie, hol kell döntenie, mit kell átnéznie.
- **Agent:** mit kap bemenetként, mit kell előállítania, mit tehet és mit nem. Az agent ezt a
  fájlt kapja meg utasításként (ld. [8.9 Indító prompt](#89-indító-prompt-sablon)), a
  [8.8 Agent-szerződés](#88-agent-szerződés) szakasz kötelező érvényű rá.

> **Alapelv:** az agent gyűjt, rendez, javasol és javít; **a döntés a fejlesztőé.** Az agent
> soha nem dönti el egyedül, hogy egy találat téves riasztás, és soha nem javít tömegesen, egyetlen
> nagy diffben. A munka nagyobbik része annak eldöntése, mit *nem* kell javítani.

## 8.1 Áttekintés

```
 0. Előkészítés        fejlesztő   tiszta alapállapot, hozzáférések
 1. Adatgyűjtés        agent       Sonar API + results/<run-id>/ (vagy helyi újrafuttatás)
 2. Normalizálás       agent       findings.json + triage.md (szabálycsoportonként)
 3. Triázs             fejlesztő   ◄── DÖNTÉSI PONT: javít / elnyom / elfogad / később
 4. Javítás            agent       csoportonként egy branch, egy PR
 5. Ellenőrzés         agent       build, teszt, célzott újra-scan, baseline-összevetés
 6. Review + merge     fejlesztő   ◄── DÖNTÉSI PONT
 7. Lezárás            mindkettő   Sonar-állapotok, work itemek, új baseline
```

A 3. és a 6. lépés emberi döntés. Az agent ezeknél **megáll és vár**.

## 8.2 Előfeltételek

### A projektben (fejlesztő biztosítja)

| # | Feltétel | Miért | Ellenőrzés |
|---|---|---|---|
| 1 | A toolkit telepítve, a `.security/` commitolva ([3. fejezet](03-telepites-projektbe.md)) | ebből olvassa az agent a configot és ezzel futtat újra | `security-toolkit install --project-root . --check` → exit 0 |
| 2 | Legalább egy teljes pipeline-futás Sonarral ([4. fejezet](04-pipeline.md)) | ez a kiinduló állapot (baseline) | a `summary.md`-ben minden eszköz `completed` |
| 3 | **A tesztek zöldek** a fő ágon | a javítások ellenőrzése a tesztekre épül; piros alapból nem lehet regressziót mérni | `dotnet test` / `ng test` helyben |
| 4 | **A formázás/lint egyszer lefutott és külön commitban be van vezetve** | különben minden javító diffbe formázási zaj kerül, ami reviewzhatatlan | `git log` mutatja a formázó commitot; egy újrafuttatás nem módosít fájlt |
| 5 | Tiszta munkakönyvtár a fő ág friss állapotán | a csoportonkénti branchek ebből ágaznak | `git status` üres |
| 6 | A projekt helyben buildel és tesztel (a teszt-SQL connection string env-ben, ha kell) | az agent helyben ellenőriz | [6.0](06-helyi-futtatas-es-cli.md) |

### Hozzáférések

| Mi | Kell-e | Megjegyzés |
|---|---|---|
| **Saját Sonar user token** (`SONAR_TOKEN` env, munkamenetenként) | kötelező | ugyanaz a token, amit helyben minden projektnél használsz ([2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)); **nem** a pipeline project analysis tokenje. Lekérdezéshez a *Browse* jog elég, a helyi Sonar-újrafuttatáshoz *Execute Analysis* is kell. |
| Sonar *Administer Issues* / *Administer Security Hotspots* jog | csak a 7. lépéshez | az elnyomások Sonar-oldali rögzítéséhez; ezt a fejlesztő is megteheti a UI-ban |
| Azure DevOps hozzáférés (PAT vagy MCP) | opcionális | a pipeline-artifact letöltéséhez és a work itemekhez. Helyi újrafuttatással kiváltható. |
| Docker Desktop fut | a helyi újra-scanhez | a Semgrep/Gitleaks/OSV konténerben fut |

> A tokent **env-változóban** add át, ne a promptban és ne fájlban. Az agent nem írja ki, nem
> logolja, és nem teszi bele egyetlen generált fájlba sem.

> **Az agent a saját folyamatának env-jét látja, nem a te terminálodét.** A `SONAR_TOKEN`-t abból a
> munkamenetből örökli, amelyből elindítottad, akár egy régi, azóta érvénytelen tokent is. Ha a
> tokent az agent indítása *után* állítottad be vagy cserélted, indítsd újra az agentet abból a
> terminálból. Az agent ezt az 1. lépés előtt ellenőrzi (ld. alább).

**Ellenőrzés** (az agent futtatja az 1. lépés előtt, a token kiírása nélkül): a token a **hatályos**
Sonar-URL-en (ld. [8.4](#84-1-lépés--adatgyűjtés-agent)) érvényes-e, és milyen típusú:

```powershell
$t = $env:SONAR_TOKEN; $t.Substring(0,4)    # squ_ = user token (helyes), sqp_ = project analysis token (helyben hibás)
$h = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${t}:")) }
(Invoke-RestMethod "<hatályos sonar.url>/api/authentication/validate" -Headers $h).valid   # True kell
```

`False` esetén az agent megáll, és jelzi, melyik URL-en és milyen típusú tokennel bukott el.

### Amit a fejlesztő előre eldönt

- **Hatókör:** mely eszközök és súlyosságok tartoznak ebbe a körbe (pl. „minden security
  találat + Sonar Bug, a code smell-ek nem”). Alapértelmezés: minden security-találat, a code
  smell-ek nélkül.
- **Branch- és PR-konvenció** (pl. `fix/security-<szabály>`, cél-ág, kötelező reviewer).
- **Work item hely** (ha van): melyik Feature/User Story alá kerülnek a feladatok.

## 8.3 0. lépés – Előkészítés (fejlesztő)

1. Teljesítsd a 8.2 feltételeit.
2. Jegyezd fel a **baseline-t**: a pipeline-futás run-id-ját (build-szám) és a fő ág commit SHA-ját.
3. Indítsd az agentet a [8.9](#89-indító-prompt-sablon) prompttal, kitöltve a hatókört.

## 8.4 1. lépés – Adatgyűjtés (agent)

A munkamappa: **`.security/results/remediation-<YYYY-MM-DD>/`**. A `.security/results/` a
toolkit által gitignore-olt, így semmi nem kerül véletlenül commitba.

### Scanner-eredmények

Két egyenértékű forrás, ebben a preferencia-sorrendben:

1. **Helyi újrafuttatás** a fő ág baseline commitján:
   ```powershell
   security-toolkit scan --include-sonar --run-id remediation-baseline
   ```
   Előny: ugyanaz a parancs ellenőrzi később a javítást (5. lépés). Ha a projekt nem buildel
   helyben vagy a Sonar-futás túl hosszú, a Sonar kihagyható (`--include-sonar` nélkül), az
   adatai úgyis az API-ból jönnek.
2. **A pipeline `security-results` artifactja** ([5.1](05-kiertekeles.md#51-a-security-results-artifact)),
   a `<build-szám>/` mappa átmásolva a munkamappába.

A beolvasandó fájlok eszközönként: [5.2 táblázat](05-kiertekeles.md#52-eszközönként-hol-a-részlet-mit-jelent).
Elsődleges a `status.json` (melyik eszköz futott le, `failed` eszköz adatát nem szabad teljesnek
tekinteni), utána a nyers riportok (`semgrep.json`, `gitleaks.json`, `dotnet-*-nuget.json`,
`npm-*.json`, `osv.json`, `devskim.sarif`, `dotnet-*-build.log`).

### Sonar Web API

Hitelesítés: HTTP Basic, felhasználónév = token, jelszó üres (ugyanígy hívja a toolkit is).

A Sonar URL-t és a projektkulcsot a **hatályos configból** kell venni, pontosan úgy, ahogy a `scan`
is teszi: a `.security/security.config.json` fölé a `.security/security.config.local.json`
(ha létezik) mélyen ráfésülődik. Fejlesztői gépen a local fájl jellemzően egy **helyi** Sonarra
mutat (pl. `http://127.0.0.1:9000`); ilyenkor a helyi scan oda töltött fel, és az adatot is onnan
kell lekérni, nem a commitolt (közös/CI) URL-ről. A két Sonar tartalma eltérhet (más futás, más
triázs-állapot), ezért a gyűjtési jelentésben mindig szerepeljen, melyik URL-ről jött az adat.

| Adat | Endpoint | Fontos paraméterek |
|---|---|---|
| Nyitott issue-k | `GET /api/issues/search` | `componentKeys=<projectKey>` (újabb verziókban `components`), `resolved=false`, `ps=500`, `p=1..n`, opcionálisan `types=VULNERABILITY,BUG` |
| Security hotspotok | `GET /api/hotspots/search` | `project=<projectKey>`, `status=TO_REVIEW`, `ps=500`, `p` |
| Szabály-leírás | `GET /api/rules/show` | `key=<rule>`; csoportonként egyszer, a triázshoz |
| Quality Gate állapot | `GET /api/qualitygates/project_status` | `projectKey=<projectKey>` |
| Coverage | `GET /api/measures/component` | `component=<projectKey>`, `metricKeys=coverage,new_coverage` |

- **Lapozási korlát:** a Sonar legfeljebb 10 000 találatot ad vissza egy lekérdezésre (`p*ps ≤
  10 000`). Ha a `total` ennél nagyobb, szeletelni kell (pl. `types`, `severities` vagy
  `createdAfter`/`createdBefore` szerint).
- A hotspotok **nem** issue-k, külön endpointon vannak, és mindig emberi döntést igényelnek.
- A nyers válaszokat a munkamappába kell menteni (`sonar-issues-all.json`,
  `sonar-hotspots.json`), hogy a normalizálás ismételhető legyen újabb API-hívás nélkül.

**Kimenet:** a munkamappában a nyers adatok, és egy rövid jelentés a fejlesztőnek: mely forrásból
mennyi adat jött, és melyik eszköz hiányzik vagy `failed`.

## 8.5 2. lépés – Normalizálás (agent)

### Egységes találat-formátum: `findings.json`

Minden forrásból ugyanez a rekord készül:

```json
{
  "id": "semgrep:csharp.lang.security.sqli...:src/Api/Repo.cs:42",
  "tool": "semgrep",
  "rule": "csharp.lang.security.sqli.csharp-sqli",
  "category": "injection",
  "severity": "high",
  "toolSeverity": "ERROR",
  "file": "src/Api/Repo.cs",
  "line": 42,
  "message": "...",
  "fingerprint": "...",
  "url": "http://<sonar>/project/issues?id=...&issues=... (ha van)",
  "duplicates": ["devskim:DS104456:src/Api/Repo.cs:42"]
}
```

- **`file`** mindig repó-gyökérhez képesti, `/` elválasztóval.
- **`severity`** egységes skála: `critical | high | medium | low | info`. Az eszköz saját értéke a
  `toolSeverity`-ben marad. (Semgrep `ERROR` → `high`, `WARNING` → `medium`; Sonar
  `BLOCKER/CRITICAL` → `critical/high`; NuGet/npm a saját skáláján; Gitleaks mindig `critical`.)
- **Függőség-találatoknál** a `file` a projektfájl (`.csproj` / `package.json`), a `rule` az
  advisory azonosítója (GHSA/CVE), a `message` tartalmazza a csomagot, a verziót és a javított
  verziót.
- **Gitleaks:** a titok értéke a riportban redaktált; az agent **nem** próbálja a fájlból
  kiolvasni és nem írja le.

### Duplikáció-szűrés

Ugyanazt a hibát több eszköz is jelezheti (pl. Semgrep + DevSkim + Sonar ugyanarra a sorra; NuGet
+ OSV ugyanarra a csomagra). Összevonási szabály: azonos `file` + `line` (±2 sor) + azonos
`category`, illetve függőségeknél azonos csomag + advisory. A megmaradó rekord a
legmegbízhatóbb eszközé (sorrend: Sonar > Semgrep > analyzers > DevSkim; NuGet/npm > OSV), a
többi a `duplicates` listába kerül.

### Csoportosítás: `triage.md`

**Szabályonként** (nem fájlonként) csoportosíts: egy szabály javítási mintája mindenhol ugyanaz,
és így egy csoport = egy döntés = egy PR. A `triage.md` szerkezete:

```markdown
# Triázs – <projekt> – baseline <run-id> @ <commit>

Összesen: 312 találat (duplikáció után 241), 23 szabálycsoport, + 14 security hotspot.

| # | Csoport (eszköz:szabály) | Db | Súlyosság | Érintett | Agent javaslata | Indoklás | Becsült méret | DÖNTÉS |
|---|---|---|---|---|---|---|---|---|
| G01 | gitleaks:generic-api-key | 1 | critical | appsettings.json | ROTÁLÁS + javítás | valódi kulcsnak tűnik | S | |
| G02 | nuget:GHSA-xxxx (System.Text.Json 8.0.0) | 3 | high | 3 projekt | javít (frissítés 8.0.5) | patch-verzió, nincs breaking | S | |
| G03 | sonar:csharpsquid:S2077 | 4 | high | src/Api/... | átnézendő | 2 valódinak tűnik, 2 konstans stringből épül | M | |
| ... |
```

Az **Agent javaslata** oszlop értékei: `javít`, `téves riasztás`, `elfogad`, `később`,
`átnézendő` (ha a kódból nem dönthető el egyértelműen), `ROTÁLÁS + javítás` (titkoknál). Az
indoklásnak konkrétnak kell lennie (mit nézett meg a kódban), nem a szabályleírás ismétlése.

A hotspotok külön táblázatba kerülnek, ugyanígy, egyenként (nem csoportosítva).

A `triage.md` végén: a hiányzó vagy `failed` eszközök listája, és az, hogy mely adat **nem**
megbízható emiatt.

**Az agent itt megáll**, és átadja a `triage.md`-t a fejlesztőnek.

## 8.6 3. lépés – Triázs (fejlesztő)

A fejlesztő kitölti a **DÖNTÉS** oszlopot minden csoportra:

| Döntés | Jelentés | Ki hajtja végre |
|---|---|---|
| `javít` | valódi probléma, javítani kell | agent (4. lépés) |
| `téves` | téves riasztás; a legszűkebb elnyomás + indoklás | agent az elnyomást a kódban, a Sonar-állapotot a 7. lépésben |
| `elfogad` | valódi, de tudatosan vállalt kockázat | csak dokumentálás (Sonar *Accept* kommenttel) |
| `később` | valódi, de nem ebben a körben | work item, a kód nem változik |
| `saját` | a fejlesztő maga javítja (pl. üzleti logikát érint) | fejlesztő |

Egy csoporton belül vegyes döntés is lehet (pl. `G03: javít, kivéve Repo.cs:88 → téves`).

### Javasolt sorrend

1. **Titkok (Gitleaks).** Ez **nem** kódjavítás: a titkot az üzemeltetésnek **cserélnie kell**,
   mert a git historyban megmarad. A kódból kivenni és user-secrets / Key Vault / pipeline secret
   mögé tenni csak a második lépés. A cserét a fejlesztő szervezi, nem az agent.
2. **Függőségek High/Critical** (NuGet, npm). Mechanikus, de major verzióugrásnál breaking change
   lehet: ilyenkor `saját` vagy `átnézendő`.
3. **Sonar Vulnerability, Semgrep ERROR, analyzer CA3xxx**, és a hotspotok.
4. **Sonar Bug**, majd a többi (ha a hatókör tartalmazza).

A formázási és lint-szabályok **nem** ebbe a körbe tartoznak: azok külön folyamat (8.2/4. pont).

Kitöltés után a fejlesztő visszaadja a `triage.md`-t az agentnek, megadva, **mely csoportokkal
kezdjen** (javasolt: egyszerre 1–3 csoport).

## 8.7 4–5. lépés – Javítás és ellenőrzés (agent)

Csoportonként (vagy a fejlesztő által összevont csoportokként):

1. **Branch** a fő ág friss állapotáról, a megadott konvenció szerint (pl.
   `fix/security-G02-system-text-json`).
2. **Megértés a javítás előtt.** Minden érintett helyen el kell olvasni a környező kódot. Ha a
   javítás viselkedést változtatna (pl. SQL-építés átírása paraméterezettre), és a helyet nem fedi
   teszt, **először egy karakterizációs tesztet** kell írni, ami a jelenlegi helyes viselkedést
   rögzíti.
3. **Javítás** a szabály ajánlása és a projekt meglévő mintái szerint. Minimális diff: csak a
   találathoz szükséges változás, mellékes refaktor és formázás nélkül.
4. **Elnyomás** (csak `téves` döntésnél) a legszűkebb formában, mindig indoklással, az
   [5.5 táblázat](05-kiertekeles.md#55-triázs-és-téves-riasztások) szerinti módon. Toolkit-managed
   fájlba (`.security/gitleaks.toml`, managed blokkok) nem kerülhet elnyomás.
5. **Ellenőrzés**, ebben a sorrendben:
   - az érintett projekt(ek) buildje, figyelmeztetés-növekedés nélkül;
   - az érintett tesztek, majd a teljes tesztcsomag, ha a változás megosztott kódot érint;
   - **célzott újra-scan** az érintett eszközzel:
     ```powershell
     security-toolkit scan --tool <eszköz> --run-id remediation-G02
     ```
     (Sonar-szabálynál a Sonar-futás lassú: elég, ha a pipeline a PR-on lefuttatja; ilyenkor a
     PR-leírásban jelezni kell, hogy a Sonar-ellenőrzés a pipeline-ra vár.)
   - **összevetés a baseline-nal:** a csoport találatai eltűntek, és **nincs új találat** ugyanabban
     az eszközben. Az eredmény (előtte/utána darabszám) bekerül a PR-leírásba.
6. **Commit** csoportonként egy (vagy logikai lépésenként néhány), a projekt commit-konvenciója
   szerint, a csoport azonosítójával.
7. **PR** (vagy push, ha a fejlesztő így kéri) az alábbi leírással:

```markdown
## Mit javít
G02 – nuget:GHSA-xxxx – System.Text.Json 8.0.0 → 8.0.5 (3 projekt)

## Hogyan
<1–3 mondat a javítás mintájáról>

## Ellenőrzés
- build: OK (warning: 12 → 12)
- teszt: 214/214 zöld
- `scan --tool nuget`: 3 → 0 találat, új találat nincs

## Elnyomások (ha van)
- <fájl:sor> – <szabály> – <indoklás>

## Nyitott kérdés / kockázat
- <ha van>
```

Ha egy javítás közben kiderül, hogy a triázs-döntés téves volt (pl. a „téves riasztás” mégis
valódi), vagy a javítás nagyobb, mint amire a döntés szólt, az agent **megáll**, és a fejlesztőhöz
fordul. Nem dönt át csendben.

## 8.8 Agent-szerződés

Ez a szakasz kötelező érvényű az agentre.

### MUSZÁJ

- Minden lépés elején ellenőrizni a 8.2 előfeltételeit, és ha valamelyik nem teljesül, ezt
  jelezni, mielőtt továbbmenne (pl. piros teszt a fő ágon → megállás).
- A Sonar URL-t és projektkulcsot a hatályos configból venni (`security.config.json` +
  `security.config.local.json`), sosem csak a commitolt fájlból; és a tokent ezen az URL-en
  ellenőrizni (8.2), mielőtt bármit lekérdezne.
- Ha egy hitelesítés elbukik, előbb a saját feltételezéseit ellenőrizni (melyik URL, melyik token,
  honnan örökölte), és csak utána a fejlesztő környezetét hibáztatni vagy tőle változtatást kérni.
- A 2. lépés után megállni és átadni a `triage.md`-t; csak kitöltött DÖNTÉS oszlop után javítani.
- Csak a fejlesztő által kijelölt csoportokon dolgozni, csoportonként külön branchen.
- Minden javítást buildeléssel, tesztekkel és célzott újra-scannel ellenőrizni, és az eredményt
  számokkal jelenteni. Ha egy ellenőrzés nem futott le, ezt kimondani, nem elhallgatni.
- Minden elnyomás mellé indoklást írni, a legszűkebb hatókörrel.
- Bizonytalanságot jelölni (`átnézendő`), nem elfedni egy határozott javaslattal.

### TILOS

- Titkot (tokent, jelszót, connection stringet) kiírni, logolni, fájlba vagy commitba tenni,
  vagy a redaktált Gitleaks-találat értékét a forrásból kiolvasni és megismételni.
- Egyedül eldönteni, hogy valami téves riasztás, és elnyomni.
- Sonar-issue vagy hotspot állapotát módosítani (`do_transition`, `change_status`) a fejlesztő
  kifejezett jóváhagyása nélkül.
- Szabályt globálisan kikapcsolni, a Sonar Quality Profile-t vagy Quality Gate-et módosítani.
- Toolkit-managed fájlt szerkeszteni (`.security/` tartalma, managed blokkok, a generált
  pipeline-YAML).
- Javítást formázással, átnevezéssel vagy mellékes refaktorral keverni.
- Egy PR-ba több, egymástól független szabálycsoportot tenni (kivéve, ha a fejlesztő összevonta).
- A git historyt átírni (force push, rebase a megosztott ágon) a titkok eltávolítására. Ez
  szervezeti döntés.
- Tesztet törölni, kikapcsolni vagy gyengíteni azért, hogy a javítás „zöld” legyen.
- Merge-ölni.

### Elvárt kimenetek

| Lépés | Fájl / artefaktum | Hol |
|---|---|---|
| 1 | nyers adatok + rövid gyűjtési jelentés | munkamappa |
| 2 | `findings.json`, `triage.md` | munkamappa |
| 4–5 | branch + commit(ok) + PR-leírás csoportonként | repó |
| 7 | a 8.10 lezárási összesítő | a válaszban / work itemben |

## 8.9 Indító prompt (sablon)

A fejlesztő ezt adja az agentnek (a `<…>` részeket kitöltve):

```text
Dolgozz a docs/08-llm-tamogatott-javitas.md szerint; a 8.8 Agent-szerződés kötelező.

Projekt: <repó útvonala>
Baseline: pipeline run <build-szám>, commit <sha>   (vagy: futtass helyi baseline-t)
Hatókör: <pl. minden security-találat + Sonar Bug; code smell nem>
Sonar: a saját user tokenem a SONAR_TOKEN env-ben van; URL és projektkulcs a hatályos configból
(security.config.json + security.config.local.json). Az 1. lépés előtt ellenőrizd a tokent ezen az URL-en.
Branch-konvenció: <pl. fix/security-<csoport>>, cél-ág: <main>
PR: <hozd létre | csak pusholj | csak commitolj helyben>
Work item: <ha van: szülő azonosító>

Most: 1–2. lépés (adatgyűjtés, normalizálás), majd állj meg a triage.md-vel.
```

A triázs után:

```text
A triage.md DÖNTÉS oszlopa kitöltve. Kezdd ezekkel: <G02, G05>. Csoportonként külön branch és PR.
```

## 8.10 7. lépés – Lezárás (fejlesztő, az agent előkészíti)

1. **Sonar-állapotok:** a `téves` és `elfogad` döntésű Sonar-issue-kat a UI-ban (vagy a fejlesztő
   jóváhagyásával az API-n: `POST /api/issues/do_transition`, `transition=falsepositive` vagy
   `accept` – régebbi verziókon `wontfix`) kommenttel kell lezárni; a hotspotokat
   `POST /api/hotspots/change_status` (`status=REVIEWED`, `resolution=SAFE|FIXED|ACKNOWLEDGED`).
   Ehhez *Administer Issues* / *Administer Security Hotspots* jog kell. Az agent ehhez egy kész
   listát ad (issue-kulcs, döntés, indoklás).
2. **Work itemek:** a `később` csoportokból szabálycsoportonként **egy** work item, nem
   találatonként. A leírásba a Sonar-link és a `triage.md` sora kerül.
3. **Új baseline:** a merge-ök után egy teljes pipeline-futás; az agent összesíti az eredeti és az
   új számokat eszközönként (előtte → utána, ebből javítva / elnyomva / elfogadva / később).
4. **Megelőzés:** ha a régi adósság rendezett, érdemes a Sonar Quality Gate-et az *új kódra*
   vonatkozó feltételekre állítani, és onnantól csak az új találatokat triázsolni
   ([5.5](05-kiertekeles.md#55-triázs-és-téves-riasztások), 5. pont).

## 8.11 Ismert korlátok

- A **Gate mód abszolút küszöb** ([5.4](05-kiertekeles.md#54-report-vagy-gate-mód)): egy javító
  PR pipeline-ja addig piros maradhat Gate módban, amíg a régi adósság el nem fogy. A generált
  pipeline Report módban fut, így ez ott nem akadály.
- A **Sonar-ellenőrzés lassú** helyben (teljes build + elemzés), ezért a Sonar-szabályok javítását
  gyakran csak a PR pipeline-ja igazolja.
- **Tranzitív függőségek:** a javítás sokszor egy közvetlen függőség frissítése vagy explicit
  `PackageReference`/`overrides`; az agentnek meg kell mutatnia, melyik közvetlen függőség húzza be.
- A **Semgrep `failed`** állapota (parse-hiba) azt jelenti, hogy a lista nem teljes; az ilyen
  fájlokra a triázs nem ad teljes képet ([5.2](05-kiertekeles.md#52-eszközönként-hol-a-részlet-mit-jelent)).
- Az LLM **téved**: a javaslatai (különösen a `téves riasztás`) ellenőrizendők, és a javításai
  ugyanazt a code review-t igénylik, mint bármely más változtatás.

[← Tartalomjegyzék](README.md)
