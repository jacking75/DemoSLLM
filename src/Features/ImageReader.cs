using System.Text;
using System.Text.Json;
using LocalMind.Core;

namespace LocalMind.Features;

public record ExtractedRow(string Name, decimal? Quantity, decimal? Amount);
public record ImageExtraction(string Kind, decimal? Total, IReadOnlyList<ExtractedRow> Rows, string Raw, int Attempts);
public sealed class ImageReader(IInferenceBackend backend)
{
    public async Task<string> ReadAsync(ImageInput image, bool diagnose, CancellationToken ct)
    {
        var prompt = diagnose
            ? "이미지에 보이는 오류 메시지와 오류 코드를 그대로 적고, 가능한 원인과 안전한 확인 방법 2가지를 한국어로 간결하게 적어라. 이미지 밖의 정보는 추측이라고 표시하라."
            : "이미지에 보이는 주요 내용과 숫자를 한국어로 간결하게 설명하라. 보이지 않는 정보는 만들어내지 마라.";
        return await CompleteAsync(backend, new(prompt, 512, Images: [image]), ct);
    }
    public async Task<ImageExtraction> ExtractAsync(ImageInput image, CancellationToken ct)
    {
        const string schema = "이미지의 표나 영수증을 읽고 JSON 객체 하나만 출력하라. 스키마: {\"kind\":\"receipt 또는 table\",\"total\":숫자 또는 null,\"rows\":[{\"name\":\"항목\",\"quantity\":숫자 또는 null,\"amount\":숫자 또는 null}]}. 금액은 쉼표 없는 숫자이다. 표의 각 행은 name에 행 이름, quantity에 첫 숫자 열, amount에 두 번째 숫자 열을 넣는다. 보이지 않는 값은 null로 한다. total은 이미지의 합계 표시 그대로이며 없으면 null이다. 설명이나 코드블록은 출력하지 마라.";
        Exception? failure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var raw = await CompleteAsync(backend, new(schema + (attempt > 1 ? " 이전 형식이 잘못됐다. 모든 필드를 포함한 유효한 JSON만 출력하라." : ""), 1024, 0.2, Images: [image]), ct);
            try { return Parse(raw, attempt); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException) { failure = ex; }
        }
        throw new InvalidDataException("JSON 형식 검증이 3회 실패했다. 결과를 확정하거나 저장하지 않는다.", failure);
    }
    public static ImageExtraction Parse(string raw, int attempts = 1)
    {
        using var document = JsonDocument.Parse(raw.Trim());
        var obj = document.RootElement;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("kind", out var kind) || kind.GetString() is not ("receipt" or "table") || !obj.TryGetProperty("total", out var total) || !obj.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("추출 스키마가 다르다.");
        decimal? Number(JsonElement n) => n.ValueKind == JsonValueKind.Null ? null : n.ValueKind == JsonValueKind.Number && n.TryGetDecimal(out var d) ? d : throw new InvalidDataException("숫자 형식이 다르다.");
        List<ExtractedRow> result = [];
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || !row.TryGetProperty("quantity", out var q) || !row.TryGetProperty("amount", out var a)) throw new InvalidDataException("추출 행이 유효하지 않다.");
            result.Add(new(name.GetString()!, Number(q), Number(a)));
        }
        if (result.Count == 0) throw new InvalidDataException("추출 행이 없다.");
        return new(kind.GetString()!, Number(total), result, raw, attempts);
    }
    public static string Csv(ImageExtraction extraction)
    {
        string Escape(string value) { if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        string Num(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var csv = new StringBuilder("항목,수량/첫 열,금액/둘째 열\r\n");
        foreach (var row in extraction.Rows) csv.AppendLine($"{Escape(row.Name)},{Num(row.Quantity)},{Num(row.Amount)}");
        csv.AppendLine($"합계,,{Num(extraction.Total)}"); return csv.ToString();
    }
    public static async Task<string> CompleteAsync(IInferenceBackend backend, ChatRequest request, CancellationToken ct)
    {
        var result = new StringBuilder();
        await foreach (var chunk in backend.StreamChatAsync(request, ct)) result.Append(chunk.Content);
        return result.ToString();
    }
}
