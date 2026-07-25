# Probe image for the Spec Queue Runner architecture decision.
#
# This is NOT the runner image. It exists to answer four questions that decide
# whether the runner is containerised, and it deliberately contains no .NET:
#
#   1. Can Remote Control register and push to a phone from inside a container?
#   2. Does workspace trust survive a freshly created git worktree directory?
#   3. Does `claude remote-control --spawn worktree` subsume hand-rolled worktrees?
#   4. Does session resumption re-register Remote Control cleanly?
#
# Base is Debian rather than Alpine: the docs call out extra musl setup
# (libgcc/libstdc++/ripgrep + USE_BUILTIN_RIPGREP=0) that would add variables to
# a run whose whole purpose is isolating one unknown.

FROM debian:bookworm-slim

# bash + curl: required by the native installer
# git:         worktrees (needs >= 2.5; bookworm ships 2.39)
# tmux:        the live-session wrapper the design depends on
# ripgrep:     Claude Code search
# jq:          inspecting ~/.claude.json to see what workspace trust writes
RUN apt-get update && apt-get install -y --no-install-recommends \
        bash \
        curl \
        ca-certificates \
        git \
        tmux \
        ripgrep \
        jq \
        less \
        procps \
    && rm -rf /var/lib/apt/lists/*

# Non-root. Anthropic's guidance ties unattended permission modes to a non-root
# container user, and the runner will need that mode in production.
ARG USERNAME=runner
ARG UID=1000
RUN useradd --create-home --uid ${UID} --shell /bin/bash ${USERNAME}

USER ${USERNAME}
WORKDIR /home/${USERNAME}

# Same install path as the operator's Mac, so the probe tests the real thing.
RUN curl -fsSL https://claude.ai/install.sh | bash
ENV PATH="/home/${USERNAME}/.local/bin:${PATH}"

# Pin the binary for the duration of the probe. A background auto-update
# mid-probe would make a failure impossible to attribute.
ENV DISABLE_AUTOUPDATER=1

# NOTE: deliberately NOT setting ANTHROPIC_API_KEY / ANTHROPIC_BASE_URL here.
# Both break Remote Control when set, but Docker does not inherit host environment
# variables, so they are already absent. Setting them to "" would make them
# *present but empty*, which risks tripping Claude Code's auth-precedence checks
# (an API key outranks subscription OAuth) and triggers a build warning.
# The probe script verifies they are absent at runtime instead.

COPY --chown=${USERNAME}:${USERNAME} run-probe.sh /home/${USERNAME}/run-probe.sh
RUN chmod +x /home/${USERNAME}/run-probe.sh

CMD ["/bin/bash"]
