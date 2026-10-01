namespace LocalMind.Core;

public record BackendOptions(string Server, string Model, string Projector, int Context = 8192, bool Cpu = false, bool Embedding = false);
public record ImageInput(byte[] Data, string MediaType = "image/png");
public record ChatRequest(string Text, int MaxTokens = 512, double Temperature = 1.0,
    string? SystemPrompt = null, IReadOnlyList<ImageInput>? Images = null);
public record ChatChunk(string Content, bool Completed = false);
public record BackendStats(double? TokensPerSecond, double? FirstTokenSeconds, long TotalTokens,
    string RateSource, int? ProcessId, int Context);

public interface IInferenceBackend
{
    Task StartAsync(BackendOptions options, CancellationToken ct);
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, CancellationToken ct);
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task StopAsync();
    BackendStats GetStats();
}

public static class ProductPaths
{
    // 사용자 승인: 데모 기본값은 메모리 절약용 E2B·4K이다. E4B 정의는 비교/과거 재현용으로 보존한다.
    public static BackendOptions Demo(string root) => Fallback(root);
    public static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "tools", "spike-artifacts.json"))) return folder.FullName;
        throw new DirectoryNotFoundException("모델 목록이 없다. tools, models, third_party 폴더를 앱과 함께 배치해야 한다.");
    }
    public static BackendOptions Primary(string root) => new(
        Path.Combine(root, "third_party/llama-b11146-cuda12/llama-server.exe"),
        Path.Combine(root, "models/google-qat/gemma-4-E4B_q4_0-it.gguf"),
        Path.Combine(root, "models/google-qat/gemma-4-E4B-it-mmproj.gguf"));
    public static BackendOptions Fallback(string root) => new(
        Primary(root).Server,
        Path.Combine(root, "models/fallback/gemma-4-E2B-it-Q4_0.gguf"),
        Path.Combine(root, "models/fallback/mmproj-gemma-4-E2B-it-BF16.gguf"), 4096);
    public static BackendOptions Embedding(string root) => new(Primary(root).Server,
        Path.Combine(root, "models/embedding/embeddinggemma-300M-Q8_0.gguf"), "", 2048, true, true);
}
