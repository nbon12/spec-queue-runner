# Contract: The build-and-test check

**Feature**: 001-spec-queue-runner | **Consumers**: GitHub (running it), branch protection
(requiring it), the operator (reading it), the runner (blocked by it)

The runner's `verify` command and this check run the same command. That is deliberate: two gates
that run *different* commands produce disagreements nobody can adjudicate. When these two disagree,
the only possible cause is the environment — which is diagnosable.

## Identity

| Field | Value | Why it is a contract |
|---|---|---|
| File | `.github/workflows/build-and-test.yml` | The path GitHub reads workflows from |
| Workflow name | `build and test` | Shown in the Actions tab |
| Job id / check name | `build-and-test` | **Branch protection references this exact string.** Renaming the job silently un-requires the check, leaving protection enabled and enforcing nothing |

Renaming the job is a breaking change to branch protection, not a cosmetic edit.

## Triggers

```yaml
on:
  pull_request:
    types: [opened, synchronize, reopened]
  push:
    branches: [master]
```

- **`pull_request`** is the gate. `synchronize` is what re-runs the check when the runner pushes
  another commit to an open item's branch.
- **`push` to the base branch** gives `master` a standing health signal, so a broken base is
  distinguishable from a broken change.

## Permissions and concurrency

```yaml
permissions:
  contents: read

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
```

`contents: read` and nothing more. The check reads code and reports pass/fail; it writes nothing,
and it references no secret — so a pull request from a fork gains no capability by triggering it.

`cancel-in-progress` matters more here than in a human repo: the runner pushes to `work/NN`
branches repeatedly within a single item, and paying for superseded runs is pure waste.

## What it runs

| Step | Command | Note |
|---|---|---|
| Restore | `dotnet restore SpecRunner.slnx` | |
| Build | `dotnet build SpecRunner.slnx -c Debug --no-restore` | `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended` make this the analyzer gate for `src/` (§8) |
| Test | `dotnet test SpecRunner.slnx -c Debug --no-build` | Tiers 1–3 in full |

Environment: `ubuntu-latest`, `actions/setup-dotnet` pinned to `10.0.x`, `timeout-minutes: 15`,
NuGet packages cached by `actions/cache` on the hash of every `*.csproj` plus
`Directory.Build.props`.

**Credit-free and offline by construction, not by policy.** The suite substitutes an in-memory
GitHub client and a `RecordingProcessRunner`; no real `git`, `tmux`, or `claude` process is spawned
and no test reaches the network. Tier 4 probes need a phone and a live Claude session and are
manual by nature — they are not in CI and must never be added to it.

**Architecture**: x64, while the product ships `linux-arm64`. The suite never publishes, never
P/Invokes, and never shells out, so it is RID-agnostic; the arm64 build is proven by
`docker build --platform linux/arm64` in the deploy path instead.

## Result semantics

| Outcome | Meaning |
|---|---|
| Success | The solution builds with analyzers clean and Tiers 1–3 pass |
| Failure | Either the build or a test failed — the change is not mergeable on its merits |
| Cancelled | Superseded by a newer push to the same ref; carries no verdict |

A cancelled run is **not** a pass. Branch protection treats it as unmet, which is correct.

## Operator responsibilities (the runner cannot do these)

Both sit outside the token ceiling §6 defines, and neither is runner behaviour:

1. **Landing this file.** A push touching `.github/workflows/` requires the Workflows permission,
   which the runner's PAT must never hold. The operator commits it by hand.
2. **Requiring the check.** Branch protection is an administration setting. Require
   `build-and-test` on the base branch only **after** the runner's merge stage is deployed —
   requiring it earlier makes every runner merge fail 405 and leaves items silently stalled (R18).

## What the runner does with it

The runner never reads this check directly — doing so would need the Checks permission, outside the
ceiling. It reads the pull request's own `mergeable_state`, which branch protection already folds
this check's result into, and declines to merge unless the base branch says the merge may proceed.

See [issue-conventions.md](./issue-conventions.md) for the `stage/merge` label and the
`kind=merge-blocked` comment this produces.
