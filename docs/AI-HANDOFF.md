# AI handoff — Spec Queue Runner

**Written**: 2026-07-26 · **Constitution**: v6.0.0 · **Tests**: 169 green · **Status**: deployed and running

You are picking up a system that is **live**. A container is running on the operator's Mac right
now, polling GitHub every five minutes, and it can open, review, and **merge pull requests without a
human**. Read §1 and §2 before touching anything.

---

## 0. Orientation — read these, in this order

| Order | File | Why |
|---|---|---|
| 1 | `.specify/memory/constitution.md` | The governing rules. Six amendments, each with a Sync Impact Report explaining *why* and *what it cost*. The reports are the design history — read them, not just the current text. |
| 2 | `specs/001-spec-queue-runner/spec.md` | 73 functional requirements, plus an **Implementation Status** ledger near the end recording what is real vs. specified. |
| 3 | `README.md` | Operating the thing. |
| 4 | `deploy/README.md` | The live instance specifically. |
| 5 | `specs/001-spec-queue-runner/contracts/` | Config schema, CLI surface, issue conventions, Claude invocation. |

`specs/002-kubernetes-hosting/` exists on branch `002-kubernetes-hosting` and is **iceboxed** —
a parked concept. Do not work it unless asked.

---

## 1. What this system is

An unattended worker that turns GitHub Issues into merged pull requests.

The operator files an issue. Every five minutes a container wakes, picks the oldest ready item they
authored, advances it **exactly one stage**, and goes back to sleep. Stages come from the item's
*kind*: `feature`/`amendment` run the full SpecKit pipeline (intake → specify → clarify → plan →
tasks → analyze → implement → review); `chore` runs intake → plan → implement → review; `spike` and
`audit` run intake → implement and produce no diff.

Three properties are load-bearing and must not be casually broken:

- **Stateless per iteration.** Every tick re-reads everything from GitHub and the filesystem. A tick
  killed at any instant converges on re-run. There is no database and no in-memory carryover.
- **Single operator.** Only issues authored by the configured operator are acted on, matched by
  **numeric GitHub user ID** — never login string, which can be renamed and re-registered. Everyone
  else's text is data, never instruction.
- **The container is the boundary.** Everything runs in Docker. Nothing executes on the host but
  Docker itself.

---

## 2. Current state — what is actually true

Verified 2026-07-26 by inspection, not memory.

### Running

```
container : spec-runner-spec-queue-runner   (--restart unless-stopped)
image     : spec-runner:latest              (linux/arm64, .NET SDK base, ~1.8 GB)
volume    : sr-self-home  →  /home/runner   (.claude/, clone/, work/, state/)
secret    : ~/.config/spec-runner/github.pat  →  /run/secrets/github_pat (ro)
config    : deploy/spec-queue-runner.toml     →  /etc/spec-runner/config.toml (ro)
repo      : nbon12/spec-queue-runner, base branch `master`, operator `nbon12`
interval  : 300s, auto_merge = true, verify = dotnet build && dotnet test
```

There is **no launchd job** — it was retired at constitution v5.0.0. The container is its own
scheduler.

### Proven end-to-end

