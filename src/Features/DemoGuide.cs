using System.Security.Cryptography;
using System.Text.Json;

namespace LocalMind.Features;

public record DemoStep(string Id, string Title, string Screen, int Seconds, string Action,
    string? Sample, string? Question, string Instruction, string Notes, string? Replay);
public record ReplaySource(string Id, string Kind, string Evidence, string Sha256, string CapturedAt,
    string Model, int Context, int Item = 0);
public record DemoContent(int Version, DemoStep[] Steps, Dictionary<string, string[]> Ideas, ReplaySource[] Replays);
public record ReplayView(string Input, string Output, string Provenance);

public sealed class DemoGuide
{
    public DemoContent Content { get; }
    private readonly string root;
    public int Position { get; private set; } = -1;
    public bool Active => Position >= 0;
    public DemoStep Current => Active ? Content.Steps[Position] : throw new InvalidOperationException("데모를 먼저 시작해야 한다.");
    public DemoGuide(string root)
    {
        this.root = Path.GetFullPath(root);
        Content = JsonSerializer.Deserialize<DemoContent>(File.ReadAllText(ResolveAsset("assets/demo/guide.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("데모 구성이 비어 있다.");
        if (Content.Version != 1 || Content.Steps is not { Length: > 0 } || Content.Steps.Any(s => s is null || s.Seconds <= 0)
            || Content.Steps.Sum(s => s.Seconds) != 420 || Content.Steps.Select(s => s.Id).Distinct().Count() != Content.Steps.Length)
            throw new InvalidDataException("7분 진행표 또는 단계 번호가 유효하지 않다.");
        if (Content.Replays is null || Content.Replays.Select(r => r.Id).Distinct().Count() != Content.Replays.Length
            || Content.Ideas is null || new[] { "A", "B", "E", "F" }.Any(k => !Content.Ideas.TryGetValue(k, out var cards)
                || cards.Length != 3 || cards.Any(string.IsNullOrWhiteSpace))) throw new InvalidDataException("재생 목록 또는 아이디어 카드가 유효하지 않다.");
        foreach (var step in Content.Steps)
        {
            if (step.Screen is not ("setup" or "A" or "B" or "E" or "F" or "review" or "ideas")
                || step.Action is not ("setup" or "receipt" or "error" or "vault" or "shortcut" or "review" or "limits" or "ideas")
                || string.IsNullOrWhiteSpace(step.Title) || string.IsNullOrWhiteSpace(step.Instruction))
                throw new InvalidDataException("데모 단계가 유효하지 않다.");
            if (step.Sample is not null && !File.Exists(ResolveAsset(step.Sample))) throw new FileNotFoundException("데모 샘플이 없다.");
            if (step.Replay is not null && !Content.Replays.Any(r => r.Id == step.Replay)) throw new InvalidDataException("재생 근거가 없다.");
        }
        foreach (var source in Content.Replays)
            if (!File.Exists(ResolveAsset(source.Evidence)) || source.Sha256.Length != 64 || source.Model != "Gemma 4 E2B Q4_0" || source.Context != 4096)
                throw new InvalidDataException("고정 재생 근거가 유효하지 않다.");
    }
    public void Begin() => Position = 0;
    public void End() => Position = -1;
    public bool Move(int direction)
    {
        if (!Active || direction is not (-1 or 1)) return false;
        var next = Position + direction;
        if (next < 0 || next >= Content.Steps.Length) return false;
        Position = next; return true;
    }
    public int PlannedStartSeconds => Content.Steps.Take(Math.Max(Position, 0)).Sum(s => s.Seconds);
    public string ResolveAsset(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var boundary = Path.Combine(root, "assets") + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || !full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("샘플 폴더 밖의 경로를 거부한다.");
        return full;
    }
    public ReplayView ReadReplay(string id)
    {
        var source = Content.Replays.Single(r => r.Id == id);
        var bytes = File.ReadAllBytes(ResolveAsset(source.Evidence));
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("재생 근거 SHA-256이 다르다. 결과를 표시하지 않는다.");
        using var json = JsonDocument.Parse(bytes); var j = json.RootElement;
        string input, output;
        switch (source.Kind)
        {
            case "receipt":
                var extraction = ImageReader.Parse(j.GetProperty("extraction").GetProperty("Raw").GetString()!);
                input = "가짜 영수증 · Notebook 2개 6,000 / Pen 3개 3,000";
                output = $"합계: {extraction.Total:N0}\n" + extraction.Raw; break;
            case "error": input = "가짜 오류 화면 · 127.0.0.1:8080 연결 거부"; output = j.GetProperty("answer").GetString()!; break;
            case "vault":
                input = j.GetProperty("question").GetString()!; var answer = j.GetProperty("answer");
                output = answer.GetProperty("Answer").GetString() + "\n\n" + string.Join("\n\n", answer.GetProperty("Sources").EnumerateArray()
                    .Select(s => $"출처: {s.GetProperty("File").GetString()} · 문단 {s.GetProperty("Paragraph").GetInt32()}\n{s.GetProperty("Text").GetString()}")); break;
            case "shortcut":
                input = "가상 고객에게 내일 오후 세 시까지 견적서를 보내 주세요.";
                output = string.Join("\n\n", j.GetProperty("outcomes").EnumerateArray().Where(o => o.TryGetProperty("answer", out _))
                    .Select(o => o.GetProperty("action").GetString() + ":\n" + o.GetProperty("answer").GetString()));
                output += "\n\n가짜 입력을 팝업에 직접 전달한 기록이다. 다른 앱의 선택 읽기·전역 단축키 동작을 입증하지 않는다."; break;
            case "limits":
                var item = j[source.Item]; input = item.GetProperty("Prompt").GetString()!;
                output = item.GetProperty("answer").GetString() + "\n\n" + item.GetProperty("Reference").GetString() + "\n" + item.GetProperty("Caution").GetString(); break;
            default: throw new InvalidDataException("지원하지 않는 재생 형식이다.");
        }
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException("재생 결과가 비어 있다.");
        return new(input, output, $"재생 결과 · {source.CapturedAt} 기록\n{source.Model} · 컨텍스트 {source.Context}\n현재 추론·현재 성능 측정이 아니다. AI 결과는 검증이 필요하다.");
    }
}
