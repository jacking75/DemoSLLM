using System.Diagnostics;
using System.Text.Json;
using LocalMind.Backend;
using LocalMind.Core;
using LocalMind.Features;
using LocalMind.Telemetry;

public static class Stage2Tests
{
    public static async Task Run(string root, bool selfTest, bool primary = false)
    {
        if (selfTest) { await FeatureTests.Run(root); return; }
        var options = primary ? ProductPaths.Primary(root) : ProductPaths.Demo(root);
        var folder = Path.Combine(root, "docs/stage2-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(folder);
        Console.WriteLine("실측 폴더: " + folder);
        using var nvml = new Nvml(); var before = nvml.Read();
        ulong peak = before.UsedBytes; int sockets = 0, samples = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var monitorLife = new CancellationTokenSource();
        await using var inference = new LlamaServerBackend(); await using var embedding = new LlamaServerBackend();
        var monitor = Task.Run(async () => { while (!monitorLife.IsCancellationRequested) { peak = Math.Max(peak, nvml.Read().UsedBytes); samples++; foreach (var backend in new[] { inference, embedding }) if (backend.GetStats().ProcessId is int pid) sockets = Math.Max(sockets, NetworkMonitor.CountExternalSockets(pid)); try { await Task.Delay(50, monitorLife.Token); } catch (OperationCanceledException) { break; } } });
        List<object> imageResults = [], questionResults = [], limitResults = [];
        int? inferencePid = null, embeddingPid = null; int correctSources = 0; bool imagesPass = true; bool absentPass = false; string? error = null; int count = 0;
        void Save(string file, object data) => File.WriteAllText(Path.Combine(folder, file), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await inference.StartAsync(options, timeout.Token); inferencePid = inference.GetStats().ProcessId;
            await embedding.StartAsync(ProductPaths.Embedding(root), timeout.Token); embeddingPid = embedding.GetStats().ProcessId;
            Save("started.json", new { options, inferencePid, embeddingPid, before, loaded = nvml.Read(), internet = NetworkMonitor.InternetStatus(), offlineTest = "사용자 면제 / 동작 미확인" });
            foreach (var name in new[] { "receipt", "error", "table" })
            {
                var image = new ImageInput(File.ReadAllBytes(Path.Combine(root, "assets/samples/images", name + ".png")));
                try
                {
                    if (name == "error") { var answer = await new ImageReader(inference).ReadAsync(image, true, timeout.Token); var result = new { name, answer, stats = inference.GetStats(), passed = answer.Length > 0 }; imageResults.Add(result); Save("image-" + name + ".json", result); }
                    else { var extraction = await new ImageReader(inference).ExtractAsync(image, timeout.Token); var passed = name != "receipt" || extraction.Total == 9000; imagesPass &= passed; var result = new { name, extraction, stats = inference.GetStats(), passed }; imageResults.Add(result); Save("image-" + name + ".json", result); }
                    Console.WriteLine("A 측정 완료: " + name);
                }
                catch (Exception ex) { imagesPass = false; imageResults.Add(new { name, error = ex.Message, passed = false }); Console.WriteLine("A 실패: " + name + " " + ex.Message); }
            }
            var vault = new DocumentVault(embedding, inference, Path.Combine(folder, "vault.sqlite"));
            count = await vault.IndexAsync(Path.Combine(root, "assets/documents"), null, timeout.Token);
            using var questions = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/vault-questions.json")));
            var index = 0;
            foreach (var q in questions.RootElement.EnumerateArray())
            {
                index++; var question = q.GetProperty("question").GetString()!; var expected = q.GetProperty("file").GetString()!; var fact = q.GetProperty("fact").GetString()!;
                try
                {
                    var answer = await vault.AnswerAsync(question, timeout.Token);
                    var sourceCorrect = answer.Sources.Count > 0 && answer.Sources.All(s => s.File == expected) && answer.Sources.All(s => DocumentVault.ReadParagraphs(Path.Combine(root, "assets/documents", s.File)).Count >= s.Paragraph);
                    if (sourceCorrect) correctSources++;
                    var result = new { question, expectedFile = expected, expectedFact = fact, answer, sourceCorrect, expectedFactPresent = answer.Answer.Replace(",", "").Contains(fact), stats = inference.GetStats() };
                    questionResults.Add(result); Save($"question-{index:00}.json", result);
                    Console.WriteLine($"B {index}/10: 출처 {(sourceCorrect ? "정답" : "오답")}");
                }
                catch (Exception ex) { var failure = new { question, error = ex.ToString(), raw = (ex as VaultResponseException)?.Raw, sourceCorrect = false }; questionResults.Add(failure); Save($"question-{index:00}.json", failure); Console.WriteLine($"B {index}/10 실패: {ex.Message}"); }
            }
            var absent = await vault.AnswerAsync("사내 달 탐사 출장의 우주복 구매 한도는?", timeout.Token); absentPass = absent.Answer == DocumentVault.NotFound && absent.Sources.Count == 0; Save("absent.json", new { absent, absentPass });
            foreach (var item in LimitsDemo.Examples)
            {
                var answer = await ImageReader.CompleteAsync(inference, new(item.Prompt, 1024), timeout.Token);
                var result = new { item.Name, item.Prompt, item.Reference, answer, item.Caution, stats = inference.GetStats() }; limitResults.Add(result); Save("limits.json", limitResults); Console.WriteLine("F 측정 완료: " + item.Name);
            }
        }
        catch (Exception ex) { error = ex.ToString(); Console.WriteLine(error); }
        finally
        {
            await Task.WhenAll(inference.StopAsync(), embedding.StopAsync()); monitorLife.Cancel(); await monitor; await Task.Delay(1500);
            var serverAlive = new[] { inferencePid, embeddingPid }.Where(p => p.HasValue).Any(p => Alive(p!.Value));
            Save("result.json", new { options, before, after = nvml.Read(), peakBytes = peak, peakGiB = peak / Math.Pow(1024, 3), samples, intervalMilliseconds = 50, externalSocketsObservedMax = sockets, inferencePid, embeddingPid, serverAliveAfterStop = serverAlive, imagesPass, correctSources, totalQuestions = 10, absentPass, chunkCount = count, images = imageResults, questions = questionResults, limits = limitResults, error, offlineTest = "사용자 면제 / 동작 미확인", serviceGate = error is null && imagesPass && correctSources >= 8 && absentPass && peak <= 7_516_192_768 && sockets == 0 && !serverAlive && limitResults.Count == 3, guiGate = "별도 측정 필요" });
        }
        Console.WriteLine(File.ReadAllText(Path.Combine(folder, "result.json")));
    }
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
