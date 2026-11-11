# SecurityToolkit

[English](README.md) · **Magyar**

[![CI](https://github.com/barcsaidominik/SecurityToolkit/actions/workflows/ci.yml/badge.svg)](https://github.com/barcsaidominik/SecurityToolkit/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Projektfüggetlen biztonsági scanner-csomag .NET és Angular repókhoz, Windows és Linux gépekre. Egy
CLI (`security-toolkit`) telepíti magát bármelyik repóba, futtatja a scannereket helyben, a
pre-commit hookban és egy generált CI-pipeline-ban, és kezeli a közös gépi infrát (egy
SonarQube-szervert és egy teszt-SQL Servert).

## Mit futtat

| Scanner | Mit talál meg |
|---|---|
| .NET security analyzerek | a CA30xx/CA31xx biztonsági szabályokat (SQL injection, path traversal, XSS, nem biztonságos XML-feldolgozás …) build közben |
| NuGet audit, npm audit | ismert sérülékenységű csomagokat |
| Semgrep CE | C# és JS/TS statikus elemzés, a repóba rögzített registry-szabályokkal |
| Gitleaks | titkokat a munkakönyvtárban, a staged változásokban (pre-commit) és a git history-ban |
| OSV-Scanner | második véleményt a függőségek sérülékenységeiről |
| DevSkim | kiegészítő biztonsági lintet |
| SonarQube | szerveroldali elemzést .NET és Angular (Karma vagy Vitest) teszt-coverage-dzsel |

Minden scanner repónként ki-be kapcsolható. A Docker-image-ek digest szerint, a .NET-eszközök
verzió szerint vannak rögzítve egy commitolt lock-fájlban, így a CI pontosan azt futtatja, amit
átnéztek.

## Hogyan működik

- **Gépi szinten, gépenként egyszer:** az `install.bat` (Windows) vagy az `install.sh` (Linux)
  self-contained programként a rendszer PATH-jára teszi a CLI-t, és Dockerben telepítheti a közös
  SonarQube + PostgreSQL-t és egy teszt-SQL Servert.
- **Projektszinten, repónként egyszer:** a `security-toolkit install` megírja a `.security/`
  mappát (config, lock, szabályok), a managed blokkokat a `.gitignore`, `.semgrepignore`,
  `Directory.Build.props` és `.editorconfig` fájlokban, egy Gitleaks pre-commit hookot, valamint
  egy Azure DevOps pipeline-t és/vagy GitHub Actions workflow-t. Minden módosított fájlról mentés
  készül, és visszaállítható.
- **Minden futáskor:** a `security-toolkit scan` lefuttatja a bekapcsolt scannereket, és
  `status.json`-t meg egy Markdown `summary.md`-t ír. **Gate** módban titokra, Semgrep-hibára és
  high/critical csomagsérülékenységre bukik; commitolt baseline mellett csak az *új* találatokra.

## Kilépési kódok és hibakezelés

| Exit | Jelentés |
|---|---|
| `0` | minden lefutott scanner végigment (Report módban találattal vagy anélkül) |
| `1` | Gate módban (vagy a `--staged` pre-commit scanben) blokkoló találat van |
| `2` | technikai hiba: egy scanner nem tudott elindulni, váratlan kóddal lépett ki, vagy hiányzó, illetve olvashatatlan riportot írt |

Egy scanner hibája sosem jelenik meg tiszta eredményként: a `status.json`-ban `failed` lesz, és a
futás mindkét módban `2`-vel lép ki. A Semgrep csak akkor számít teljesnek, ha minden jelentett
parse-hibája warn szintű részleges feldolgozás; minden más elemzési hiba bukott lépést ad. Gate
módban egy bukó SonarQube Quality Gate a Sonar-lépést buktatja el, ezért `2`-ként jelenik meg, nem
`1`-ként. A kikapcsolt eszközök kimaradnak, nem kerülnek a riportba.

## Hatókör és korlátok

A toolkit meglévő, nyílt forrású scannereket futtat rögzített verzióval, és a kimenetükből egy
riportot és egy gate-döntést készít. Nem penetrációs teszt, nem végez dinamikus (DAST) tesztelést, és
egy tiszta futás nem jelenti azt, hogy a kód sérülékenységmentes:

- Blokkolni csak a Gitleaks, a Semgrep `ERROR` találatai és a High/Critical NuGet/npm advisoryk
  tudnak. A .NET analyzerek, az OSV-Scanner és a DevSkim csak leltárt ad, sosem blokkol.
- A CA30xx taint-szabályok csak az MVC controller-akciók paramétereit látják, a minimal API
  végpontokat nem. A Semgrep CE fájlonként elemez, fájlokon átívelő adatfolyam nélkül.
- A Gitleaks mintaalapú, így egy fel nem ismert formátumú titok átcsúszhat, a függőség-auditok pedig
  csak a publikált advisorykat ismerik.
- A Gitleaks baseline-bejegyzése a szabályra, a fájlra és a redaktált találatra épül, ezért ha
  ugyanabban a fájlban pontosan ugyanarra a kulcsra egy másik titok kerül, az ismertnek számít.

## Gyorsindítás

Előfeltételek: a `global.json`-ban rögzített .NET SDK, Git, Docker (a Semgrephez, a Gitleakshez,
az OSV-Scannerhez és a közös infrához), frontendhez Node.js.

Windows, rendszergazdai parancssorból, a repó gyökerében:

```bat
install.bat --no-infra
```

Linux, a repó gyökerében:

```bash
sudo bash install.sh --no-infra
```

Utána új terminálban, a védendő repóban:

```
security-toolkit install --project-root .
security-toolkit scan --project-root .
```

A `--no-infra` elhagyásával (vagy `--sonar` / `--mssql` kapcsolóval) a közös SonarQube és a
teszt-SQL Server is települ. A `security-toolkit --help` minden parancsot felsorol.

A release-ek (`v*` tagek) self-contained programokat tesznek közzé `win-x64`, `linux-x64` és
`linux-arm64` platformra, `SHA256SUMS` fájllal. Egy célprojekt rögzíthet egyet
(`toolkitRelease` a `.security/security.config.json`-ban), így a pipeline-ja hosted, efemer
agenten is letölti és ellenőrzi a CLI-t.

## Dokumentáció

**[Telepítési és üzemeltetési útmutató →](docs/hu/README.md)**: nulláról a zöld pipeline-ig, a
CI-gép előkészítésétől a közös Sonaron és SQL Serveren, a projekt-telepítésen és a pipeline-on át a
találatok triázsáig, teljes CLI-referenciával és hibaelhárítással.

## Build forrásból

```
dotnet build SecurityToolkit.slnx
dotnet test SecurityToolkit.slnx
```

## Biztonság

A sérülékenységek bejelentésének módja a [SECURITY.md](SECURITY.md)-ben van.

## Licenc

[MIT](LICENSE)
