using System.Text.Json.Nodes;
using System.IO;
using System.Text.RegularExpressions;

namespace XiaobianPet.Models;

/// <summary>Classifies untrusted task data; nothing here executes task instructions.</summary>
public static class PetTaskContextSignals
{
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".kt", ".swift",
      ".c", ".h", ".cpp", ".hpp", ".cc", ".rs", ".go", ".rb", ".php", ".vue", ".svelte",
      ".html", ".css", ".scss", ".xaml", ".sql", ".sh", ".ps1", ".lua", ".dart" };

    public static (bool ResumeRequested, bool HasCodeEdit) Read(JsonArray? items)
    {
        if (items is null) return (false, false);
        bool resume = false, code = false;
        foreach (var item in items.OfType<JsonObject>())
        {
            var type = Text(item["type"]);
            if (type == "userMessage")
            {
                var text = Text(item["text"]);
                if (item["content"] is JsonArray content)
                    text += string.Join("\n", content.OfType<JsonObject>()
                        .Where(part => Text(part["type"]) is "text" or "input_text")
                        .Select(part => Text(part["text"])));
                else text += Text(item["content"]);
                // Do not react to quotes, attached documents, or long pasted histories.
                var firstLine = text.Trim().Split('\n', '\r')[0].Trim();
                if (firstLine.Length <= 160)
                    resume |= Regex.IsMatch(firstLine,
                        @"^(?:请|帮我|麻烦|那|好[，, ]*|嗯[，, ]*){0,3}(?:继续|接着|接着做|恢复之前|接上之前)|^(?:please\s+)?(?:continue|resume)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(30));
            }
            if (type == "fileChange" && Text(item["status"]) is not ("failed" or "declined"))
            {
                if (item["changes"] is JsonArray changes)
                    code |= changes.OfType<JsonObject>().Any(change => IsCodePath(Text(change["path"])));
                code |= IsCodePath(Text(item["path"]));
            }
            // Tool names are evidence; merely mentioning code in a command/log is not.
            if (type is "mcpToolCall" or "dynamicToolCall" &&
                Text(item["tool"]) is "apply_patch" &&
                Text(item["status"]) is not ("failed" or "declined"))
            {
                var patch = Text(item["arguments"]);
                if (patch.Length <= 65536)
                    code |= patch.Split('\n').Where(line => line.StartsWith("*** Update File: ") ||
                            line.StartsWith("*** Add File: ") || line.StartsWith("*** Delete File: "))
                        .Any(line => IsCodePath(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim()));
            }
        }
        return (resume, code);
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool IsCodePath(string path) =>
        path.Length is > 0 and < 2048 && CodeExtensions.Contains(Path.GetExtension(path));
}
