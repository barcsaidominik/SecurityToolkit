# 3. Telepítés egy célprojektbe

[← 2. Sonar és teszt-SQL](02-sonar-es-mssql.md) · [Tartalomjegyzék](README.md) · [Következő: 4. Pipeline →](04-pipeline.md)

A projekt-telepítés egy tetszőleges .NET és/vagy Angular (npm) repóba generálja a `.security/`
mappát, a pipeline-YAML-t és néhány managed blokkot. Ez repónként egyszer kell, utána a generált
fájlokat commitolni kell. A többi fejlesztőnek és a CI-nak ezután nincs teendője a repóval.

## 3.1 Előfeltételek a telepítést végző gépen

A telepítést egy **fejlesztői gépen** végezd (nem a CI-gépen), és ott legyen:

- Git, .NET SDK (amit a projekt `global.json`-ja kér), Node (ha van frontend);
- **Docker (Desktop, Linuxon Docker Engine), futó állapotban.** A teljes telepítés letölti a scanner-image-eket, és a
  `security.lock.json`-ba digestre rögzíti őket. Docker nélkül csak `--configure-only` megy, abból
  viszont nem lesz futtatható pipeline (ld. 3.4);
- a SecurityToolkit CLI: a SecurityToolkit repó klónjában **rendszergazdaként** `install.bat --no-infra`,
  majd új terminál ([1.3](01-gep-es-agent.md#13-a-securitytoolkit-cli-telepítése-installbat)); Linuxon
  `sudo bash install.sh --no-infra` ([1.7](01-gep-es-agent.md#17-linux-ci-gép-windows-helyett));
- a Sonar URL-je és projektkulcsa ([2.2](02-sonar-es-mssql.md#22-a-sonar-első-beüzemelése)), ha
  Sonart is használsz.

A repóban legyen tiszta a munkaterület. Érdemes külön ágon dolgozni:

```powershell
cd C:\Work\<repó>
git switch -c security-toolkit
```

## 3.2 Interaktív telepítés

```powershell
security-toolkit install --project-root .
```

Ha még nincs `.security/security.config.json`, a teljes varázsló indul. Igen/nem kérdéseknél
`i`/`n` a válasz, Enter = a szögletes zárójelben nagybetűvel jelölt alapérték.

### Célok

| Kérdés | Teendő |
|---|---|
| **.NET solution/projekt célok** | a felsorolt `.sln`/`.slnx`/`.csproj` közül válaszd ki, mit vizsgáljon (sorszámok vesszővel; Enter = mind; 0 = egyik sem). Általában a fő solution. |
| **Frontend/npm célkönyvtárak** | az `package.json`-t tartalmazó mappák közül a valódi frontend(ek), pl. `src/Client`. |

### Eszközök

Mind a 8 eszközre rákérdez ([leírásuk](06-helyi-futtatas-es-cli.md#62-a-8-scanner)):

| Eszköz | Javaslat |
|---|---|
| .NET security analyzers | **igen**, ha van .NET cél |
| Semgrep CE | **igen** |
| Gitleaks | **igen** |
| NuGet Audit | **igen**, ha van .NET cél |
| npm audit | **igen**, ha van frontend (`package-lock.json` kell hozzá) |
| OSV-Scanner | opcionális, kiegészítő függőség-vizsgálat |
| DevSkim | opcionális, kiegészítő lint; csak szűkítve érdemes (`devskim.ignoreGlobs`/`ignoreRuleIds`, ld. [6.4](06-helyi-futtatas-es-cli.md#64-a-securityconfigjson-kulcsai)) |
| SonarScanner for .NET | **igen**, ha van közös Sonar |

### További kérdések

| Kérdés | Javaslat / magyarázat |
|---|---|
| Semgrep hatókör | `repository` = a teljes repó, C#-pal együtt. `frontend` = csak a kiválasztott frontendek, `*.cs` nélkül. A Semgrep CE a modern C#-ot hiányosan elemzi (parse-hibák), ezért ha Sonar is fut, a `frontend` a jobb választás. |
| Hiányzó rendszerfüggőségek telepítése wingettel? | fejlesztői gépen kényelmes. Ha igen: SDK-csatorna (pl. `10.0`), Node winget-ID és verzió. Linuxon ez a kérdés elmarad (ld. [1.7](01-gep-es-agent.md#17-linux-ci-gép-windows-helyett)). |
| Gyártói Semgrep registry szabályok letöltése? | **igen**. A `p/csharp` és `p/typescript` szabálykészlet a `.security/rules/` alá kerül, és commitolva lesz, így a CI internet nélkül, rögzített szabályokkal fut. |
| Sonar URL | a közös Sonar címe, pl. `http://ci-gep.<domain>.hu:9000` |
| Sonar projektkulcs | a [2.2](02-sonar-es-mssql.md#projekt-létrehozása--mindig-kézzel)-ben kézzel létrehozott kulcs |
| Egyetlen Sonar .NET target | a Sonar ezt a solutiont buildeli és elemzi |
| Teszt-coverage mérése és feltöltése? | ld. [3.5](#35-sonar-coverage). Ha igen: `dotnet test --filter` kifejezés (üres = minden teszt), majd a teszt-env-változók `ENV_NEV=PipelineValtozoNeve` alakban, egyesével, üres sorral lezárva. |
| Gitleaks staged pre-commit hook? | **igen** (ld. 3.6) |
| VS Code extensionök telepítése? | igen, ha a `code` parancs elérhető (SonarLint, DevSkim) |
| Külön manuális Azure security YAML előállítása? | **igen**, ebből lesz a pipeline |
| Azure self-hosted agent pool neve | az [1.4](01-gep-es-agent.md#14-agent-pool-létrehozása-az-azure-devops-ban)-ben létrehozott pool, pl. `SecurityToolkit` |
| Gitleaks a teljes git history-t vizsgálja? | `nem` = csak a munkaterület. `igen` = minden korábbi commit is. Első bevezetéskor egyszer hasznos az `igen`, utána zajos. |
| NuGetAuthenticate task kell privát feedhez? | **igen**, ha a projekt privát Azure Artifacts NuGet feedet használ |
| Privát npm `.npmrc` relatív útvonala | üres, ha nincs privát npm feed |

### Lokális beállítás és befejezés

- **Lokális Sonar URL felülírás?** Csak akkor `igen`, ha a saját gépeden egy másik (pl. helyi)
  Sonart használsz. Ez a gitignore-olt `.security/security.config.local.json`-ba kerül, a
  commitolt configot nem érinti.
- **Telepítés után teljes Report vizsgálat fusson?** Első telepítésnél hasznos próbának, de a Sonar
  feltöltés ilyenkor nem fut (ahhoz `--include-sonar` és a saját **user tokened** kell a `SONAR_TOKEN`-ben,
  ld. [2.2](02-sonar-es-mssql.md#személyes-user-token-helyi-futtatáshoz)).

### Mit csinál a telepítő

1. Kiírja a `.security/` mappát: config, lock, PS-scriptek, `gitleaks.toml`, `SecurityToolkit.props`.
2. Managed blokkokat ír (`# BEGIN SECURITY-TOOLKIT` … `# END SECURITY-TOOLKIT` jelölők közé; a
   blokkon kívüli tartalomhoz nem nyúl):
   - `.gitignore`: a futásidejű fájlok kizárása;
   - `.semgrepignore`: build-kimenetek, `node_modules`, `.security/`;
   - a legközelebbi `Directory.Build.props`: a `.security/SecurityToolkit.props` importja
     (analyzerek + NuGet audit, csak a toolkit buildjeiben aktív);
   - a projektmappák `.editorconfig`-ja: a CA30xx/CA31xx biztonsági szabályok `warning` szintre.
3. Letölti a Semgrep-szabályokat a `.security/rules/` alá.
4. **Provisioning:** letölti a Docker-image-eket, a digesteket rögzíti a `security.lock.json`-ban,
   és telepíti/rögzíti a .NET-eszközöket (DevSkim, SonarScanner, dotnet-coverage) a `.security/bin/` alá.
5. Telepíti a git hookot és a VS Code extensionöket (ha kérted).
6. Legenerálja az `azure-security-toolkit.yml`-t (és/vagy GitHubon a `.github/workflows/security-toolkit.yml`-t, ld. `pipelineProviders`, [4.6](04-pipeline.md#46-github-actions-az-azure-helyett-vagy-mellett)).
7. A hátralévő kézi teendőket kiírja a konzolra és a `.security/pending-actions.md`-be.

Minden módosított fájlról mentés készül a `.security/backups/<időbélyeg>/` alá (ld. 3.7, `restore`).

**Ellenőrzés:**
```powershell
security-toolkit install --project-root . --check
```
Exit code 0 és „In sync” üzenet. Nézd át a `.security/pending-actions.md`-t is.

## 3.3 Mi kerül commitba

| Fájl / mappa | Commit? | Megjegyzés |
|---|---|---|
| `.security/security.config.json` | **igen** | a csapat közös beállításai |
| `.security/security.lock.json` | **igen** | az eszközök rögzített verziói/digestjei; a CI `provision --require-pinned`-del erre épít |
| `.security/SecurityToolkit.props`, `gitleaks.toml` | **igen** | toolkit által kezelt („owned”) fájlok, kézzel ne szerkeszd |
| `.security/rules/` | **igen** | a rögzített Semgrep-szabályok |
| `.security/security.config.local.json.example` | **igen** | minta a lokális felülíráshoz |
| `azure-security-toolkit.yml`, `.github/workflows/security-toolkit.yml` | **igen** | a generált pipeline (amelyiket kérted); kézzel ne szerkeszd |
| `.gitignore`, `.semgrepignore`, `Directory.Build.props`, `.editorconfig` | **igen** | a managed blokkok miatt módosultak |
| `.security/baseline.json` | **igen**, ha van | az elfogadott (ismert) blokkoló találatok; a `scan --update-baseline` írja ([5.4](05-kiertekeles.md#baseline-csak-az-új-találat-blokkoljon)) |
| `.security/security.config.local.json` | nem (gitignore) | gépspecifikus felülírás |
| `.security/results/`, `bin/`, `backups/`, `npm-tools/`, `vendor/` | nem (gitignore) | futásidejű adat |
| `.security/install-state.json`, `pending-actions.md` | nem (gitignore) | helyi telepítési állapot |

```powershell
git add -A
git status          # ellenőrizd, hogy csak a fenti fájlok vannak benne
git commit -m "SecurityToolkit telepítés"
git push -u origin security-toolkit
```

Ha a pre-commit hook már települt, a commit előtt lefut egy Gitleaks-vizsgálat (Docker kell hozzá).
Ha talál valamit, a commit nem jön létre (ld. 3.6).

## 3.4 Telepítés felügyelet nélkül (`--non-interactive`)

Scriptből, vagy ha a válaszok már egy profilban vannak:

```powershell
security-toolkit install --project-root . --non-interactive --config-file .\security-profile.json
```

Példa `security-profile.json` (csak az alapértéktől eltérő kulcsok kellenek; a célokat a telepítő
magától felderíti, ha nem adod meg):

```json
{
  "dotnetTargets": [ "src/MyApp.sln" ],
  "frontendTargets": [ "src/Client" ],
  "tools": { "analyzers": true, "nuget": true, "npm": true, "semgrep": true, "gitleaks": true, "sonar": true },
  "semgrepScope": "frontend",
  "installHook": true,
  "agentPool": "SecurityToolkit",
  "privateNuGet": true,
  "sonar": {
    "url": "http://ci-gep.example.hu:9000",
    "projectKey": "MyApp",
    "dotnetTarget": "src/MyApp.sln"
  }
}
```

> **A `--configure-only` önmagában nem elég egy új projekthez.** Csak a fájlokat írja, a Docker-image
> digesteket és az eszközverziókat nem rögzíti, így a pipeline `Initialize pinned security tools`
> lépése „Pin … first” hibával elbukik. Új projektnél vagy egy új eszköz bekapcsolásakor egyszer
> mindig teljes (`--configure-only` nélküli) telepítés kell.

## 3.5 Sonar coverage

A Sonar alapból csak statikus elemzést tölt fel. A coverage-importtal a Sonar-lépés a build és a
feltöltés között lefuttatja a teszteket, és a lefedettséget is feltölti. Erre a Quality Gate
új-kódos coverage-feltétele miatt van szükség.

1. Az interaktív telepítő a Sonar-kérdések után rákérdez (ld. 3.2), vagy kézzel a
   `.security/security.config.json` `sonar` szakaszában:
   ```json
   "sonar": {
     "url": "http://ci-gep.example.hu:9000",
     "projectKey": "MyApp",
     "dotnetTarget": "src/MyApp.sln",
     "coverage": true,
     "testFilter": "",
     "testEnvironment": {
       "ConnectionStrings__Default": "TestDbConnectionString"
     }
   }
   ```
   - `coverage`: be/ki.
   - `testFilter`: opcionális `dotnet test --filter` kifejezés, pl. `Category!=Slow`.
   - `testEnvironment`: **környezeti változó neve → Azure pipeline-változó NEVE** (nem az értéke!).
     A pipeline a Sonar-lépésnél az `env:` blokkba írja: `ConnectionStrings__Default: $(TestDbConnectionString)`.
     Az értéket (pl. a [2.3](02-sonar-es-mssql.md#23-teszt-sql-server-projektlogin-létrehozása)
     connection stringjét) a pipeline secret változójában kell megadni ([4.2](04-pipeline.md#42-pipeline-változók-és-secretek)).
     A neveknek azonosító formájúnak kell lenniük (betű, szám, `_`), és a `SONAR_TOKEN` nem írható felül.
2. **Teljes** telepítés, mert a `dotnet-coverage` verzióját is rögzíteni kell:
   ```powershell
   security-toolkit install --project-root . --non-interactive
   ```
3. Commitold a `security.config.json`, `security.lock.json` és a generált pipeline-fájl(ok) változását.

Mit futtat:

- **.NET:** `dotnet-coverage collect … dotnet test <dotnetTarget> --no-build [--filter …]`.
- **Angular:** minden `angular.json`-os frontend célban `npm ci` (ha nincs `node_modules`), majd az
  `angular.json` minden `test` targettel rendelkező projektjére külön `ng test`, headless Chrome-mal:
  - `@angular/build:karma` (vagy más `…:karma`) builder: `--code-coverage --karma-config=<generált>`;
  - `@angular/build:unit-test` builder `runner: karma`-val: `--coverage --runner-config=<generált>`;
  - más builder (pl. Vitest, Jest): figyelmeztetéssel kihagyva.
  - A Chrome-ot magától megkeresi. Más helyen lévő böngészőt a `CHROME_BIN` környezeti változóval adhatsz meg.

> **A coverage sosem buktatja el a Sonar-lépést.** Ha egy teszt elbukik, nincs riport, vagy hiányzik
> egy eszköz, a lépés `Completed` marad, és csak egy `Coverage: …` megjegyzés jelzi a gondot az
> eredményben ([5.3](05-kiertekeles.md#coverage-megjegyzések)).

## 3.6 Git hook és VS Code

**Pre-commit hook** (`installHook: true`): minden `git commit` előtt lefut a
`security-toolkit scan --staged`. Ez csak a commitolandó (staged) változásokat vizsgálja Gitleaksszel,
és ha titkot talál, **mindig blokkol**. A hook a PATH-on lévő CLI-t hívja, ezért a gépen kell a CLI
(`install.bat`) és a futó Docker. Ha a repóban már volt pre-commit hook, azt
`pre-commit.security-toolkit-original` néven megtartja és előbb azt futtatja.

A hook a `.git/hooks` alatt van, ezért minden branchen lefut. Ha az aktuális branchen nincs
`.security/security.config.json` (pl. egy régebbi branch vagy a toolkit nélküli forrás-master), a
vizsgálatot egy figyelmeztetéssel kihagyja, és a commit átmegy.

**Husky:** ha a repó hookjait a Husky kezeli (a `core.hooksPath` egy `.husky/_` vagy `.husky`
mappára mutat), a telepítő **nem ír hookot**. A Husky a `.husky/_` mappát minden `npm install`-nál
újragenerálja, a `.husky/pre-commit` pedig commitolt, az egész csapatra érvényes script. Ehelyett a
telepítő kiírja, melyik fájlba kell felvenni ezt a sort:
```sh
security-toolkit scan --project-root "$(git rev-parse --show-toplevel)" --staged
```
Ezt a csapatnak kell eldöntenie és commitolnia. Utána mindenkinél kell a CLI és a Docker, különben
a commit elbukik.

> A hookot ne kerüld meg `--no-verify`-jal. Ha a hook egy valódi titkot talál, azt a commitból kell
> kivenni. Ha téves riasztás, kezeld az [5.5](05-kiertekeles.md#55-triázs-és-téves-riasztások) szerint.

**VS Code extensionök** (`installVSCodeExtensions: true`, ha a `code` a PATH-on van): Sonar esetén
*SonarQube for IDE* (SonarLint), DevSkim esetén a DevSkim extension.

## 3.7 Karbantartás és frissítés

| Parancs | Mire |
|---|---|
| `install --project-root . --check` | Csak jelenti, mely managed fájlok térnek el attól, amit a mostani toolkit generálna. Semmit nem ír. Exit 0 = szinkronban, 1 = eltérés. |
| `install --project-root . --dry-run` | Kiírja a feloldott konfigurációt, semmit nem ír. |
| `install --project-root . --configure-only --non-interactive` | Újragenerálja a fájlokat a mostani configból, provisioning nélkül (pl. egy config-módosítás után, ha nem kapcsoltál be új eszközt). |
| `install --project-root . --non-interactive` | Teljes újratelepítés kérdések nélkül, a meglévő config alapján. |
| `restore --project-root . --manifest .security\backups\<időbélyeg>\manifest.json [--dry-run]` | Egy telepítés visszavonása a mentésből. |

**Új toolkit-verzió bevezetése egy projektben:**

1. A SecurityToolkit repóban `git pull`, majd rendszergazdaként `install.bat --no-infra` (a CI-gépen
   `install.bat`, utána az agent-szolgáltatás újraindítása). Linuxon `sudo bash install.sh --no-infra`.
2. A projektben: `security-toolkit install --project-root . --check`. Ez megmutatja, mi fog változni.
3. `security-toolkit install --project-root . --non-interactive`, majd a változások commitolása.
4. **A kivezetett toolkit-fájlokat a telepítő törli.** Ilyenek a korábbi verziók PowerShell-scriptjei
   (`.security/Initialize-SecurityTools.ps1`, `Restore-SecurityToolkit.ps1`, `Toolkit.Common.ps1`,
   `Invoke-SecurityScan.ps1`): ezeket ma a `security-toolkit provision` és `restore` váltja ki. Csak a
   kézzel nem módosítottakat törli, mentéssel (a `restore` visszahozza őket); a kézzel módosítottakat
   meghagyja, és a `pending-actions.md`-ben jelzi. A törlést is commitold.

> **Owned fájl kézi módosítása.** A `.security/SecurityToolkit.props`-ot, a `gitleaks.toml`-t, a lockot és
> a pipeline-YAML-t a toolkit kezeli, és hash-sel védi. Ha valaki kézzel módosította, a telepítés
> azt az egy fájlt **nem írja felül, csendben a régi állapotában hagyja**, a többit frissíti, és a
> kihagyott fájlt a kézi teendők (`pending-actions.md`) között jelzi. A `--check` ezeket
> `ManualChangesDetected`-ként listázza. Megoldás: vagy vond vissza a kézi módosítást
> (`git checkout -- <fájl>`), és futtasd újra a telepítést, vagy vidd át a változtatást a toolkit
> forrásába, és adj ki új verziót. A legbiztosabb, ha frissítés előtt mindig `--check`-kel kezdesz.
>
> A védelem a gitignore-olt `.security/install-state.json` hash-eire épül. **Egy friss klónban vagy
> új gépen** ez a fájl még nincs meg, ezért a commitolt owned fájlok alapállapotnak számítanak, és a
> telepítés felülírhatja őket. A változás ilyenkor a `git diff`-ben látszik, commit előtt nézd át.

[Következő: 4. Pipeline →](04-pipeline.md)
