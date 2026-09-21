using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CursorSync.Models;

namespace CursorSync.Services;

public static class ConversationPreviewService
{
    private static readonly Regex UserQuery = new(
        @"<user_query>\s*(.*?)\s*</user_query>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static ConversationPreview Load(AgentRecord? agent)
    {
        if (agent is null)
            return new ConversationPreview { Status = "Select an agent to see how the conversation started and how it ended." };

        var file = FindTranscript(agent.TranscriptDir);
        if (file is null)
            return new ConversationPreview
            {
                Status = "No transcript file was found for this agent. The chat may still exist in Cursor’s database."
            };

        var users = new List<string>();
        var assistants = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var role = root.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "";
                    var text = ExtractText(root);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        var query = ExtractUserQuery(text);
                        if (!string.IsNullOrWhiteSpace(query))
                            users.Add(query);
                    }
                    else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        assistants.Add(text);
                    }
                }
                catch
                {
                    // skip malformed lines
                }
            }
        }
        catch (Exception ex)
        {
            return new ConversationPreview { Status = "Could not read the transcript: " + ex.Message };
        }

        if (users.Count == 0 && assistants.Count == 0)
            return new ConversationPreview { Status = "The transcript has no readable messages yet." };

        var openingUser = users.FirstOrDefault() ?? "";
        var closingUser = users.LastOrDefault() ?? openingUser;
        var openingAssistant = assistants.FirstOrDefault() ?? "";
        var closingAssistant = assistants.LastOrDefault() ?? openingAssistant;
        var showClosing = users.Count > 1 || assistants.Count > 1;

        return new ConversationPreview
        {
            OpeningUser = Clip(openingUser),
            OpeningAssistant = Clip(openingAssistant),
            ClosingUser = showClosing ? Clip(closingUser) : "",
            ClosingAssistant = showClosing ? Clip(closingAssistant) : "",
            Status = users.Count <= 1
                ? "This conversation is still short — start and end are the same turn."
                : $"{users.Count} user message{(users.Count == 1 ? "" : "s")} in the transcript.",
            HasContent = true
        };
    }

    private static string? FindTranscript(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return null;

        var named = Path.Combine(dir, Path.GetFileName(dir) + ".jsonl");
        if (File.Exists(named))
            return named;

        return Directory.EnumerateFiles(dir, "*.jsonl").FirstOrDefault();
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            return ExtractContent(content);
        if (root.TryGetProperty("content", out content))
            return ExtractContent(content);
        return "";
    }

    private static string ExtractContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString()?.Trim() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var raw = part.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (builder.Length > 0)
                        builder.AppendLine();
                    builder.Append(raw.Trim());
                }
                continue;
            }

            if (!part.TryGetProperty("type", out var type) || type.GetString() != "text")
                continue;
            if (!part.TryGetProperty("text", out var textEl))
                continue;
            var text = textEl.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(text.Trim());
            }
        }

        return builder.ToString();
    }

    private static string ExtractUserQuery(string text)
    {
        var match = UserQuery.Match(text);
        var raw = match.Success ? match.Groups[1].Value : text;
        raw = Regex.Replace(raw, @"<timestamp>.*?</timestamp>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"<[^>]+>", " ");
        raw = raw.Replace("\\n", "\n");
        return Collapse(raw);
    }

    private static string Clip(string text)
    {
        text = Collapse(text);
        var lines = text.Split('\n');
        if (lines.Length > 8)
            text = string.Join("\n", lines.Take(8)) + "\n…";
        return text.Length <= 600 ? text : text[..600].TrimEnd() + "…";
    }

    private static string Collapse(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines).Trim();
    }
}
