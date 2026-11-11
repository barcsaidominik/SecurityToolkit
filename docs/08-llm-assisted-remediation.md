# 8. LLM-assisted evaluation and remediation

**English** · [Magyar](hu/08-llm-tamogatott-javitas.md)

[← 7. Troubleshooting](07-troubleshooting.md) · [Contents](README.md)

This chapter describes how a developer and a coding LLM agent work through **together** all open scanner
and Sonar findings of a project: querying, merging into one list, triage, fixing in small batches,
verification. It is project-independent: it applies to any target project that has the toolkit installed
and in which the pipeline has already run.

The chapter has two readers:

- **Developer:** what to prepare, where to decide, what to review.
- **Agent:** what it gets as input, what it has to produce, what it may and may not do. The agent
  receives this file as its instructions (see [8.9 Starting prompt](#89-starting-prompt-template)), and
  the [8.8 Agent contract](#88-agent-contract) section is binding on it.

> **Principle:** the agent collects, organizes, proposes and fixes; **the decision is the developer's.**
> The agent never decides on its own that a finding is a false positive, and never fixes in bulk, in one
> big diff. Most of the work is deciding what *not* to fix.

## 8.1 Overview

```
 0. Preparation        developer   clean baseline, access
 1. Data collection    agent       Sonar API + results/<run-id>/ (or a local re-run)
 2. Normalization      agent       findings.json + triage.md (per rule group)
 3. Triage             developer   ◄── DECISION POINT: fix / suppress / accept / later
 4. Fixing             agent       one branch, one PR per group
 5. Verification       agent       build, tests, targeted re-scan, comparison with the baseline
 6. Review + merge     developer   ◄── DECISION POINT
 7. Closing            both        Sonar states, work items, new baseline
```

Steps 3 and 6 are human decisions. At those, the agent **stops and waits**.

## 8.2 Prerequisites

### In the project (provided by the developer)

| # | Prerequisite | Why | Check |
|---|---|---|---|
| 1 | The toolkit is installed, `.security/` committed ([chapter 3](03-project-installation.md)) | the agent reads the config from it and re-runs with it | `security-toolkit install --project-root . --check` → exit 0 |
| 2 | At least one full pipeline run with Sonar ([chapter 4](04-pipeline.md)) | this is the starting state (baseline) | every tool is `completed` in `summary.md` |
| 3 | **The tests are green** on the main branch | verifying the fixes relies on the tests; regressions cannot be measured from a red start | `dotnet test` / `ng test` locally |
| 4 | **Formatting/lint has been run once and introduced in a separate commit** | otherwise every fixing diff gets formatting noise, which makes it unreviewable | `git log` shows the formatting commit; a re-run changes no file |
| 5 | A clean working tree on the fresh state of the main branch | the per-group branches start from it | `git status` is empty |
| 6 | The project builds and tests locally (with the test SQL connection string in the environment, if needed) | the agent verifies locally | [6.0](06-local-runs-and-cli.md#60-preparing-a-freshly-cloned-repository) |

### Access

| What | Needed? | Note |
|---|---|---|
| **Your own Sonar user token** (`SONAR_TOKEN` env var, per session) | required | the same token you use locally for every project ([2.2](02-sonar-and-mssql.md#personal-user-token-for-local-runs)); **not** the pipeline's project analysis token. *Browse* is enough for queries; a local Sonar re-run also needs *Execute Analysis*. |
| Sonar *Administer Issues* / *Administer Security Hotspots* | only for step 7 | to record the suppressions on the Sonar side; the developer can also do this in the UI |
| Azure DevOps access (PAT or MCP) | optional | to download the pipeline artifact and for the work items. A local re-run can replace it. |
| Docker Desktop running | for the local re-scan | Semgrep/Gitleaks/OSV run in containers |

> Pass the token in an **environment variable**, not in the prompt and not in a file. The agent does not
> print it, does not log it, and does not put it into any generated file.

> **The agent sees its own process environment, not your terminal's.** It inherits `SONAR_TOKEN` from the
> session it was started from, even an old, since invalidated token. If you set or changed the token
> *after* starting the agent, restart the agent from that terminal. The agent checks this before step 1
> (see below).

**Check** (the agent runs it before step 1, without printing the token): whether the token is valid on the
**effective** Sonar URL (see [8.4](#84-step-1--data-collection-agent)), and what type it is:

```powershell
$t = $env:SONAR_TOKEN; $t.Substring(0,4)    # squ_ = user token (correct), sqp_ = project analysis token (wrong locally)
$h = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${t}:")) }
(Invoke-RestMethod "<effective sonar.url>/api/authentication/validate" -Headers $h).valid   # must be True
```

On `False` the agent stops and reports on which URL and with what type of token it failed.

### What the developer decides up front

- **Scope:** which tools and severities belong to this round (e.g. "every security finding + Sonar Bug,
  no code smells"). Default: every security finding, without code smells.
- **Branch and PR convention** (e.g. `fix/security-<rule>`, target branch, required reviewer).
- **Where the work items go** (if any): under which Feature/User Story the tasks belong.

## 8.3 Step 0 – Preparation (developer)

1. Meet the prerequisites of 8.2.
2. Note the **baseline**: the run ID (build number) of the pipeline run and the commit SHA of the main
   branch.
3. Start the agent with the prompt of [8.9](#89-starting-prompt-template), with the scope filled in.

## 8.4 Step 1 – Data collection (agent)

The working folder: **`.security/results/remediation-<YYYY-MM-DD>/`**. The toolkit gitignores
`.security/results/`, so nothing gets committed by accident.

### Scanner results

Two equivalent sources, in this order of preference:

1. **A local re-run** on the main branch's baseline commit:
   ```powershell
   security-toolkit scan --project-root . --include-sonar --run-id remediation-baseline
   ```
   Advantage: the same command verifies the fix later (step 5). If the project does not build locally or
   the Sonar run takes too long, Sonar can be skipped (without `--include-sonar`); its data comes from the
   API anyway.
2. **The pipeline's `security-results` artifact** ([5.1](05-evaluation.md#51-the-security-results-artifact)),
   with the `<build number>/` folder copied into the working folder.

The files to read per tool: [the 5.2 table](05-evaluation.md#52-per-tool-where-the-details-are-what-they-mean).
`status.json` comes first (which tool ran; a `failed` tool's data must not be treated as complete), then
the raw reports (`semgrep.json`, `gitleaks.json`, `dotnet-*-nuget.json`, `npm-*.json`, `osv.json`,
`devskim.sarif`, `dotnet-*-build.log`).

### Sonar Web API

Authentication: HTTP Basic, user name = token, empty password (the toolkit calls it the same way).

Take the Sonar URL and the project key from the **effective config**, exactly as `scan` does:
`.security/security.config.local.json` (if it exists) is deep-merged over `.security/security.config.json`.
On a developer machine the local file typically points to a **local** Sonar (e.g.
`http://127.0.0.1:9000`); then the local scan uploaded there, and the data has to be fetched from there
too, not from the committed (shared/CI) URL. The two Sonars can differ (a different run, a different
triage state), so the collection report must always say which URL the data came from.

| Data | Endpoint | Important parameters |
|---|---|---|
| Open issues | `GET /api/issues/search` | `componentKeys=<projectKey>` (`components` in newer versions), `resolved=false`, `ps=500`, `p=1..n`, optionally `types=VULNERABILITY,BUG` |
| Security hotspots | `GET /api/hotspots/search` | `project=<projectKey>`, `status=TO_REVIEW`, `ps=500`, `p` |
| Rule description | `GET /api/rules/show` | `key=<rule>`; once per group, for the triage |
| Quality Gate status | `GET /api/qualitygates/project_status` | `projectKey=<projectKey>` |
| Coverage | `GET /api/measures/component` | `component=<projectKey>`, `metricKeys=coverage,new_coverage` |

- **Paging limit:** Sonar returns at most 10,000 results for one query (`p*ps ≤ 10 000`). If `total` is
  larger, slice the query (e.g. by `types`, `severities` or `createdAfter`/`createdBefore`).
- Hotspots are **not** issues; they have their own endpoint and always need a human decision.
- Save the raw responses into the working folder (`sonar-issues-all.json`, `sonar-hotspots.json`), so the
  normalization can be repeated without new API calls.

**Output:** the raw data in the working folder, and a short report for the developer: how much data came
from which source, and which tool is missing or `failed`.

## 8.5 Step 2 – Normalization (agent)

### A single finding format: `findings.json`

Every source produces the same record:

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
  "url": "http://<sonar>/project/issues?id=...&issues=... (if any)",
  "duplicates": ["devskim:DS104456:src/Api/Repo.cs:42"]
}
```

- **`file`** is always relative to the repository root, with `/` separators.
- **`severity`** is a single scale: `critical | high | medium | low | info`. The tool's own value stays in
  `toolSeverity`. (Semgrep `ERROR` → `high`, `WARNING` → `medium`; Sonar `BLOCKER/CRITICAL` →
  `critical/high`; NuGet/npm on their own scale; Gitleaks always `critical`.)
- **For dependency findings** `file` is the project file (`.csproj` / `package.json`), `rule` is the
  advisory ID (GHSA/CVE), and `message` contains the package, the version and the fixed version.
- **Gitleaks:** the secret's value is redacted in the report; the agent does **not** try to read it from
  the file and does not write it down.

### Deduplication

Several tools can report the same issue (e.g. Semgrep + DevSkim + Sonar on the same line; NuGet + OSV on
the same package). Merge rule: same `file` + `line` (±2 lines) + same `category`, or for dependencies the
same package + advisory. The remaining record belongs to the most reliable tool (order: Sonar > Semgrep >
analyzers > DevSkim; NuGet/npm > OSV); the others go into the `duplicates` list.

### Grouping: `triage.md`

Group **by rule** (not by file): a rule's fix pattern is the same everywhere, so one group = one
decision = one PR. The structure of `triage.md`:

```markdown
# Triage – <project> – baseline <run-id> @ <commit>

Total: 312 findings (241 after deduplication), 23 rule groups, + 14 security hotspots.

| # | Group (tool:rule) | Count | Severity | Affected | Agent's proposal | Reasoning | Estimated size | DECISION |
|---|---|---|---|---|---|---|---|---|
| G01 | gitleaks:generic-api-key | 1 | critical | appsettings.json | ROTATE + fix | looks like a real key | S | |
| G02 | nuget:GHSA-xxxx (System.Text.Json 8.0.0) | 3 | high | 3 projects | fix (update to 8.0.5) | patch version, no breaking change | S | |
| G03 | sonar:csharpsquid:S2077 | 4 | high | src/Api/... | review | 2 look real, 2 are built from constant strings | M | |
| ... |
```

The values of the **Agent's proposal** column: `fix`, `false positive`, `accept`, `later`, `review` (if
the code does not settle it clearly), `ROTATE + fix` (for secrets). The reasoning has to be concrete (what
it looked at in the code), not a repetition of the rule description.

Hotspots go into a separate table, the same way, one by one (not grouped).

At the end of `triage.md`: the list of missing or `failed` tools, and which data is **not** reliable
because of them.

**The agent stops here** and hands `triage.md` over to the developer.

## 8.6 Step 3 – Triage (developer)

The developer fills in the **DECISION** column for every group:

| Decision | Meaning | Who carries it out |
|---|---|---|
| `fix` | a real problem, has to be fixed | agent (step 4) |
| `false` | a false positive; the narrowest suppression + justification | the agent does the suppression in the code, the Sonar state in step 7 |
| `accept` | real, but a deliberately accepted risk | documentation only (Sonar *Accept* with a comment) |
| `later` | real, but not in this round | a work item, the code does not change |
| `own` | the developer fixes it personally (e.g. it touches business logic) | developer |

A group can have a mixed decision too (e.g. `G03: fix, except Repo.cs:88 → false`).

### Suggested order

1. **Secrets (Gitleaks).** This is **not** a code fix: operations **must rotate** the secret, because it
   stays in the git history. Taking it out of the code and putting it behind user-secrets / Key Vault / a
   pipeline secret is only the second step. The developer organizes the rotation, not the agent.
2. **High/Critical dependencies** (NuGet, npm). Mechanical, but a major version jump can bring breaking
   changes: then `own` or `review`.
3. **Sonar Vulnerability, Semgrep ERROR, analyzer CA3xxx**, and the hotspots.
4. **Sonar Bug**, then the rest (if in scope).

Formatting and lint rules are **not** part of this round: they are a separate process (8.2, point 4).

When done, the developer hands `triage.md` back to the agent, saying **which groups to start with**
(suggested: 1–3 groups at a time).

## 8.7 Steps 4–5 – Fixing and verification (agent)

Per group (or per group merged by the developer):

1. **A branch** from the fresh state of the main branch, following the given convention (e.g.
   `fix/security-G02-system-text-json`).
2. **Understanding before fixing.** Read the surrounding code at every affected location. If the fix
   would change behavior (e.g. rewriting SQL building to parameterized), and the location is not covered
   by a test, **first write a characterization test** that pins the current correct behavior.
3. **Fix** as the rule recommends and following the project's existing patterns. A minimal diff: only the
   change the finding needs, without incidental refactoring or formatting.
4. **Suppression** (only for a `false` decision) in the narrowest form, always with a justification, as in
   [the 5.5 table](05-evaluation.md#55-triage-and-false-positives). No suppression may go into a
   toolkit-managed file (`.security/gitleaks.toml`, managed blocks).
5. **Verification**, in this order:
   - the build of the affected project(s), without more warnings;
   - the affected tests, then the whole test suite if the change touches shared code;
   - **a targeted re-scan** with the affected tool:
     ```powershell
     security-toolkit scan --project-root . --tool <tool> --run-id remediation-G02
     ```
     (For a Sonar rule the Sonar run is slow: it is enough if the pipeline runs it on the PR; then the PR
     description has to say that the Sonar check waits for the pipeline.)
   - **comparison with the baseline:** the group's findings are gone, and there is **no new finding** in
     the same tool. The result (count before/after) goes into the PR description.
6. **Commit** one per group (or a few per logical step), following the project's commit convention, with
   the group's ID.
7. **PR** (or a push, if the developer asks for that) with this description:

```markdown
## What it fixes
G02 – nuget:GHSA-xxxx – System.Text.Json 8.0.0 → 8.0.5 (3 projects)

## How
<1–3 sentences about the fix pattern>

## Verification
- build: OK (warnings: 12 → 12)
- tests: 214/214 green
- `scan --tool nuget`: 3 → 0 findings, no new finding

## Suppressions (if any)
- <file:line> – <rule> – <justification>

## Open question / risk
- <if any>
```

If it turns out during a fix that the triage decision was wrong (e.g. the "false positive" is real after
all), or the fix is bigger than what the decision covered, the agent **stops** and turns to the developer.
It does not silently reverse the decision.

## 8.8 Agent contract

This section is binding on the agent.

### MUST

- At the start of every step, check the prerequisites of 8.2, and if one is not met, say so before going
  on (e.g. red tests on the main branch → stop).
- Take the Sonar URL and project key from the effective config (`security.config.json` +
  `security.config.local.json`), never from the committed file alone; and validate the token on that URL
  (8.2) before querying anything.
- If authentication fails, first check its own assumptions (which URL, which token, where it was
  inherited from), and only then blame the developer's environment or ask them to change it.
- Stop after step 2 and hand over `triage.md`; fix only after the DECISION column is filled in.
- Work only on the groups the developer picked, each on its own branch.
- Verify every fix with a build, the tests and a targeted re-scan, and report the result in numbers. If
  a check did not run, say so instead of keeping quiet.
- Write a justification next to every suppression, with the narrowest scope.
- Mark uncertainty (`review`) instead of hiding it behind a confident proposal.

### MUST NOT

- Print, log, or put into a file or commit any secret (token, password, connection string), or read the
  value of a redacted Gitleaks finding from the source and repeat it.
- Decide alone that something is a false positive, and suppress it.
- Change the state of a Sonar issue or hotspot (`do_transition`, `change_status`) without the developer's
  explicit approval.
- Turn a rule off globally, or change the Sonar Quality Profile or Quality Gate.
- Edit a toolkit-managed file (the contents of `.security/`, managed blocks, the generated pipeline YAML).
- Mix a fix with formatting, renaming or incidental refactoring.
- Put several unrelated rule groups into one PR (unless the developer merged them).
- Rewrite the git history (force push, rebase on a shared branch) to remove secrets. That is an
  organizational decision.
- Delete, disable or weaken a test to make the fix "green".
- Merge.

### Expected outputs

| Step | File / artifact | Where |
|---|---|---|
| 1 | raw data + a short collection report | working folder |
| 2 | `findings.json`, `triage.md` | working folder |
| 4–5 | branch + commit(s) + PR description per group | repository |
| 7 | the closing summary of 8.10 | in the answer / work item |

## 8.9 Starting prompt (template)

The developer gives this to the agent (with the `<…>` parts filled in):

```text
Work according to docs/08-llm-assisted-remediation.md; the 8.8 Agent contract is binding.

Project: <repository path>
Baseline: pipeline run <build number>, commit <sha>   (or: run a local baseline)
Scope: <e.g. every security finding + Sonar Bug; no code smells>
Sonar: my own user token is in the SONAR_TOKEN env var; URL and project key from the effective config
(security.config.json + security.config.local.json). Validate the token on that URL before step 1.
Branch convention: <e.g. fix/security-<group>>, target branch: <main>
PR: <create it | push only | commit locally only>
Work item: <if any: parent ID>

Now: steps 1–2 (data collection, normalization), then stop with triage.md.
```

After the triage:

```text
The DECISION column of triage.md is filled in. Start with these: <G02, G05>. A separate branch and PR per group.
```

## 8.10 Step 7 – Closing (developer, prepared by the agent)

1. **Sonar states:** close the Sonar issues decided `false` and `accept` in the UI (or with the
   developer's approval through the API: `POST /api/issues/do_transition`, `transition=falsepositive` or
   `accept` – `wontfix` on older versions), with a comment; the hotspots with
   `POST /api/hotspots/change_status` (`status=REVIEWED`, `resolution=SAFE|FIXED|ACKNOWLEDGED`). This needs
   *Administer Issues* / *Administer Security Hotspots*. The agent provides a ready list for it (issue key,
   decision, justification).
2. **Work items:** **one** work item per rule group from the `later` groups, not per finding. The
   description gets the Sonar link and the `triage.md` row.
3. **New baseline:** a full pipeline run after the merges; the agent summarizes the original and the new
   numbers per tool (before → after; of which fixed / suppressed / accepted / later).
4. **Prevention:** once the old debt is settled, it is worth setting the Sonar Quality Gate to conditions
   on *new code*, and from then on triaging only the new findings
   ([5.5](05-evaluation.md#55-triage-and-false-positives), point 5).

## 8.11 Known limitations

- **Gate mode without a baseline is an absolute threshold** ([5.4](05-evaluation.md#54-report-or-gate-mode)):
  a fixing PR's pipeline can stay red in Gate mode until the old debt is gone. `.security/baseline.json`
  solves this: the existing findings are accepted, and only new ones block. The generated pipeline runs in
  Report mode by default.
- **The Sonar check is slow** locally (a full build + analysis), so fixes for Sonar rules are often proven
  only by the PR's pipeline.
- **Transitive dependencies:** the fix is often an update of a direct dependency or an explicit
  `PackageReference`/`overrides`; the agent has to show which direct dependency pulls it in.
- A **Semgrep `failed`** state means the list is incomplete (a file was left out, or another error
  happened); and in the `Partially parsed` files next to a `completed` state, one construct each was not
  analyzed. For those files the triage does not give a complete picture
  ([5.2](05-evaluation.md#52-per-tool-where-the-details-are-what-they-mean)).
- The LLM **makes mistakes**: its proposals (especially `false positive`) have to be checked, and its
  fixes need the same code review as any other change.

[← Contents](README.md)
