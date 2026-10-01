using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalMind.Core;

public record ModelArtifact(string Name, string Path, string Url, long Size, string Sha256);

public sealed class ModelStore(string root, Func<HttpClient>? clientFactory = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public IReadOnlyList<ModelArtifact> ReadManifest() => JsonSerializer.Deserialize<ModelArtifact[]>(
        File.ReadAllText(System.IO.Path.Combine(root, "tools/spike-artifacts.json")), Json)!;
    public string Resolve(ModelArtifact artifact)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, artifact.Path));
        var modelRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "models")) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("모델 폴더 밖 쓰기를 거부한다.");
        return full;
    }
    public async Task<bool> VerifyAsync(ModelArtifact artifact, CancellationToken ct)
    {
        var path = Resolve(artifact);
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Size) return false;
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    // Only called after explicit download action, never during inference or auto-start.
    public async Task DownloadAsync(ModelArtifact artifact, IProgress<long>? progress, CancellationToken ct)
    {
        var url = new Uri(artifact.Url);
        if (url.Scheme != "https" || url.Host != "huggingface.co") throw new InvalidDataException("허용하지 않는 다운로드 주소이다.");
        var path = Resolve(artifact);
        if (File.Exists(path)) throw new IOException("기존 파일을 자동 교체하지 않는다. 먼저 무결성을 확인해야 한다.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > artifact.Size) throw new InvalidDataException("부분 파일 크기가 예상값보다 크다.");
        if (offset < artifact.Size)
        {
            using var client = clientFactory?.Invoke() ?? new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset))
                throw new InvalidDataException("서버가 이어받기를 지원하지 않는다. 부분 파일은 보존한다.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.None, 65536, true);
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) > 0)
            {
                offset += count;
                if (offset > artifact.Size) throw new InvalidDataException("다운로드 크기 초과이다.");
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                progress?.Report(offset);
            }
        }
        await using (var check = File.OpenRead(partial))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            if (check.Length != artifact.Size || !actual.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 또는 크기 불일치이다. 부분 파일을 보존한다.");
        }
        File.Move(partial, path, overwrite: false);
    }
}
