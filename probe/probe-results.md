# Probe results — 2026-07-25

**Environment**: Docker Desktop on macOS (arm64), Debian bookworm container,
Claude Code 2.1.220, non-root user. Login: full-scope claude.ai OAuth
(`nick.i.bonilla1@gmail.com`, Max plan), minted inside the container.

**Verdict: the runner can be fully containerized, with the phone live-channel intact.**
Every open question resolved favorably. No blocker survived.

## Results

| # | Question | Result |
|---|---|---|
| 1 | In-container login | **PASS** — full-scope claude.ai OAuth via paste-back code flow (containers get the code flow, not localhost redirect, as documented). No macOS Keychain dependency — credential written to `~/.claude/.credentials.json` natively. |
| 2 | Headless `claude -p` | **PASS** — works; not blocked by workspace trust. |
| 3 / OQ-3 | Workspace trust across fresh worktrees | **RESOLVED — not a blocker.** Trust is a per-path boolean `projects["<abs-path>"].hasTrustDialogAccepted` in `~/.claude.json`. Controlled test: a pre-seeded worktree launched interactive Claude Code with **no** trust dialog; an unseeded control worktree **prompted**. The runner pre-seeds this key at `git worktree add` time. |
| 5 / OQ-4 | tmux `send-keys` kickoff | **PASS with caveat** — delivery works, but a bare send-then-Enter is not reliable (one of two attempts failed to submit). Needs the readiness-probe + retry the design already specified. |
| 4 | **Remote Control from inside the container** | **PASS — the headline result.** `claude --remote-control` registered from inside Docker, issued `claude.ai/code/session_016aGJGuKGDtPFxZ9JXX3hAy`, pushed to the operator's phone, and the operator drove the session bidirectionally from the Claude mobile app (including correct answers about the container itself). Undocumented question answered: **yes, it works.** |
| 7 / OQ-5 | Session resumption under Remote Control | **RESOLVED — best case.** Killed the live session (simulating reboot/crash), then `claude --resume <uuid> --remote-control`. Transcript fully restored; Remote Control re-registered with the **same** session ID; the operator's existing phone client reconnected to the **same URL** and got a context-aware answer (`b3f1c789c713`) with no re-run. FR-024 and FR-047 validated. |

## Not run

- **§6 server mode `--spawn worktree`** — deferred. Could subsume FR-012/014/021–026 if Claude Code's own server mode does worktree-per-session natively (docs say it does, at 32 concurrent). Worth running before committing to hand-rolled worktree code.
- **Push-notification confirmation** — both toggles (`Push when actions required`, `Push when Claude decides`) were already `true`; an explicit "a push buzzed my phone" confirmation was not isolated.

## Design consequences (to fold into spec/constitution)

1. **Container is the runner's home.** `.NET 10` and `tmux` move from host prerequisites (T001/T002) to Dockerfile lines; the runner builds and runs `linux-arm64`.
2. **Trust pre-seeding is a new FR.** The runner writes `projects["<worktree-path>"].hasTrustDialogAccepted = true` into `~/.claude.json` when it creates a worktree, before the first interactive session. This is the difference between the live channel working and stalling on OQ-3.
3. **Credential lifecycle.** One interactive in-container `/login`, persisted in a named volume. claude.ai logins expire (documented, ~3-day warning) — `doctor` should check expiry proactively; the Tailscale+SSH break-glass path is for re-login.
4. **Two IDs, not one.** The Remote Control session id (`session_...`, the phone URL) and the Claude Code conversation UUID (what `--resume` takes) are distinct. The design's "Live session: <id>" and the reaper's resume-by-id must record the **conversation UUID**; the RC session id proved stable across resume as a bonus.
5. **Kickoff needs retry.** OQ-4's readiness-probe-plus-retry is required, not optional — confirmed by a real send-keys miss during the probe.

## §6 Server mode — RAN, and it does NOT fit (no FRs deleted)

`claude remote-control --spawn worktree --capacity N` starts one process that
registers an **environment** (not a single session) and serves up to N sessions,
each in an **isolated worktree Claude Code creates on demand**. It also enforces
workspace trust on its base directory (re-confirming OQ-3).

Why it doesn't fit the runner's model:
- The runner needs one worktree per **item**, created by the runner, used by
  **both** the headless `claude -p` run and that item's live session, persisting
  for the item's whole life.
- Server mode's worktrees are CC-owned, per-connection, and disconnected from the
  headless path. There is no documented way to point an on-demand session at a
  specific pre-existing worktree.

Conclusion: keep the design as written. Per-item `claude --remote-control` in the
item's own worktree (proven in §4) is the correct approach. FR-012, FR-014,
FR-021–026, and FR-025's one-session-per-instance all stand. The probe's value
was ruling server mode OUT before any code was written.