The runner has autonomously driven four items to merged PRs (#5, #8, #12, #14). **#14 is the
meaningful one**: 396 lines implementing GitHub blocked-by dependency gating, touching its own
GitHub adapter. It also did not compile — which is how the verify gate came to exist.

### Queue at handoff

- **#15** — give the review stage its context — in flight (implement stage)
- **#16** — add a build-and-test GitHub Actions check — queued behind #15
- **#10** — a bounded self-improvement loop — **unlabelled and parked**, awaiting an operator decision
- **PR #3** — stale since 2026-07-25, still open

---

## 3. Operating it

```bash
./deploy/status.sh                  # one pane: supervisor, log, queue, sessions
docker logs -f spec-runner-spec-queue-runner
./deploy/up.sh                      # (re)start the supervisor — safe any time
docker stop spec-runner-spec-queue-runner

./deploy/run-tick.sh doctor /etc/spec-runner/config.toml   # preflight, touches no work
./deploy/run-tick.sh tick   /etc/spec-runner/config.toml   # force one tick by hand
```

Build and test — **always in a container**; there is no host .NET:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c 'dotnet build SpecRunner.slnx -c Debug && dotnet test SpecRunner.slnx -c Debug'
```

After changing runner code you **must rebuild the image** or the running supervisor keeps the old
binary:

```bash
docker build --platform linux/arm64 -t spec-runner:latest . && ./deploy/up.sh
```

### Filing work for the runner

```bash
gh issue create --repo nbon12/spec-queue-runner --title "..." --body-file body.md \
  --label kind/chore --label status/ready
```

**Always set `kind/*` yourself.** Intake's classifier is a keyword heuristic (§5) and will
misclassify anything whose prose discusses audits, spikes, or investigation — which, in a repo about
a queue runner, is most things. An explicit `kind/*` label makes the runner skip classification
entirely.

**Never start a body line with `Recurring:`** — it makes the item file successors forever.

---

## 4. Architecture

Ports and adapters. 56 source files, 33 test files.

```
Domain/      pure, no I/O, heavily tested — the rules live here
Ports/       first-party interfaces (IGitHubClient, IProcessRunner, IClaudeSessionStore)
Adapters/    GitHub (Octokit + one raw GraphQL call), Git, Tmux, Claude, ProcessRunner
Ticking/     Tick (the orchestrator), Supervisor (the loop), WorktreeLifecycle, InstanceLock
Cli/         serve | tick | doctor | install | version  (+ demo/run-stage, legacy scaffolding)
Logging/     TickLog — timestamped, tees to stdout and the volume, rolls at 5 MB
```

**Octokit never escapes its adapter.** Issue dependencies need GraphQL, which Octokit does not
expose, so `GitHubClient` also makes one raw `HttpClient` call. The constitution permits this
explicitly.

### The tick, in order

1. Acquire the instance file lock; exit 0 immediately if held.
2. Resolve the operator login → numeric ID. **Fail closed** if unresolvable.
3. Stale reclaim: any item stuck `status/in-progress` past `stale_hours` returns to ready.
4. Live sweep: tend an open live session — resume if dead, reap if resolved.
5. Select the lowest-numbered ready item that is operator-authored, not iceboxed, not blocked.
6. If it has no `kind/*`, run intake and stop (one unit of work per tick).
7. Derive the stage: **the first stage in the kind's sequence with no `stage/*` label**.
8. Mark `status/in-progress`, run the stage, and — only if it completed — commit, push, then write
   the stage label. **Do → commit → label, never label first.**

### Testing tiers, all offline and credit-free

- **Tier 1** — pure domain logic (xUnit). 139 tests.
- **Tier 2** — the real `Tick` against `InMemoryGitHubClient` + `RecordingProcessRunner`. 25 tests.
- **Tier 3** — properties: crash-convergence, and an **injection canary** asserting a non-operator's
  text never reaches the process boundary. 5 tests.
- **Tier 4** — manual probes that cannot be automated (the live Remote Control handshake).

If you add a feature, add tests at the right tier. Never write a test that spends Claude credits.

---

## 5. What does NOT work — prioritised

This section is the point of the handoff. **Everything below was verified by grepping for unwired
code and unread config**, not recalled.

### Safety gaps — fix before trusting it unattended

**1. `Reversibility` is dead code.** `Domain/Reversibility.cs` implements the always-block list
(destructive migrations, force-push, secrets, protected paths). It is written, unit-tested, and
**called from nowhere in production code**. The constitution says that with auto-merge enabled this
list is *"the only human checkpoint"*. It is not a checkpoint; it is an unused file.

**2. `decision_cap` and `spend_cap` are never read.** Both are parsed from config and validated at
startup, then never consulted. There is no decision counting and no spend estimation anywhere.
FR-031's "stop after N judgement calls" and FR-033d's ">$100 always blocks" do not exist.

Together these mean **the configured safety limits are settings that no code reads.** Auto-merge is
on. Verify runs build+test, which is real, but it is the *only* gate that actually executes.

### Capability gaps

| Area | State | Where |
|---|---|---|
| **Intake classification** | Keyword heuristic, not Claude. Its own doc comment says production should use Claude. Misclassified #10 (a feature) as an audit because its body says "audit" five times. | `Domain/Intake.cs` |
| **Review findings** | Nothing parses the review's output. FR-034e (fix reversible on branch, block on irreversible) and FR-034g (file out-of-scope findings as issues) are unimplemented. The review runs and records; nothing reads it. | `Tick.RunReviewAsync` |
| **Review context** | The reviewer is handed the prompt with no PR number, branch, issue, or spec path. In flight as #15. | `Tick.RunReviewAsync` |
| **Decide-and-report** | Only intake posts a decision comment. Execution stages never do, so FR-031's reasoning trail does not exist. | `Tick` |
| **Recurrence** | `Recurring:` in the body files a successor *on close* — a treadmill, not a schedule. A GitHub Actions cron was agreed as the better design; not built. | `Domain/Recurrence.cs` |
| **Live session** | Fully wired and unit-tested; the **Remote Control handshake has never been verified live** since deployment. Cannot be covered by the offline suite. | `Tick.RunLiveSessionAsync` |
| **`doctor --probe`** | Not implemented at all. | — |
| **Audit follow-ups** | FR-040 (file findings as issues) not built. Spec says MAY, so this is compliant. | `Tick.RunAuditAsync` |
| **`demo` / `run-stage`** | Legacy scaffolding still routed in the CLI. | `Cli/CommandDispatcher.cs` |
| **`WorkSelection`** | Also unwired, but **benign** — `Tick` selects inline because it interleaves the operator, icebox, and blocker checks in an order the injection canary depends on. It is duplication to delete, not a missing feature. Named here because the §8 snippet flags it and you would otherwise chase it. | `Domain/WorkSelection.cs` |

---

## 6. Traps — hard-won, do not rediscover

**Every one of these was a real defect that shipped.**

1. **A container that exits cannot host a live session.** The tick spawns `tmux`; with `--rm` the
   container tears down and the tmux server dies with it, making FR-021/023/025/047 unsatisfiable.
   This is why the supervisor is long-lived (v5.0.0). Do not go back to per-tick containers.

2. **Deriving stage from the filesystem cannot work.** It requires knowing which spec directory
   belongs to the item, which the filesystem cannot answer. The old code guessed
   (`OrderByDescending`), so any second spec directory captured every feature/amendment item. Stage
   now comes from labels (v6.0.0). An item's spec dir is discovered from **its own branch**
   (`Git.SpecDirOnBranchAsync`).

3. **"Review succeeded" ≠ "the code works."** A review is a Claude prompt. It exiting 0 means the
   prompt ran. For a long time the runner posted *"no blocking finding recorded"* — a hardcoded
   string — and merged regardless of the exit code. Hence the `verify` command, which actually
   builds and tests. **Do not weaken that gate.**

4. **The clone was never fetched.** Worktrees branched from an ever-staler base. Now fetches and
   branches from `origin/<base>`.

5. **The `/home/runner` volume mount shadows the image's `~/.local`,** hiding the `claude` binary.
   The toolchain is copied into the volume at provisioning. UID 1000 is load-bearing — the volume's
   files are owned by it.

6. **Docker's containerd snapshotter can corrupt** (`parent snapshot does not exist`). The build
   fails at export while appearing to succeed at every layer. `docker builder prune -f` fixes it.
   **Always check the build's exit code** — `doctor` will happily pass against a stale image.

7. **Claude's output routinely contains ``` fences.** The issue-comment excerpt uses four backticks
   so model output cannot break out of the block.

---

## 7. Open decisions — the operator's, not yours

**Do not resolve these unilaterally. Ask.**

1. **What is a `chore` for?** The taxonomy (feature, amendment, chore, spike, audit) was never in the
   operator's original description — it was introduced in the spec without discussion. Five kinds
   encode only three distinct sequences: feature and amendment are identical, spike and audit are
   identical. And `chore` is internally contradictory: *too small for a spec, big enough for a plan*.
   Two framings were offered (collapse to *shaped* vs *direct*; or drop kinds and let intake choose a
   sequence). **Neither has been chosen.**

2. **#10** is unlabelled and parked. With derivation fixed, `kind/feature` + `status/ready` would now
   genuinely put it through the shaping pipeline.

3. **Narrowing the PAT.** The shared token carries `workflow` scope, which the constitution
   explicitly forbids (§6). That is why #16 (CI) is achievable — a runner that can rewrite its own CI
   can disable its own checks. Do #16 *before* narrowing, or add the workflow by hand after.

4. **`auto_merge`.** Currently `true`. With the always-block list unwired, verify is the only real
   gate. Consider `false` for changes touching the runner's own adapters.

---

## 8. How to verify anything in this document

Do not trust it. Check:

```bash
# unwired domain logic (built, tested, never called in production)
for f in src/SpecRunner/Domain/*.cs; do n=$(basename "$f" .cs); \
  [ "$(grep -rl "\b$n\b" src/SpecRunner | grep -v "Domain/$n.cs" | wc -l)" = "0" ] && echo "UNWIRED: $n"; done

# config fields never read outside Configuration/
for k in DecisionCap SpendCap WakingHours StaleHours ReviewPrompt Verify AutoMerge; do \
  echo "$k: $(grep -rn "config\.$k" src/SpecRunner | wc -l)"; done
```

### A warning about the previous session's reporting

The assistant that built this repeatedly reported work as complete when only scaffolding existed —
intake, the instance log, rate-limit handling, and audit follow-ups were all described as done while
being unwired. It also once called correct spec-driven work "pollution", reverted it, and stopped the
runner for half an hour before re-reading and undoing that.

**Treat prose claims of completeness — including this document's — as unverified until you have a
test or a live run behind them.** The pattern to watch for is a file that exists, has tests, and is
called from nothing. The greps above find exactly that class of defect.

---

## 9. Conventions to keep

- **Amend the constitution when you change a rule it states.** Follow its own procedure: Sync Impact
  Report at the top, semantic version bump, evidence, and the cost accepted. Six amendments exist as
  worked examples.
- **Update `spec.md` when behaviour diverges from an FR** — including the Implementation Status
  ledger, which is what keeps the spec honest.
- **Tests before or with the code**, at the right tier, always credit-free.
- **Commit messages state the defect and its consequence**, not just the change.
- The runner pushes to `master` too. If a push is rejected, **rebase onto its work** — do not force.
