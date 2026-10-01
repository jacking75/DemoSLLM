using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using LocalMind.Core;

namespace LocalMind.Backend;

public sealed class LlamaServerBackend : IInferenceBackend, IAsyncDisposable
{
    private WindowsJob? job;
    private Process? process;
    private HttpClient? client;
    private BackendStats stats = new(null, null, 0, "미확인", null, 8192);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim requests = new(1, 1);
    private CancellationTokenSource? lifetime;
    private bool embeddingMode;
    public BackendStats GetStats() => Volatile.Read(ref stats);

    public async Task StartAsync(BackendOptions options, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (process is not null) throw new InvalidOperationException("서버가 이미 실행 중이다.");
            foreach (var file in options.Embedding ? new[] { options.Server, options.Model } : new[] { options.Server, options.Model, options.Projector })
                if (!File.Exists(file)) throw new FileNotFoundException("필수 파일이 없다. 다운로드/사전 복사가 필요하다.", file);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            job = new WindowsJob(); lifetime = new CancellationTokenSource();
            embeddingMode = options.Embedding;
            List<string> arguments = ["-m", options.Model,
                "-c", options.Context.ToString(), "-np", "1", "-ngl", options.Cpu ? "0" : "99",
                "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", key,
                "--no-webui"];
            if (options.Embedding) arguments.AddRange(["--embedding", "--pooling", "mean"]);
            else arguments.AddRange(["--mmproj", options.Projector, "--jinja", "--image-max-tokens", "1120"]);
            process = job.Start(options.Server, arguments, options.Cpu ? new Dictionary<string, string> { ["CUDA_VISIBLE_DEVICES"] = "" } : null);
            client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // 요청 사이에 닫힌 keep-alive 연결을 재사용하지 않도록 루프백 연결을 요청마다 종료한다.
            client.DefaultRequestHeaders.ConnectionClose = true;
            Volatile.Write(ref stats, new(null, null, 0, "미확인", process.Id, options.Context));
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                ready.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("추론 서버가 준비 전에 종료됐다.");
                try
                {
                    using var response = await client.GetAsync("/health", ready.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, ready.Token);
            }
        }
        catch { await StopInternalAsync(); throw; }
        finally { lifecycle.Release(); }
    }

    public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await requests.WaitAsync(ct);
        try
        {
            var http = client ?? throw new InvalidOperationException("서버를 먼저 시작해야 한다.");
            if (embeddingMode) throw new InvalidOperationException("임베딩 서버에 채팅을 요청할 수 없다.");
            var userContent = BuildContent(request);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime!.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var token = deadline.Token;
            var watch = Stopwatch.StartNew();
            Volatile.Write(ref stats, GetStats() with { TokensPerSecond = null, FirstTokenSeconds = null, RateSource = "측정 중" });
            var output = new System.Text.StringBuilder();
            double? ttft = null, rate = null; long tokens = 0;
            using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = JsonContent.Create(new {
                    messages = new[] {
                        new { role = "system", content = (object)(request.SystemPrompt ?? "한국어로 간결하게 답한다. 절대적인 보안 보장이나 확인하지 않은 사실을 주장하지 않는다. AI 결과는 검증이 필요하다.") },
                        new { role = "user", content = userContent } },
                    temperature = request.Temperature, top_p = 0.95, top_k = 64,
                    max_tokens = request.MaxTokens, stream = true,
                    stream_options = new { include_usage = true }, chat_template_kwargs = new { enable_thinking = false }
                })
            };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);
            bool done = false, truncated = false;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") { done = true; break; }
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out _)) throw new IOException("스트리밍 서버 오류이다.");
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length") truncated = true;
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString()!;
                        if (text.Length > 0)
                        {
                            ttft ??= watch.Elapsed.TotalSeconds; output.Append(text);
                            Volatile.Write(ref stats, GetStats() with { FirstTokenSeconds = ttft });
                            yield return new(text);
                        }
                    }
                }
                if (root.TryGetProperty("timings", out var timing) && timing.TryGetProperty("predicted_n", out var n) && timing.TryGetProperty("predicted_ms", out var ms) && ms.GetDouble() > 0)
                    rate = n.GetDouble() / (ms.GetDouble() / 1000.0);
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var total)) tokens = total.GetInt64();
            }
            var elapsed = watch.Elapsed.TotalSeconds;
            if (!done || output.Length == 0) throw new IOException("응답이 중단됐거나 비어 있다.");
            var source = rate.HasValue ? "서버 생성" : "미확인";
            if (!rate.HasValue)
            {
                try
                {
                    using var tokenize = await http.PostAsJsonAsync("/tokenize", new { content = output.ToString(), add_special = false, parse_special = false }, token);
                    tokenize.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(await tokenize.Content.ReadAsStringAsync(token));
                    var count = doc.RootElement.GetProperty("tokens").GetArrayLength();
                    if (count > 0 && elapsed > 0) { rate = count / elapsed; source = "재토큰화 추정치 (TTFT 포함)"; }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException) { }
            }
            Volatile.Write(ref stats, GetStats() with { TokensPerSecond = rate, FirstTokenSeconds = ttft,
                TotalTokens = GetStats().TotalTokens + tokens, RateSource = source });
            if (truncated) throw new IOException("출력 한도로 응답이 잘렸다. 부분 응답이며 완료 결과가 아니다.");
            yield return new("", true);
        }
        finally { requests.Release(); }
    }

    public static object BuildContent(ChatRequest request)
    {
        if (request.Images is not { Count: > 0 }) return request.Text;
        List<object> parts = [];
        foreach (var image in request.Images)
        {
            if (image.Data.Length is 0 or > 16_777_216 || image.MediaType is not ("image/png" or "image/jpeg")) throw new InvalidDataException("PNG/JPEG 이미지는 16MiB 이하이어야 한다.");
            parts.Add(new { type = "image_url", image_url = new { url = "data:" + image.MediaType + ";base64," + Convert.ToBase64String(image.Data) } });
        }
        parts.Add(new { type = "text", text = request.Text });
        return parts.ToArray();
    }
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        await requests.WaitAsync(ct);
        try
        {
            var http = client ?? throw new InvalidOperationException("임베딩 서버가 시작되지 않았다.");
            if (!embeddingMode) throw new InvalidOperationException("CPU 임베딩 서버에만 임베딩을 요청한다.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime!.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            using var tokenize = await http.PostAsJsonAsync("/tokenize", new { content = text, add_special = true }, deadline.Token);
            tokenize.EnsureSuccessStatusCode();
            using var tokens = JsonDocument.Parse(await tokenize.Content.ReadAsStringAsync(deadline.Token));
            if (tokens.RootElement.GetProperty("tokens").GetArrayLength() > 1900) throw new InvalidDataException("임베딩 입력이 너무 길다. 청크를 줄여야 한다.");
            using var response = await http.PostAsJsonAsync("/v1/embeddings", new { input = text, model = "local" }, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var vector = doc.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (vector.Length == 0 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("임베딩 값이 유효하지 않다.");
            return vector;
        }
        finally { requests.Release(); }
    }
    private async Task StopInternalAsync()
    {
        lifetime?.Cancel();
        job?.Dispose(); job = null;
        if (process is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); } finally { process.Dispose(); process = null; }
        }
        client?.Dispose(); client = null; lifetime?.Dispose(); lifetime = null;
        Volatile.Write(ref stats, GetStats() with { ProcessId = null });
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopInternalAsync(); } finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
