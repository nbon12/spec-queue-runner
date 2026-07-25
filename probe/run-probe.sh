#!/usr/bin/env bash
# Spec Queue Runner — architecture probe.
#
# Answers the open questions that decide whether the runner is containerised,
# and whether Claude Code's own server mode replaces work the design does by hand.
# Run inside the probe container, with your phone nearby.
#
# Deliberately does NOT `set -e`: a failed check is a result, not a crash.
set -uo pipefail

RESULTS="${HOME}/probe-results.md"
WORK="${HOME}/probe-work"
PASS=0; FAIL=0; UNKNOWN=0

c_ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
c_bad()  { printf '\033[31m%s\033[0m\n' "$*"; }
c_warn() { printf '\033[33m%s\033[0m\n' "$*"; }
c_head() { printf '\n\033[1;36m== %s ==\033[0m\n' "$*"; }

record() { printf '%s\n' "$*" >> "$RESULTS"; }

pass() { PASS=$((PASS+1)); c_ok   "  PASS    $1"; record "- **PASS** — $1"; }
fail() { FAIL=$((FAIL+1)); c_bad  "  FAIL    $1"; record "- **FAIL** — $1"; }
unkn() { UNKNOWN=$((UNKNOWN+1)); c_warn "  UNKNOWN $1"; record "- **UNKNOWN** — $1"; }

# Ask the operator a question the machine cannot answer for itself.
ask() {
    local q="$1"
    local answer
    printf '\n\033[1;35m  ? %s\033[0m [y/n/s(kip)] ' "$q"
    read -r answer </dev/tty
    case "$answer" in
        y|Y) pass "$q"; return 0 ;;
        n|N) fail "$q"; return 1 ;;
        *)   unkn "$q (skipped)"; return 2 ;;
    esac
}

: > "$RESULTS"
record "# Probe results"
record ""
record "Run at: $(date -u '+%Y-%m-%dT%H:%M:%SZ') (UTC)"
record "Claude Code: $(claude --version 2>/dev/null || echo 'not found')"
record "Container: $(uname -sm)"
record ""

# ---------------------------------------------------------------------------
c_head "0. Environment"
# ---------------------------------------------------------------------------
for tool in claude git tmux rg jq; do
    if command -v "$tool" >/dev/null 2>&1; then
        pass "$tool present ($("$tool" --version 2>/dev/null | head -1))"
    else
        fail "$tool MISSING"
    fi
done

# An API key in the environment breaks Remote Control outright, and a non-Anthropic
# base URL disables it. Both are documented failure modes worth ruling out first.
[ -z "${ANTHROPIC_API_KEY:-}" ] && pass "ANTHROPIC_API_KEY unset (required for Remote Control)" \
                                || fail "ANTHROPIC_API_KEY is set — Remote Control will refuse to start"
case "${ANTHROPIC_BASE_URL:-}" in
    ""|https://api.anthropic.com*) pass "ANTHROPIC_BASE_URL unset or api.anthropic.com" ;;
    *) fail "ANTHROPIC_BASE_URL=${ANTHROPIC_BASE_URL} — Remote Control is disabled off api.anthropic.com" ;;
esac

# ---------------------------------------------------------------------------
c_head "1. Authentication (one-time, interactive)"
# ---------------------------------------------------------------------------
cat <<'EOF'
  Remote Control needs a FULL-SCOPE session token from an interactive login.
  A setup token or API key cannot establish it — that is documented, not a guess.
  So we mint the credential inside the container rather than passing one in.

  If you are not already logged in, run this in ANOTHER shell on this container:

      docker exec -it specrunner-probe claude

  ...then use /login and complete the flow in your Mac's browser.
EOF

if claude auth status >/dev/null 2>&1; then
    pass "claude auth status reports an authenticated session"
    claude auth status 2>&1 | sed 's/^/      /'
else
    unkn "claude auth status did not report a session — log in before continuing"
fi

ask "Is the login a claude.ai OAuth session (NOT an API key / setup token)?"

# ---------------------------------------------------------------------------
c_head "2. Headless path (the easy one — expected to work)"
# ---------------------------------------------------------------------------
rm -rf "$WORK"; mkdir -p "$WORK/repo"
cd "$WORK/repo" || exit 1
git init -q . && git config user.email probe@local && git config user.name Probe
echo "hello" > README.md && git add -A && git commit -qm "initial"
pass "fixture git repo created at $WORK/repo"

