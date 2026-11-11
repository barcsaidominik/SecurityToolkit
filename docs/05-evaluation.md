# 5. Evaluating the results and triage

**English** · [Magyar](hu/05-kiertekeles.md)

[← 4. Pipeline](04-pipeline.md) · [Contents](README.md) · [Next: 6. Local runs and CLI →](06-local-runs-and-cli.md)

After a run you see the results in these places:

| Where | What it shows | When to look |
|---|---|---|
| **`security-results` artifact → `summary.md`** | every tool in one table, with finding counts | **always start here** |
| **Scans tab** (on the run's page) | the Semgrep and Gitleaks findings, linked to file/line | for code-level findings |
| **Sonar UI** | issues, security hotspots, coverage, Quality Gate | for the Sonar findings and trends |
| **Code Coverage tab** (on the run's page) | the .NET and Angular coverage per file, in HTML | if the coverage import is on ([3.5](03-project-installation.md#35-sonar-coverage)) |

## 5.1 The `security-results` artifact

On the run's page: *Summary* → *Related* → *published* → **security-results** → download. Its layout:

```
security-results/
  <build number>/               ← one run (the shared --run-id)
    summary.md                  ← for humans
    status.json                 ← machine format, same content
    <per-tool reports and logs>
  latest-status.json            ← the status.json of the latest run
  setup-status.json             ← the result of the "Initialize pinned security tools" step
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

- **Status:** `completed` = the tool ran. `failed` = a technical error, the tool could not finish; the
  reason is in the *Message* column. `not-run` = it did not run (e.g. Sonar without `--include-sonar`).
- **Findings:** the number of findings. `completed` + `0` is the clean result.
- **Blocking:** can be `yes` only in Gate mode (see 5.4).

You can show the same table locally, in color:

```powershell
security-toolkit report --project-root .                 # the latest run
security-toolkit report --project-root . --run <run-id>  # a given run
```

To view a downloaded artifact this way, copy the `<build number>` folder into the repository's
`.security/results/` folder and pass it with `--run`.

## 5.2 Per tool: where the details are, what they mean

| Tool | Report in the folder | What it counts | How to read it |
|---|---|---|---|
| **analyzers** | `dotnet-<n>-build.log` | the build's CA30xx/CA31xx, EF1002 and NU1507 security warnings; the *Message* breaks them down by code (e.g. `CA3001: 1, NU1507: 14`) | look for the `warning CA3`, `warning EF1002`, `warning NU1507` lines; CA rule descriptions: `https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca<number>`. The remedy for NU1507 is `packageSourceMapping` in `nuget.config`. |
| **nuget** | `dotnet-<n>-nuget.json` | NuGet packages with known vulnerabilities, transitive ones included | package, version, severity, advisory link |
| **npm** | `npm-<n>.json` | the findings of `npm audit` | `vulnerabilities` per package, with severity; `fixAvailable` shows the fix |
| **semgrep** | `semgrep.json`, `semgrep.sarif`, `semgrep-coverage.json` | Semgrep rule hits | the Scans tab, or `semgrep.json` → `results[]`: `check_id`, `path`, `start.line`, `extra.severity` |
| **gitleaks** | `gitleaks.json`, `gitleaks.sarif` | possible secrets (password, key, token, connection string) | the Scans tab, or `gitleaks.json`: `RuleID`, `File`, `StartLine`, `Fingerprint`. The secret's value is **always redacted**. |
| **osv** | `osv.json` | dependency vulnerabilities from the OSV database (supplementary) | `results[].packages[].vulnerabilities[]` |
| **devskim** | `devskim.sarif` | security lint rules (supplementary) | with a SARIF viewer, or on the Scans tab |
| **sonar** | `sonar-issues.json`, `sonar-begin/build/end.log`, `sonar-test-*.log`, `dotnet-coverage.xml`, `frontend-coverage-<n>/` | the Sonar project's open issues of type **Vulnerability** | the details are in the Sonar UI (5.3) |

> For Semgrep, `ERROR` is the **rule's** severity, not a general "critical" label. An `ERROR` can be a
> false positive, and a `WARNING` can be a real bug.

> Semgrep CE's parser does not know some (valid) constructs of modern C#, e.g. a trailing comma in a
> `with { … }` initializer. There are two cases:
>
> - **`Partially parsed: …`** in the message of a `completed` row: Semgrep skipped only that construct
>   and analyzed the rest of the file. The run counts as complete, the step is green; `semgrep-coverage.json`
>   and the "Semgrep coverage" part of `summary.md` name the files.
> - **`failed`** (`Incomplete analysis: …`): at least one file could not be parsed at all, or another
>   error happened (timeout, rule error). The findings are kept, but the list is incomplete; if Sonar runs
>   too, the `frontend` scope helps ([3.2](03-project-installation.md#further-questions)).

## 5.3 Sonar UI

The project page: `http://<sonar>:9000/dashboard?id=<projectKey>`.

- **Issues** → *Type: Vulnerability*: this is the number `summary.md` shows. `summary.md` also lists the
  10 most severe with rule and file:line (*Top … Sonar vulnerabilities*), so triage can start there.
  Here you find the code snippet, the rule's explanation and the suggested fix.
- **Security Hotspots:** code locations that need a human decision (e.g. "this regex may be prone to
  ReDoS"). The toolkit does **not** count these as findings; review them one by one in the Sonar UI
  (*Review*: *Safe*, *Fixed* or *Acknowledged*).
- **Measures** → *Coverage*: the result of the coverage import (.NET and frontend together).
- **Quality Gate:** at the top of the dashboard; *Passed* or *Failed*, with the failing conditions.

### Coverage notes

With the coverage import on, the *Message* column of the Sonar row contains a `Coverage: …` part:

| Note | Meaning | What to do |
|---|---|---|
| `.NET report imported` | the .NET coverage was uploaded | – |
| `.NET report imported, but tests failed (exit 1; see sonar-test-dotnet.log)` | there is coverage, but some tests failed, so the coverage is lower than the real one | look for the `Failed` lines in `sonar-test-dotnet.log`; fixing the tests is the project team's job |
| `<target>:<project> ok` | the Angular project's lcov was uploaded | – |
| `<target>:<project> no report (exit 1; see sonar-test-frontend-<n>.log)` | the Angular tests did not run (often a compile error) | the named log |
| `<target>:<project> no report (…)` for a Vitest project | often the coverage provider is missing (in the log: `Code coverage requires either "@vitest/coverage-v8" …`) | `npm i -D @vitest/coverage-v8` in the project ([3.5](03-project-installation.md#35-sonar-coverage)) |
| `<target>:<project> skipped (unsupported test builder …)` | the project is neither Karma- nor Vitest-based (e.g. Jest) | not supported for now |
| `<target>:<project> skipped (project name is not a valid coverage path)` | the `angular.json` project name would point outside the `coverage/` folder | rename the project |
| `<target> skipped (not an Angular workspace)` | the frontend target has no `angular.json` | only Angular frontend coverage is supported |
| `.NET skipped (dotnet-coverage is not provisioned; rerun install)` | the tool is not pinned in the lock | a full `install` ([3.5](03-project-installation.md#35-sonar-coverage)) |
| `.NET failed (…)` / `<target>:<project> failed (…)` | the test run could not even start | the error message in parentheses |

## 5.4 Report or Gate mode

| | Report (default) | Gate |
|---|---|---|
| On findings | exit 0, the step is green | **exit 1** on a blocking finding |
| Technical error (e.g. missing config, a failed tool) | exit 2 | exit 2 |
| Sonar | uploads only | waits for the Quality Gate; if it fails, the step fails too |
| Where to use it | CI pipeline (the generated pipeline uses this by default), regular reports | local checks before a release, the generated pipeline with `pipelineMode: "Gate"`, custom pipelines |

**What blocks in Gate mode** (an absolute threshold without a baseline, see below):

| Tool | Blocks if |
|---|---|
| gitleaks | any finding |
| semgrep | at least one finding of `ERROR` severity |
| nuget | at least one High or Critical |
| npm | at least one High or Critical |
| analyzers, osv, devskim | never (inventory only) |
| sonar | a failing Quality Gate (the step fails) |

**`--staged`** (the pre-commit hook) always blocks when it finds a secret, regardless of the mode.

### Baseline: block only on new findings

In an older project, Gate mode stays red until the existing debt is gone. To be able to turn it on now,
you can record the existing blocking findings as accepted in **`.security/baseline.json`**. If this file
exists, Gate mode (and the pre-commit hook) **blocks only on findings not in the baseline**. The report
still lists every finding, and the `Message` column says how many were known and how many new.

```powershell
security-toolkit scan --project-root . --update-baseline --history
```

- It runs in Report mode and writes the **current blocking findings** of the tools that ran into the
  baseline, overwriting per tool. With `--tool` you can update a single tool's entry; the others stay.
- A tool that failed during the run and could not list its findings is not updated; its previous entry
  stays.
- **Record it with the same switches the Gate runs with.** The pipeline runs Gitleaks with `--history`
  (`pipelineHistory`), so record the baseline that way too. Otherwise every secret found in the history
  counts as new.
- **Commit** `baseline.json`: the team and the pipeline then see the same thing, and a new acceptance is
  a reviewable diff. The file contains only hashes, neither secrets nor source code.
- Update it only deliberately. Before accepting a new finding, decide as described in
  [5.5](#55-triage-and-false-positives) whether it should be fixed or narrowly suppressed instead.

What counts as the same finding (the fingerprint):

| Tool | Fingerprint | Consequence |
|---|---|---|
| semgrep | rule + file + the affected code lines (without whitespace) | moving the code within the file does not make a known finding new; changing the line does |
| gitleaks | rule + file + commit (only with `--history`) + Gitleaks' redacted match (e.g. `"ApiKey": "REDACTED"`) | the secret's value is not part of it, so if a different secret ends up under **the same key** in the same file, it counts as known |
| nuget | target + package + advisory | a version update still affected by the same advisory stays known; a new advisory is new |
| npm | target + package + advisory | as for NuGet |

A project without a baseline behaves as before. Analyzers, osv and devskim still never block. Sonar uses
its own Quality Gate, which looks at new code anyway in the *Sonar way* profile. An invalid or unreadable
`baseline.json` is a technical error (exit 2), not a block.

## 5.5 Triage and false positives

Every finding allows three decisions: **fix** it, **accept it deliberately** (with a justification), or
**suppress it as a false positive**. Always suppress as narrowly as possible (one line, one rule), and
always write the justification next to it.

| Tool | Fix | Suppression (for a false positive) |
|---|---|---|
| **gitleaks** | remove the secret from the code, **rotate** it (anything that once got into the repository counts as compromised), and use user-secrets / Key Vault / a pipeline secret instead | inline: a `gitleaks:allow` comment at the end of the line. Or: the finding's `Fingerprint` into a `.gitleaksignore` file in the repository root (one per line). Do **not** edit `.security/gitleaks.toml`, the toolkit manages it. |
| **semgrep** | fix the code as the rule recommends | inline: `// nosemgrep: <check_id>`. A whole file or folder: into `.semgrepignore`, **outside the managed block**. |
| **nuget** | update the package to the fixed version (for a transitive one, update the direct dependency or add an explicit `PackageReference`) | if there is no fix and the vulnerability does not affect you: `<ItemGroup><NuGetAuditSuppress Include="<advisory URL>" /></ItemGroup>` in `Directory.Build.props` |
| **npm** | `npm audit fix`, or update the dependency; for a transitive one, `overrides` in `package.json` | no built-in suppression; document the decision |
| **analyzers** | fix the code | `#pragma warning disable CA3xxx // justification` around the smallest possible block, or `[SuppressMessage(..., Justification = "...")]` |
| **sonar** | fix the code | in the Sonar UI, on the issue: *Accept* (deliberate acceptance) or *False positive*, always with a comment. Only the project admin can do this. |
| **osv / devskim** | like nuget/npm, or analyzers respectively | supplementary tools, their triage is optional |

**Suggested order after the first run of a new project:**

1. **Gitleaks:** review every finding right away. A real secret has to be rotated; deleting it is not
   enough, because it stays in the git history.
2. **NuGet/npm High/Critical:** package updates.
3. **Sonar Vulnerabilities and Security Hotspots:** in the Sonar UI.
4. **Semgrep `ERROR`:** on the Scans tab.
5. After that the goal is a stable baseline: after every run you only need to look at the **new**
   findings (the change in the `summary.md` numbers, and Sonar's *New Code* view).

[Next: 6. Local runs and CLI →](06-local-runs-and-cli.md)
