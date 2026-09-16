using System.Collections.ObjectModel;
using System.Text;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public static class PetCommandRouter
{
    private const string CodexEscapePrefix = "/codex";

    // Keep the former names as input aliases so old habits and archived
    // examples still route to the same persistent companion conversation.
    private static readonly string[] WakeWordValues = ["柯朵", "小编龙", "编编", "小龙"];

    private static readonly ReadOnlyCollection<string> ReadOnlyWakeWords =
        Array.AsReadOnly(WakeWordValues);

    private static readonly string[] RememberPrefixes =
    [
        "请帮我记住",
        "帮我记住",
        "替我记住",
        "请记住",
        "记住"
    ];

    private static readonly string[] ForgetPrefixes =
    [
        "请帮我忘掉",
        "帮我忘掉",
        "请忘掉",
        "删除记忆",
        "删掉记忆",
        "忘掉"
    ];

    private static readonly string[] ForgetQuestionPrefixes = ["你忘记", "忘记"];

    private static readonly string[] QueryPrefixes =
    [
        "你还记得",
        "还记得",
        "你记得",
        "查一下记忆",
        "查询记忆",
        "找找记忆"
    ];

    private static readonly string[] PersonaPrefixes =
    [
        "更新角色设定",
        "更新设定",
        "角色设定",
        "设定",
        "以后说话"
    ];

    private static readonly HashSet<string> ViewPersonaCommands = new(StringComparer.Ordinal)
    {
        "查看角色设定",
        "查看设定",
        "角色设定是什么",
        "你现在是什么设定"
    };

    private static readonly HashSet<string> ClearPersonaCommands = new(StringComparer.Ordinal)
    {
        "恢复默认设定",
        "清空角色设定",
        "清除角色设定"
    };

    private static readonly HashSet<string> ConfirmClearPersonaCommands = new(StringComparer.Ordinal)
    {
        "确认恢复默认设定",
        "确认清空角色设定",
        "确认清除角色设定"
    };

    private static readonly HashSet<string> QueryAllCommands = new(StringComparer.Ordinal)
    {
        "你记得什么",
        "你都记得什么",
        "都记得什么",
        "记得什么",
        "查看记忆",
        "看看记忆",
        "列出记忆",
        "记忆列表"
    };

    private static readonly HashSet<string> ClearCommands = new(StringComparer.Ordinal)
    {
        "清空记忆",
        "清除记忆",
        "忘掉全部",
        "全部忘掉"
    };

    private static readonly HashSet<string> ConfirmClearCommands = new(StringComparer.Ordinal)
    {
        "确认清空记忆",
        "确认清除记忆",
        "确认忘掉全部",
        "确认全部忘掉"
    };

    public static IReadOnlyList<string> WakeWords => ReadOnlyWakeWords;

    public static string RouteToPetConversation(string? input)
    {
        var content = input?.Trim() ?? string.Empty;
        if (content.Length == 0 || LooksLikePetAddress(content))
        {
            return content;
        }

        return $"柯朵，{content}";
    }

    public static bool LooksLikePetAddress(string? input)
    {
        if (TryStripCodexEscape(input, out _))
        {
            return false;
        }

        return TryStripWakeWord(input, out _, out _);
    }

    public static bool TryStripCodexEscape(string? input, out string codexPrompt)
    {
        codexPrompt = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var candidate = input.TrimStart();
        if (!candidate.StartsWith(CodexEscapePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.Length > CodexEscapePrefix.Length &&
            !IsPrefixBoundary(candidate[CodexEscapePrefix.Length]))
        {
            return false;
        }

        codexPrompt = TrimLeadingSeparators(candidate[CodexEscapePrefix.Length..]);
        return true;
    }

    public static bool TryParse(string? input, out PetCommand? command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(input) || TryStripCodexEscape(input, out _))
        {
            return false;
        }

        if (!TryStripWakeWord(input, out var wakeWord, out var body))
        {
            return false;
        }

        var originalText = input.Trim();
        var normalizedBody = NormalizeCommandText(body);
        var exactCommand = TrimTerminalCommandPunctuation(normalizedBody);

        if (ConfirmClearPersonaCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.ConfirmClearPersona,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        if (ClearPersonaCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.RequestClearPersona,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        if (ViewPersonaCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.ViewPersona,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        if (TryStripAnyPrefix(
                normalizedBody,
                PersonaPrefixes,
                requireArgumentBoundary: true,
                out var personaDirective))
        {
            command = new PetCommand(
                PetCommandKind.UpdatePersona,
                wakeWord,
                TrimLeadingSeparators(personaDirective),
                originalText);
            return true;
        }

        if (ConfirmClearCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.ConfirmClearMemory,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        if (ClearCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.RequestClearMemory,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        // “忘记……了吗”是在问她还记不记得，不是删除指令。删除只接受
        // “忘掉/删除记忆”等明确措辞，避免一句自然疑问永久删掉数据。
        if (TryParseForgetQuestion(normalizedBody, out var rememberedQuestion))
        {
            command = new PetCommand(
                PetCommandKind.QueryMemory,
                wakeWord,
                rememberedQuestion,
                originalText);
            return true;
        }

        if (TryStripAnyPrefix(
                normalizedBody,
                RememberPrefixes,
                requireArgumentBoundary: true,
                out var memory))
        {
            command = new PetCommand(
                PetCommandKind.Remember,
                wakeWord,
                TrimLeadingSeparators(memory),
                originalText);
            return true;
        }

        if (TryStripAnyPrefix(
                normalizedBody,
                ForgetPrefixes,
                requireArgumentBoundary: false,
                out var forgottenMemory))
        {
            command = new PetCommand(
                PetCommandKind.Forget,
                wakeWord,
                TrimLeadingSeparators(forgottenMemory),
                originalText);
            return true;
        }

        if (QueryAllCommands.Contains(exactCommand))
        {
            command = new PetCommand(
                PetCommandKind.QueryMemory,
                wakeWord,
                string.Empty,
                originalText);
            return true;
        }

        if (TryStripAnyPrefix(
                normalizedBody,
                QueryPrefixes,
                requireArgumentBoundary: false,
                out var query))
        {
            command = new PetCommand(
                PetCommandKind.QueryMemory,
                wakeWord,
                TrimQuestion(TrimLeadingSeparators(query)),
                originalText);
            return true;
        }

        command = new PetCommand(
            PetCommandKind.Chat,
            wakeWord,
            normalizedBody,
            originalText);
        return true;
    }

    private static bool TryStripWakeWord(
        string? input,
        out string wakeWord,
        out string body)
    {
        wakeWord = string.Empty;
        body = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var candidate = input.TrimStart();
        foreach (var supportedWakeWord in WakeWordValues)
        {
            if (!candidate.StartsWith(supportedWakeWord, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Length > supportedWakeWord.Length &&
                !IsPrefixBoundary(candidate[supportedWakeWord.Length]))
            {
                continue;
            }

            wakeWord = supportedWakeWord;
            body = TrimLeadingSeparators(candidate[supportedWakeWord.Length..]);
            return true;
        }

        return false;
    }

    private static bool TryStripAnyPrefix(
        string value,
        IEnumerable<string> prefixes,
        bool requireArgumentBoundary,
        out string remainder)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (requireArgumentBoundary &&
                    value.Length > prefix.Length &&
                    !IsLeadingSeparator(value[prefix.Length]))
                {
                    continue;
                }

                remainder = value[prefix.Length..];
                return true;
            }
        }

        remainder = string.Empty;
        return false;
    }

    private static string NormalizeCommandText(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormKC).Trim();
        }
        catch (ArgumentException)
        {
            return value.Trim();
        }
    }

    private static string TrimLeadingSeparators(string value)
    {
        var index = 0;
        while (index < value.Length && IsLeadingSeparator(value[index]))
        {
            index++;
        }

        return value[index..].Trim();
    }

    private static string TrimTerminalCommandPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0 && (char.IsWhiteSpace(value[end - 1]) || IsTerminalPunctuation(value[end - 1])))
        {
            end--;
        }

        return value[..end];
    }

    private static string TrimQuestion(string value)
    {
        var result = TrimTerminalCommandPunctuation(value);
        if (result.EndsWith('吗') || result.EndsWith('嘛'))
        {
            result = result[..^1].TrimEnd();
        }

        return result;
    }

    private static bool TryParseForgetQuestion(string value, out string query)
    {
        query = string.Empty;
        var exact = TrimTerminalCommandPunctuation(value);
        const string questionSuffix = "了吗";
        if (!exact.EndsWith(questionSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in ForgetQuestionPrefixes)
        {
            if (!exact.StartsWith(prefix, StringComparison.Ordinal) ||
                exact.Length <= prefix.Length + questionSuffix.Length)
            {
                continue;
            }

            query = TrimLeadingSeparators(
                exact[prefix.Length..^questionSuffix.Length]);
            if (!string.IsNullOrWhiteSpace(query))
            {
                return true;
            }
        }

        query = string.Empty;
        return false;
    }

    private static bool IsPrefixBoundary(char value) =>
        char.IsWhiteSpace(value) || char.IsPunctuation(value);

    private static bool IsLeadingSeparator(char value) => value is
        ',' or '，' or ':' or '：' or ';' or '；' or '。' or '.' or
        '!' or '！' or '?' or '？' or '、' or '-' or '—' or '~' or '～' ||
        char.IsWhiteSpace(value);

    private static bool IsTerminalPunctuation(char value) => value is
        ',' or '，' or ':' or '：' or ';' or '；' or '。' or '.' or
        '!' or '！' or '?' or '？' or '、' or '~' or '～';
}
