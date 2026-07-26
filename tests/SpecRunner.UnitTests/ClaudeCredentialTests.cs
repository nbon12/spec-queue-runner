using SpecRunner.Domain;
using Xunit;

namespace SpecRunner.UnitTests;

/// <summary>
/// The credential check's job is to distinguish "refreshes itself" from "will stall silently"
/// (FR-052b) — not to flag the ~12h access-token expiry, which is routine and self-healing.
/// </summary>
public class ClaudeCredentialTests
{
    private const long ExpiryMs = 1785116423103; // 2026-07-26 21:40 UTC-ish

    private static string Json(string body) => "{\"claudeAiOauth\":{" + body + "}}";

    [Fact]
    public void Missing_file_is_missing()
    {
        Assert.Equal(CredentialHealth.Missing, ClaudeCredential.Parse(null).Health);
        Assert.Equal(CredentialHealth.Missing, ClaudeCredential.Parse("   ").Health);
    }

    [Fact]
    public void Garbage_is_unreadable_not_a_crash()
    {
        Assert.Equal(CredentialHealth.Unreadable, ClaudeCredential.Parse("{not json").Health);
    }

    [Fact]
    public void Refreshable_credential_is_healthy_even_when_the_access_token_lapsed()
    {
        // The real-world steady state: access token expired hours ago, refresh token still good.
        var status = ClaudeCredential.Parse(
            Json("\"accessToken\":\"a\",\"refreshToken\":\"r\",\"expiresAt\":" + ExpiryMs));

        Assert.Equal(CredentialHealth.Healthy, status.Health);
        Assert.True(status.Ok);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ExpiryMs), status.AccessTokenExpiry);
    }

    [Fact]
    public void Access_token_without_a_refresh_token_is_flagged()
    {
        var status = ClaudeCredential.Parse(Json("\"accessToken\":\"a\",\"expiresAt\":" + ExpiryMs));

        Assert.Equal(CredentialHealth.NoRefreshToken, status.Health);
        Assert.False(status.Ok);
        Assert.Contains("re-login", status.Describe(DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_token_is_simply_missing()
    {
        Assert.Equal(CredentialHealth.Missing, ClaudeCredential.Parse(Json("\"scopes\":[]")).Health);
    }

    [Fact]
    public void A_flat_shape_without_the_oauth_wrapper_still_parses()
    {
        Assert.Equal(CredentialHealth.Healthy,
            ClaudeCredential.Parse("""{"accessToken":"a","refreshToken":"r"}""").Health);
    }

    [Fact]
    public void Lapsed_access_token_is_described_as_context_not_failure()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(-5);
        var status = ClaudeCredential.Parse(
            Json("\"accessToken\":\"a\",\"refreshToken\":\"r\",\"expiresAt\":" + expiry.ToUnixTimeMilliseconds()));

        Assert.True(status.Ok); // still passes — this is the routine state
        var described = status.Describe(DateTimeOffset.UtcNow);
        Assert.Contains("lapsed", described, StringComparison.Ordinal);
        Assert.Contains("refreshes automatically", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_never_leaks_token_material()
    {
        var status = ClaudeCredential.Parse(
            Json("\"accessToken\":\"SECRET-ACCESS\",\"refreshToken\":\"SECRET-REFRESH\",\"expiresAt\":" + ExpiryMs));
        var described = status.Describe(DateTimeOffset.UtcNow);

        Assert.DoesNotContain("SECRET", described, StringComparison.Ordinal);
    }
}