if timeout 120 claude -p "Reply with exactly: PROBE_OK" 2>&1 | tee /tmp/headless.txt | grep -q "PROBE_OK"; then
    pass "headless 'claude -p' works in container"
else
    fail "headless 'claude -p' failed — see /tmp/headless.txt"
fi

# ---------------------------------------------------------------------------
c_head "3. Workspace trust across fresh worktrees  (OQ-3 — the potential blocker)"
# ---------------------------------------------------------------------------
cat <<'EOF'
  Trust is per-DIRECTORY with no documented pre-accept mechanism, and Remote
  Control has a specific "Workspace not trusted" failure. The runner creates a
  NEW worktree directory per work item, so if trust does not carry, every item's
  first live session stalls on a prompt nobody is there to answer.

  We snapshot ~/.claude.json around the trust prompt to see exactly what gets
  written. If trust is just a list of paths, we can pre-seed it and this
  dissolves from blocker to config.
EOF

cp -f "${HOME}/.claude.json" /tmp/claude.json.before 2>/dev/null || echo '{}' > /tmp/claude.json.before

git -C "$WORK/repo" worktree add -q "$WORK/wt-1" -b probe/one 2>/dev/null \
    && pass "worktree 1 created at $WORK/wt-1" \
    || fail "git worktree add failed"

cat <<'EOF'

  In ANOTHER shell, run Claude Code from the FIRST worktree and accept trust:

      docker exec -it specrunner-probe bash -lc 'cd ~/probe-work/wt-1 && claude'

  Accept the trust dialog, then /exit.
EOF
ask "Did a workspace-trust dialog appear in worktree 1?"

cp -f "${HOME}/.claude.json" /tmp/claude.json.after 2>/dev/null || echo '{}' > /tmp/claude.json.after
c_warn "  --- what accepting trust wrote to ~/.claude.json ---"
if command -v jq >/dev/null 2>&1; then
    diff <(jq -S . /tmp/claude.json.before 2>/dev/null) \
         <(jq -S . /tmp/claude.json.after  2>/dev/null) | sed 's/^/      /' | head -40
fi
record ""
record "### Trust-state diff after accepting worktree 1"
record '```'
diff <(jq -S . /tmp/claude.json.before 2>/dev/null) \
     <(jq -S . /tmp/claude.json.after 2>/dev/null) >> "$RESULTS" 2>/dev/null
record '```'
record ""

# Second, brand-new worktree: does trust carry, or is it prompted again?
git -C "$WORK/repo" worktree add -q "$WORK/wt-2" -b probe/two 2>/dev/null \
    && pass "worktree 2 created at $WORK/wt-2 (never trusted)" \
    || fail "second git worktree add failed"

cat <<'EOF'

  Now run Claude Code from the SECOND, never-trusted worktree:

      docker exec -it specrunner-probe bash -lc 'cd ~/probe-work/wt-2 && claude'
EOF
if ask "Did worktree 2 SKIP the trust prompt (i.e. trust carried over)?"; then
    c_ok "  => OQ-3 resolved favourably: trust is not per-worktree. No blocker."
    record "- **OQ-3**: trust carried to a fresh worktree — not a blocker."
else
    c_bad "  => OQ-3 CONFIRMED AS BLOCKER unless trust can be pre-seeded."
    c_warn "     Check the diff above: if trust is a path list, seed it at worktree creation."
    record "- **OQ-3**: trust did NOT carry. Pre-seeding ~/.claude.json is the fallback; see diff."
fi

# ---------------------------------------------------------------------------
c_head "4. Remote Control from inside a container  (THE question)"
# ---------------------------------------------------------------------------
cat <<'EOF'
  Undocumented in both directions. Everything Remote Control requires is
  satisfiable in a container (subscription, claude.ai OAuth, api.anthropic.com,
  workspace trust) — but nobody has written down whether it actually works.

  In ANOTHER shell, from a TRUSTED worktree:

      docker exec -it specrunner-probe bash -lc 'cd ~/probe-work/wt-1 && claude --remote-control'

  Have your phone ready with the Claude app signed in to the same account.
