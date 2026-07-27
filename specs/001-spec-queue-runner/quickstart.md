# Quickstart & Validation Guide: Spec Queue Runner

**Feature**: 001-spec-queue-runner | **Date**: 2026-07-25

How to build, configure, and prove the runner works. Implementation details live in `tasks.md`;
this is the run-and-validate guide.

## Prerequisites

The **host** needs only Docker (verified `27.3.1` present, 2026-07-25). Everything else — .NET
10, git, tmux, Claude Code — lives in the image, so there are no host toolchain installs
(research R15).

```bash
docker --version            # any recent Docker; Desktop on macOS is fine
docker build -t spec-runner .   # builds the runner image (bundles the .NET runtime + tooling)
```

## One-time setup

### 1. Provide the PAT as a mounted secret

Create a **fine-grained** PAT scoped to exactly one repository, with Issues, Contents, and
Pull requests permissions only (constitution §6, FR-052). Deliver it to the container as a
mounted secret file (or a Docker secret) — never in config, the image, or the repo:

```bash
# e.g. write it to a host file with tight perms, mounted read-only into the container
umask 077 && printf '%s' '<your-fine-grained-PAT>' > ~/.config/spec-runner/homelab.pat
# docker run ... -v ~/.config/spec-runner/homelab.pat:/run/secrets/github_pat:ro ...
```

### 1b. Log Claude Code in, inside the container (one time)

Remote Control needs a full-scope claude.ai OAuth session, which only an interactive login
produces — a setup token or API key will not work (verified: probe §1, §4). Do it once; the
credential persists in a named volume across rebuilds:

```bash
docker run -it -v spec-runner-homelab-claude:/home/runner/.claude spec-runner \
  bash -lc 'claude'     # then /login (paste-back code flow works from any device), /exit
```

### 2. Protect the main branch

Require a pull request and disallow direct pushes, so every change reaches `main` through a PR
(FR-056). Note that **auto-merge is enabled by default** (`auto_merge = true`): the runner
merges its own PR once review passes with no blocking finding, and posts a digest first. With
auto-merge on, the always-block list — including `spend_cap` — is the only human checkpoint.
Set `auto_merge = false` to put yourself back in the loop.

### 2b. Land the build-and-test check, and require it (both by hand)

Neither step is runner behaviour, and neither can be — both sit outside the token ceiling §6
defines (R18). See [contracts/ci-check.md](./contracts/ci-check.md).

1. **Commit `.github/workflows/build-and-test.yml` yourself.** GitHub rejects any push touching
   `.github/workflows/` unless the credential holds the Workflows permission, which the runner's
   PAT must never have. If the runner authored the file on a branch, push that branch by hand.
2. **Then require the check** — branch protection → require status checks → `build-and-test`.
   Administration is likewise outside the ceiling, so this is yours.

**Order matters, and getting it backwards stalls the queue.** Require the check only *after* the
runner's merge stage is deployed. Against a runner that merges in the same tick it opens the PR, a
required check makes every merge fail with a 405 and leaves items open with unmerged PRs and
nothing reported (R18, Decision 2).

### 3. Write the instance config

See `contracts/config-schema.md` for the full schema and validation rules. One file per
instance, at `~/.config/spec-runner/<name>.toml`.

### 4. Verify the environment

```bash
spec-runner doctor ~/.config/spec-runner/homelab.toml
```

Every check must pass before scheduling. A failure here is an operator-fixable problem stated
plainly — which is the whole point of having the command.

### 5. Run the live probes (once, phone in hand)

```bash
spec-runner doctor --probe ~/.config/spec-runner/homelab.toml
```

This is the **only** command that spends credits or needs the phone. It resolves the four
assumptions the spec parks for empirical validation — most importantly whether workspace trust
carries into freshly created worktree directories, which is a potential blocker rather than a
wrinkle. Run this **before** building the live-channel work.

### 6. Schedule it

Install a launchd agent per instance (label `com.spec-runner.<slug>`, `StartInterval` 300,
`RunAtLoad` true). launchd rather than cron because it runs a missed job after wake instead of
silently skipping it (FR-001, research R8).

## Running the tests

No test in Tiers 1–3 spends a credit or touches the network.

```bash
dotnet test                                            # everything
dotnet test tests/SpecRunner.UnitTests                 # Tier 1: pure logic
dotnet test tests/SpecRunner.IntegrationTests          # Tier 2: real git + tmux, fake claude
dotnet test tests/SpecRunner.PropertyTests             # Tier 3: the two invariants
```

Tier 2 tests **skip with a stated reason** when tmux is absent — they never silently pass.

