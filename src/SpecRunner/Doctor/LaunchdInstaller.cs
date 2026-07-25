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
        string claudeVolume,
        string worktreesVolume,
        int intervalSeconds = 300)
    {
        var label = "com.spec-runner." + slug.Replace('/', '.');
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
                <string>run</string><string>--rm</string><string>--init</string>
                <string>-v</string><string>{patSecretHostPath}:/run/secrets/github_pat:ro</string>
                <string>-v</string><string>{claudeVolume}:/home/runner/.claude</string>
                <string>-v</string><string>{worktreesVolume}:/home/runner/work</string>
                <string>-v</string><string>{hostConfigPath}:{configPathInContainer}:ro</string>
                <string>{image}</string>
                <string>tick</string><string>{configPathInContainer}</string>
              </array>
            </dict>
            </plist>
            """;
    }
}
