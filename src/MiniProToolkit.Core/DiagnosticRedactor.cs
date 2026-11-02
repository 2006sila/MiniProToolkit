using System.Text.RegularExpressions;

namespace MiniProToolkit.Core;

public static class DiagnosticRedactor
{
    public static string Redact(string text)
    {
        text = Regex.Replace(text, """(?i)[\"']?\b[a-z0-9_-]*(authorization|password|passwd|token|api[_-]?key|secret)\b[\"']?\s*[:=]\s*[\"']?[^\"'\r\n,;}]+[\"']?""", "$1=[REDACTED]");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [REDACTED]");
        text = Regex.Replace(text, @"(?i)[a-z]:[\\/][^\r\n\""<>|]*", "[LOCAL_PATH]");
        text = Regex.Replace(text, @"\\\\[^\r\n\s]+", "[NETWORK_PATH]");
        return text;
    }
}