EOF

ask "Did Remote Control start WITHOUT an error notification?"
ask "Does the session appear at claude.ai/code (or in the app's Code list)?"
ask "Can you send a message from your PHONE and see the container session respond?"
if ask "Did a PUSH NOTIFICATION reach your phone? (enable via /config: 'Push when actions required')"; then
    c_ok "  => Containerised live channel is VIABLE. Full containerisation is on."
    record "- **Remote Control in container**: WORKS. Full containerisation viable."
else
    c_warn "  => Live channel may not survive containerisation."
    c_warn "     Fallbacks: (a) contain everything, use the comment fallback (FR-027);"
    c_warn "                (b) split — headless contained, live sessions on the host."
    record "- **Remote Control in container**: did not fully work. Choose comment-fallback or split shape."
fi

# ---------------------------------------------------------------------------
c_head "5. tmux kickoff delivery  (OQ-4)"
# ---------------------------------------------------------------------------
tmux kill-session -t probe 2>/dev/null
if tmux new-session -d -s probe -c "$WORK/wt-1" 2>/dev/null; then
    pass "tmux detached session created"
    tmux send-keys -t probe "claude" C-m
    sleep 12
    tmux send-keys -t probe "Reply with exactly: KICKOFF_OK" C-m
    sleep 20
    if tmux capture-pane -p -t probe 2>/dev/null | grep -q "KICKOFF_OK"; then
        pass "tmux send-keys kickoff reached the prompt (OQ-4 resolved)"
    else
        unkn "kickoff not visible in pane — may need a longer readiness probe"
        c_warn "  pane tail:"; tmux capture-pane -p -t probe 2>/dev/null | tail -12 | sed 's/^/      /'
    fi
    tmux kill-session -t probe 2>/dev/null
else
    fail "tmux could not create a session in the container"
fi

# ---------------------------------------------------------------------------
c_head "6. Server mode with native worktrees  (may delete a chunk of the design)"
# ---------------------------------------------------------------------------
cat <<'EOF'
  `claude remote-control --spawn worktree` gives each on-demand session its own
  git worktree, with --capacity defaulting to 32 concurrent sessions from ONE
  process. The runner currently hand-rolls worktree-per-item AND caps itself at
  one live session per instance (FR-025) because of a limit server mode lifts.

  In ANOTHER shell:

      docker exec -it specrunner-probe bash -lc \
        'cd ~/probe-work/repo && claude remote-control --spawn worktree --capacity 4'
EOF

ask "Did server mode start and print a session URL?"
ask "Does connecting a SECOND device/session create its own worktree automatically?"
if ask "Could server mode replace the runner's hand-rolled worktree + session management?"; then
    c_ok "  => Consider collapsing FR-012/014/021-026 onto server mode."
    record "- **Server mode**: viable replacement for hand-rolled worktree/session management."
else
    record "- **Server mode**: not a fit; keep hand-rolled worktrees."
fi

# ---------------------------------------------------------------------------
c_head "7. Session resumption under Remote Control  (OQ-5)"
# ---------------------------------------------------------------------------
cat <<'EOF'
  The reaper respawns dead sessions by recorded ID. If resumption mints a NEW
  Remote Control registration, the runner must rewrite the recorded ID — cosmetic.
  If it fails outright, dead sessions restart the conversation — annoying, and
  FR-024's "nothing re-asks, no duplicate push" guarantee weakens.

  Kill the Remote Control session (Ctrl-C), then:

      docker exec -it specrunner-probe bash -lc \
        'cd ~/probe-work/wt-1 && claude --resume --remote-control'
EOF

ask "Did --resume restore the SAME conversation?"
ask "Did Remote Control re-register without re-asking the earlier questions?"

# ---------------------------------------------------------------------------
c_head "Summary"
# ---------------------------------------------------------------------------
printf '  passed: %d   failed: %d   unknown: %d\n' "$PASS" "$FAIL" "$UNKNOWN"
record ""
record "## Totals"
record ""
record "- passed: $PASS"
record "- failed: $FAIL"
record "- unknown: $UNKNOWN"
c_ok "  Results written to $RESULTS"
echo
echo "  Copy them out with:"
echo "      docker cp specrunner-probe:$RESULTS ./probe/probe-results.md"
