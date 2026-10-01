using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalMind.Features;

public static class ModelJson
{
    // 전체 응답이 단일 JSON/무언어 코드블록인 경우에만 외곽을 제거한다. 부분 JSON을 탐색하지 않는다.
    public static JsonDocument ParseObject(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var match = Regex.Match(text, "\\A```(?:json)?[ \\t]*\\r?\\n(?<body>[\\s\\S]*?)\\r?\\n```[ \\t]*\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new InvalidDataException("응답 전체가 단일 JSON 코드블록이 아니다.");
            text = match.Groups["body"].Value;
        }
        var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException("JSON 객체 하나만 허용한다."); }
        return document;
    }
}

public sealed class VaultResponseException(string raw, Exception inner)
    : Exception("문서 답변의 JSON·출처 검증이 실패했다. 결과를 확정하지 않는다.", inner)
{
    public string Raw { get; } = raw;
}
