# Runner image. The tick runs here — there is no host .NET or tmux (constitution §2, FR-052a).
# Multi-stage: build a self-contained linux-arm64 single-file binary, then assemble a runtime
# image with git, tmux, and Claude Code (the process-boundary tools the tick shells out to).

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore against just the project files first, so a code-only change reuses the layer cache.
COPY Directory.Build.props ./
COPY src/SpecRunner/SpecRunner.csproj src/SpecRunner/
RUN dotnet restore src/SpecRunner/SpecRunner.csproj -r linux-arm64
COPY src/ src/
RUN dotnet publish src/SpecRunner/SpecRunner.csproj \
        -c Release -r linux-arm64 --no-restore \
        -o /app

# ---- runtime stage ----
FROM debian:bookworm-slim AS runtime

# git: worktrees (≥2.5). tmux: live sessions. ripgrep: Claude Code search.
# tini: a real PID 1 that reaps children — the tick spawns claude/tmux/git every run,
# and without reaping those become zombies (observed in the probe container).
# The .NET runtime is NOT installed — the binary is self-contained.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git tmux ripgrep tini \
    && rm -rf /var/lib/apt/lists/*

ARG USERNAME=runner
RUN useradd --create-home --uid 1000 --shell /bin/bash ${USERNAME}
USER ${USERNAME}
WORKDIR /home/${USERNAME}

# Claude Code (native installer; auto-update off so a mid-run update can't shift behavior).
RUN curl -fsSL https://claude.ai/install.sh | bash
ENV PATH="/home/${USERNAME}/.local/bin:${PATH}"
ENV DISABLE_AUTOUPDATER=1

COPY --chown=${USERNAME}:${USERNAME} --from=build /app/spec-runner /usr/local/bin/spec-runner

# tini as PID 1 reaps the claude/tmux/git children each tick spawns.
# launchd on the host invokes `docker run … spec-runner tick <config>` (FR-052a).
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/spec-runner"]