The same Tiers 1–3 run on every pull request as the `build-and-test` check, and inside the
container as the instance's `verify` command before any merge. All three run the identical command
(`dotnet build SpecRunner.slnx -c Debug && dotnet test SpecRunner.slnx -c Debug`) so a disagreement
between them can only mean the environment differs — see
[contracts/ci-check.md](./contracts/ci-check.md).

## Validation scenarios

Each maps to a success criterion in `spec.md`. The first five run in the harness with the fake
`claude`; the last two are inherently manual.

| # | Scenario | Proves | How |
|---|---|---|---|
| 1 | **Unattended** (SC-001) | overnight completion across a usage-limit reset | Fake claude runs `fail-usage-limit` mid-implement, then succeeds. Assert: item returned to `ready`, commits preserved, next tick resumed at the right stage, PR opened, issue closed. |
| 2 | **Staged** (SC-002) | intake infers, never asks | File a terse unlabeled fixture issue. Assert: kind + targets labelled, classification posted as a decision comment, run stops at clarify with numbered questions and defaults. |
| 3 | **Isolated** (SC-003) | worktree isolation is structural | Open a session mid-edit in item A's worktree; run item B's full execution pipeline. Assert: A's files byte-identical, and every recorded invocation's `cwd` was an item worktree, never the clone. |
| 4 | **Sequenced** (SC-004) | dependency ordering falls out of integration | Amendment targeting an unmerged spec. Assert: held with a stated reason, no work attempted; merge the fixture PR; assert promoted to ready on the next tick. |
| 5 | **Degradable** (SC-007) | the fallback keeps progress moving | Force session establishment to fail. Assert: one comment with all questions, defaults, rationales, and the stated reason; auth failures called out distinctly; a reply resolves it; the next blocked item goes live again with no reset. |
| 5b | **Reviewed** (SC-009) | nothing closes unreviewed | Run an item whose implementation deliberately omits a test for one acceptance scenario and contains one reversible defect. Assert: the PR opens first and the issue stays open; review examines each touched file before-and-after; it names the uncovered scenario; it fixes the defect on the branch and reports the fixing commit; only then does the issue close and the worktree get pruned. |
| 5c | **Oriented review** (SC-009, R17) | the reviewer is told what it is reviewing | Run a feature item and a chore item to review with the recording fake `claude`. Assert on the recorded review invocation's prompt: the `review_prompt` file's text appears **verbatim and first**; the PR number and URL, base ref, head branch, and issue number are all present; the feature item names its **own** spec directory (with a second spec directory present in the fixture, to prove it is not guessed); the chore says plainly that it has none; the missing coverage manifest is stated rather than omitted; the issue body appears only inside the delimited data region; and **no comment body appears anywhere in the prompt**. |
| 5d | **Checked** (FR-057/058, R18) | a pull request is verified by something other than the process merging it | **Part A (manual, one run):** open a pull request. Assert: a check named `build-and-test` appears, runs `dotnet restore` / `build -c Debug` / `test -c Debug` on `SpecRunner.slnx`, and passes — with no secret referenced and no Claude credit spent. **Part B (automated):** drive a chore to the merge stage with the in-memory GitHub reporting `mergeable_state = blocked`. Assert: no digest is posted, the PR is not merged, the issue stays open, and `stage/merge` is absent. Tick again with `clean`. Assert: `verify` runs, the PR merges, the digest is posted **after** the merge, the issue closes — and **no `claude` invocation is recorded on either tick**. |
| 6 | **Live** (SC-005) | *manual* — phone push, conversational resolution | Force a block during waking hours. Assert: one push, one conversation, resolution lands in the spec/plan file, no typing anywhere else. |
| 7 | **Patient** (SC-006) | *manual* — 24h across two sleeps | Leave scenario 6's session unanswered for 24 hours through two machine sleeps. Assert: still answerable, resolves on first reply, no timeout, no duplicate push, no re-asked question. |

### The two invariants (Tier 3)

- **Crash-convergence**: inject a kill after each individual side effect — label set, comment
  posted, worktree created, commit made — then re-run ticks to quiescence. End state must equal
  the never-crashed end state, with **no duplicated comments or labels**. This is the executable
  form of FR-046 and of every mid-tick crash at once.
- **Injection canary**: seed fixture issues with non-operator comments containing a canary
  string. Assert the canary appears in no recorded fake-claude `argv` or `stdin`, across every
  scenario in the suite. FR-005 and constitution §6 as a guarantee, not a promise.

## Bootstrap note

Version 1 is built by hand from this spec (the design says so explicitly). After that, every
improvement to the runner flows through its own queue: work lands on `work/NN` branches and the
installed binary changes only when the operator merges and redeploys, so the runner never
hot-patches itself mid-run.
