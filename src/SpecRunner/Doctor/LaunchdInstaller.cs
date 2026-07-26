namespace SpecRunner.Doctor;

/// <summary>
/// Generates the per-instance launchd plist (T097/T106). launchd fires the tick by invoking
/// Docker — not a host binary (FR-052a) — passing the config path, mounting the PAT secret and
/// the ~/.claude named volume. Pure string generation so it is testable.
/// </summary>
public static class LaunchdInstaller
{
    public static string Plist(
        string slug,
        string image,
        string configPathInContainer,
        string hostConfigPath,
        string patSecretHostPath,
        string homeVolume,
        int intervalSeconds = 300,
        string dockerHost = "",
        string logPath = "")
    {
        var label = "com.spec-runner." + slug.Replace('/', '.');

        // ONE volume at /home/runner holds .claude (OAuth), the clone, the worktrees, and
        // state/ (lock + log). Single volume is deliberate: the lock file must be shared so
        // overlapping tick containers mutually-exclude (FR-002).
        // No --init — the image's ENTRYPOINT is already tini as PID 1; nesting a second warns.
        var dockerHostEntry = string.IsNullOrEmpty(dockerHost)
            ? string.Empty
            : $"""
                    <key>DOCKER_HOST</key><string>{dockerHost}</string>
              """;

        var logEntries = string.IsNullOrEmpty(logPath)
            ? string.Empty
            : $"""

                 <key>StandardOutPath</key><string>{logPath}</string>
                 <key>StandardErrorPath</key><string>{logPath}</string>
              """;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
              "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{label}</string>
              <key>StartInterval</key><integer>{intervalSeconds}</integer>
              <key>RunAtLoad</key><true/>
              <key>ProgramArguments</key>
              <array>
                <string>/usr/local/bin/docker</string>
                <string>run</string><string>--rm</string>
                <string>-v</string><string>{patSecretHostPath}:/run/secrets/github_pat:ro</string>
                <string>-v</string><string>{homeVolume}:/home/runner</string>
                <string>-v</string><string>{hostConfigPath}:{configPathInContainer}:ro</string>
                <string>{image}</string>
                <string>tick</string><string>{configPathInContainer}</string>
              </array>
              <key>EnvironmentVariables</key>
              <dict>
                <key>PATH</key><string>/usr/local/bin:/usr/bin:/bin</string>
            {dockerHostEntry}  </dict>{logEntries}
            </dict>
            </plist>
            """;
    }
}
