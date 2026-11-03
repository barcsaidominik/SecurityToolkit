# SecurityToolkit

Projektfüggetlen biztonsági scanner-csomag .NET és Angular repókhoz, Azure DevOps self-hosted
pipeline-nal, Windows és Linux gépekre. Egy CLI (`security-toolkit`) telepíti a projektekbe,
futtatja a scannereket (.NET analyzerek, NuGet- és npm-audit, Semgrep, Gitleaks, OSV-Scanner,
DevSkim, SonarQube coverage-dzsel), és kezeli a gépi infrát (közös SonarQube, teszt-SQL Server).

## Dokumentáció

**[Telepítési és üzemeltetési útmutató →](docs/README.md)**: nulláról a zöld pipeline-ig, a
CI-gép előkészítésétől a találatok triázsáig.

## Gyorsindítás

Windows, rendszergazdai parancssorból, a repó gyökerében:

```bat
install.bat --no-infra
```

Linux, a repó gyökerében:

```bash
sudo bash install.sh --no-infra
```

Utána új terminálban, a célrepóban:

```
security-toolkit install --project-root .
```

## Build és teszt

```
dotnet build SecurityToolkit.slnx
dotnet test SecurityToolkit.slnx
```
