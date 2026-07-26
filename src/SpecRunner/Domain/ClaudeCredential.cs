using System.Text.Json;

namespace SpecRunner.Domain;

/// <summary>How healthy the stored claude.ai credential is (FR-052b).</summary>
public enum CredentialHealth
{
    /// <summary>No credential file — a one-time in-container <c>/login</c> is needed.</summary>
    Missing,

    /// <summary>Present but unparseable — treat as absent rather than guess.</summary>
    Unreadable,

    /// <summary>
    /// An access token but no refresh token: it dies for good at <c>expiresAt</c> with no way
    /// back. Actionable now, while there is still time to re-login deliberately.
    /// </summary>
    NoRefreshToken,

    /// <summary>Refreshable. The access token's own expiry is routine and self-healing.</summary>
    Healthy,
}

/// <summary>
/// Reads the claude.ai OAuth credential's health (FR-052b).
///
/// The distinction that matters operationally: the <b>access token</b> lives ~12 hours and Claude
/// Code silently refreshes it, persisting the result to the mounted volume — so its expiry is a
/// non-event and warning on it would cry wolf twice a day. The <b>refresh token</b> is what has
/// real longevity; when it dies (logout, revocation, lapsed subscription) every live session and
/// headless run stalls silently. So the check is "is this still refreshable", not "is it fresh".
///
/// Pure parsing over the file's text, so every case is unit-testable without a real credential.
/// Never returns or logs token material.
/// </summary>
public static class ClaudeCredential
{
    public static CredentialStatus Parse(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return new CredentialStatus(CredentialHealth.Missing, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);

            // The OAuth block is nested under "claudeAiOauth"; tolerate a flat shape too.
            var root = doc.RootElement;
            var oauth = root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("claudeAiOauth", out var nested)
                ? nested
                : root;

            if (oauth.ValueKind != JsonValueKind.Object)
            {
                return new CredentialStatus(CredentialHealth.Unreadable, null);
            }

            DateTimeOffset? expiry = null;
            if (oauth.TryGetProperty("expiresAt", out var exp) &&
                exp.ValueKind == JsonValueKind.Number &&
                exp.TryGetInt64(out var ms))
            {
                expiry = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }

            var refreshable =
                oauth.TryGetProperty("refreshToken", out var refresh) &&
                refresh.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(refresh.GetString());

            if (!refreshable)
            {
                // No way back once the access token lapses — but if there's no access token
                // either, the credential is simply absent in substance.
                var hasAccess =
                    oauth.TryGetProperty("accessToken", out var access) &&
                    access.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(access.GetString());
                return new CredentialStatus(
                    hasAccess ? CredentialHealth.NoRefreshToken : CredentialHealth.Missing, expiry);
            }

            return new CredentialStatus(CredentialHealth.Healthy, expiry);
        }
        catch (JsonException)
        {
            return new CredentialStatus(CredentialHealth.Unreadable, null);
        }
    }
}

/// <summary>The credential verdict plus the access token's expiry (informational).</summary>
public sealed record CredentialStatus(CredentialHealth Health, DateTimeOffset? AccessTokenExpiry)
{
    /// <summary>Only a refreshable credential keeps the runner working unattended.</summary>
    public bool Ok => Health is CredentialHealth.Healthy;

    /// <summary>
    /// A one-line account for <c>doctor</c>. Access-token expiry is reported as context, never as
    /// a failure — it refreshes itself.
    /// </summary>
    public string Describe(DateTimeOffset now) => Health switch
    {
        CredentialHealth.Missing =>
            "no claude.ai credential — run the one-time in-container /login",
        CredentialHealth.Unreadable =>
            "credential file is unreadable — re-run the in-container /login",
        CredentialHealth.NoRefreshToken =>
            "no refresh token: this credential dies for good when the access token lapses — re-login",
        _ => AccessTokenExpiry is { } e
            ? $"refreshable; access token {(e <= now ? "lapsed" : "valid")} " +
              $"({e:yyyy-MM-dd HH:mm} UTC) — refreshes automatically"
            : "refreshable",
    };
}
