using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpecRunner.Domain;

/// <summary>
/// Pre-seeds Claude Code's per-directory workspace trust (FR-012a). Claude Code gates
/// interactive sessions — and Remote Control — on <c>projects["&lt;path&gt;"].hasTrustDialogAccepted</c>
/// with no interactive pre-accept, and every work item is a fresh worktree directory. The probe
/// proved (§3) that setting this one boolean makes the live session launch clean; an unseeded
/// directory prompts. The JSON transform is pure so it is unit-testable without touching disk.
/// </summary>
public static class WorkspaceTrust
{
    /// <summary>
    /// Returns <paramref name="configJson"/> with <paramref name="worktreePath"/> marked trusted.
    /// Accepts null/empty (a fresh config), preserves any existing projects, and is idempotent.
    /// </summary>
    public static string Seed(string? configJson, string worktreePath)
    {
        var root = string.IsNullOrWhiteSpace(configJson)
            ? new JsonObject()
            : JsonNode.Parse(configJson)?.AsObject() ?? new JsonObject();

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
        }

        if (projects[worktreePath] is not JsonObject entry)
        {
            entry = new JsonObject();
            projects[worktreePath] = entry;
        }

        entry["hasTrustDialogAccepted"] = true;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Whether a directory is already marked trusted in the given config.</summary>
    public static bool IsTrusted(string? configJson, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        return JsonNode.Parse(configJson)?["projects"]?[worktreePath]?["hasTrustDialogAccepted"]
            ?.GetValue<bool>() == true;
    }
}
