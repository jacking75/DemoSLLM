using System.Text.Json;
using LocalMind.Features;

public static class Stage4Tests
{
    public static void Run(string root)
    {
        var output = Path.Combine(root, "docs/stage4-runs", "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output); List<string> passed = [];
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed.Add(name); }
        void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { passed.Add(name); return; } throw new Exception(name); }
        var guide = new DemoGuide(root);
        Check(guide.Content.Steps.Sum(s => s.Seconds) == 420 && guide.Content.Steps.Length == 8, "8개 화면의 총 배정 420초");
        Check(!guide.Move(1), "시작 전 이동 거부"); guide.Begin();
        Check(!guide.Move(-1), "첫 화면 이전 이동 거부");
        List<string> ids = [guide.Current.Id]; while (guide.Move(1)) ids.Add(guide.Current.Id);
        Check(ids.SequenceEqual(new[] { "setup", "receipt", "error", "vault", "shortcut", "review", "limits", "ideas" }) && !guide.Move(1), "전체 진행 순서와 마지막 경계");
        Check(guide.PlannedStartSeconds == 375 && guide.Move(-1) && guide.Current.Id == "limits", "이전 단계와 예정 시간 복원");
        guide.End(); Check(!guide.Active && !guide.Move(1), "종료 후 이동 거부");
        var receipt = guide.ReadReplay("receipt");
        Check(receipt.Output.Contains("9,000") && receipt.Provenance.Contains("재생 결과") && receipt.Provenance.Contains("현재 추론·현재 성능 측정이 아니다"), "영수증 원본과 재생 표시");
        var vault = guide.ReadReplay("vault");
        Check(vault.Output.Contains("출장규정.md") && vault.Output.Contains("35,000원"), "문서 기록의 인용 파일과 원문 표시");
        Check(guide.ReadReplay("error").Output.Contains("Connection refused"), "오류 화면 실제 저장 응답");
        Check(guide.ReadReplay("shortcut").Output.Contains("입증하지 않는다") && guide.ReadReplay("shortcut").Output.Contains("Please send"), "팝업 기록의 검증 범위 표시");
        Check(guide.ReadReplay("limits").Output.Contains("552,781") && guide.ReadReplay("limits").Output.Contains("5,472,661"), "실제 과거 오답과 검산값을 함께 표시");
        Reject(() => guide.ResolveAsset("../구현명세서.md"), "샘플 경계 밖 읽기 거부");
        var fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(Path.Combine(fixture, "assets/demo/evidence")); Directory.CreateDirectory(Path.Combine(fixture, "assets/samples/images"));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "assets/demo/evidence"))) File.Copy(path, Path.Combine(fixture, "assets/demo/evidence", Path.GetFileName(path)));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "assets/samples/images"))) File.Copy(path, Path.Combine(fixture, "assets/samples/images", Path.GetFileName(path)));
        var config = Path.Combine(fixture, "assets/demo/guide.json");
        var json = File.ReadAllText(Path.Combine(root, "assets/demo/guide.json")); File.WriteAllText(config, json);
        var altered = new DemoGuide(fixture); File.AppendAllText(altered.ResolveAsset("assets/demo/evidence/receipt.json"), " ");
        Reject(() => altered.ReadReplay("receipt"), "변조된 재생 근거 표시 거부");
        File.WriteAllText(config, JsonSerializer.Serialize(guide.Content with { Steps = guide.Content.Steps.Select((s, i) => i == 0 ? s with { Seconds = 41 } : s).ToArray() }));
        Reject(() => new DemoGuide(fixture), "7분을 벗어난 진행표 거부");
        File.WriteAllText(config, JsonSerializer.Serialize(guide.Content with { Steps = guide.Content.Steps.Select((s, i) => i == 1 ? s with { Sample = "../../outside.png" } : s).ToArray() }));
        Reject(() => new DemoGuide(fixture), "구성 파일의 외부 샘플 경로 거부");
        File.WriteAllText(config, JsonSerializer.Serialize(guide.Content with { Replays = guide.Content.Replays.Concat([guide.Content.Replays[0]]).ToArray() }));
        Reject(() => new DemoGuide(fixture), "중복 재생 ID 거부");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed, count = passed.Count, gate = "자동 회귀. 초심자 7분 진행·실제 전역키·물리 단절은 별도 미확인" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"4단계 회귀 {passed.Count}/{passed.Count} 통과: {output}");
    }
}
