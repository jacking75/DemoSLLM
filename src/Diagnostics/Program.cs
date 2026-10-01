using System.Text.Json;
using LocalMind.Core;
using LocalMind.Backend;
using LocalMind.Telemetry;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

var root = ProductPaths.FindRoot();
if (args.Contains("--stage4-self-test")) { Stage4Tests.Run(root); return; }
if (args.Contains("--stage2") || args.Contains("--feature-self-test")) { await Stage2Tests.Run(root, args.Contains("--feature-self-test"), args.Contains("--primary")); return; }
using var nvml = new Nvml();
if (args.Contains("--self-test"))
{
    await ModelStoreTests.Run(root);
    if (WindowsJob.Quote("a b") != "\"a b\"") throw new Exception("인자 인용 실패이다.");
    using var job = new WindowsJob();
    using var child = job.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        ["/c", "ping -n 30 127.0.0.1 > nul"]);
    job.Dispose();
    if (!child.WaitForExit(5000)) throw new Exception("Job 종료 실패이다.");
    Console.WriteLine("Job close 자체 검증 통과이다. 실제 모델 강제 종료는 별도 측정이다.");
    Console.WriteLine(JsonSerializer.Serialize(nvml.Read()));
    Console.WriteLine("진단 프로세스 외부 소켓: " + NetworkMonitor.CountExternalSockets(Environment.ProcessId));
    return;
}
var output = Path.Combine(root, "docs", "stage1-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(output);
var before = nvml.Read();
await using var backend = new LlamaServerBackend();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
await backend.StartAsync(args.Contains("--fallback") ? ProductPaths.Fallback(root) : ProductPaths.Primary(root), timeout.Token);
var started = backend.GetStats();
File.WriteAllText(Path.Combine(output, "started.json"), JsonSerializer.Serialize(new { parentPid = Environment.ProcessId, serverPid = started.ProcessId, before, loaded = nvml.Read(), internet = NetworkMonitor.InternetStatus() }));
Console.WriteLine("측정 폴더: " + output);
if (args.Contains("--hold")) { Console.WriteLine("강제 종료 검증 대기이다."); await Task.Delay(Timeout.Infinite, timeout.Token); return; }
var content = new System.Text.StringBuilder();
int peakSockets = 0;
await foreach (var chunk in backend.StreamChatAsync(new ChatRequest("로컬 AI의 장점을 한국어 한 문장으로 답하라."), timeout.Token))
{
    content.Append(chunk.Content);
    peakSockets = Math.Max(peakSockets, NetworkMonitor.CountExternalSockets(started.ProcessId!.Value));
}
var stats = backend.GetStats();
var during = nvml.Read();
bool canceled = false;
using (var cancel = new CancellationTokenSource())
{
    try
    {
        await foreach (var chunk in backend.StreamChatAsync(new ChatRequest("로컬 AI 업무 활용 사례를 아주 길게 100개 작성하라.", 2048), cancel.Token))
            if (chunk.Content.Length > 0) cancel.Cancel();
    }
    catch (OperationCanceledException) { canceled = true; }
}
var followup = new System.Text.StringBuilder();
await foreach (var chunk in backend.StreamChatAsync(new ChatRequest("1 더하기 1은? 숫자만 답하라."), timeout.Token)) followup.Append(chunk.Content);
await backend.StopAsync();
await Task.Delay(2000);
bool alive;
try { using var process = System.Diagnostics.Process.GetProcessById(started.ProcessId!.Value); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
    before, during, after = nvml.Read(), content = content.ToString(), stats,
    externalSocketsObservedMax = peakSockets, canceled, followup = followup.ToString(), serverAliveAfterStop = alive,
    offlineGate = NetworkMonitor.InternetStatus(), forcedExitGate = "별도 측정 필요"
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(File.ReadAllText(Path.Combine(output, "result.json")));
if (alive || !canceled || content.Length == 0 || followup.Length == 0) throw new Exception("기반 진단 실패이다.");
