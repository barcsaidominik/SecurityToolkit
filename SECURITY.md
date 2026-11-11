# Security policy

**English** · [Magyar](#biztonsági-szabályzat)

## Reporting a vulnerability

Please do not report security vulnerabilities in public issues. Use GitHub's private
vulnerability reporting instead: **Security → Report a vulnerability** on this repository.

Include what you found, how to reproduce it, and which version (release tag or commit) is
affected. You will get an answer within a few days; a confirmed issue is fixed in a new release,
and the advisory is published once the fix is out.

## Scope

In scope: the `security-toolkit` CLI, the files it writes into target projects (`.security/`,
managed blocks, generated pipelines), the machine-level infra templates, and `install.bat` /
`install.sh`.

Out of scope: vulnerabilities in the third-party scanners themselves (Semgrep, Gitleaks,
OSV-Scanner, DevSkim, SonarQube); report those to their maintainers.

## Supported versions

Only the latest release receives fixes.

---

# Biztonsági szabályzat

[English](#security-policy) · **Magyar**

## Sérülékenység bejelentése

Biztonsági hibát ne nyilvános issue-ban jelezz, hanem a GitHub privát bejelentési felületén:
a repó **Security → Report a vulnerability** menüpontjában.

Írd le, mit találtál, hogyan reprodukálható, és melyik verziót (release-taget vagy commitot)
érinti. Pár napon belül válaszolok; a megerősített hibát új release javítja, a leírás (advisory)
a javítás megjelenése után lesz nyilvános.

## Hatókör

Ide tartozik: a `security-toolkit` CLI, a célprojektekbe írt fájlok (`.security/`, managed
blokkok, generált pipeline-ok), a gépi infra sablonjai, valamint az `install.bat` és az
`install.sh`.

Nem ide tartozik: maguknak a külső scannereknek (Semgrep, Gitleaks, OSV-Scanner, DevSkim,
SonarQube) a hibái; azokat a saját fejlesztőiknek jelezd.

## Támogatott verziók

Javítást csak a legutolsó release kap.
