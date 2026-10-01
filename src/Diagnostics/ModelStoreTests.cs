using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LocalMind.Core;

internal static class ModelStoreTests
{
    public static async Task Run(string root)
    {
        var fixture = Path.Combine(root, "docs/stage1-runs", "model-store-test-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(fixture, "models"));
        byte[] data = [10, 20, 30, 40, 50];
        var artifact = new ModelArtifact("fixture", "models/fixture.gguf", "https://huggingface.co/test/fixture", 5,
            Convert.ToHexString(SHA256.HashData(data)));
        await File.WriteAllBytesAsync(Path.Combine(fixture, "models/fixture.gguf.part"), data[..2]);
        var handler = new FixtureHandler(data);
        var store = new ModelStore(fixture, () => new HttpClient(handler));
        await store.DownloadAsync(artifact, null, CancellationToken.None);
        if (handler.RangeStart != 2 || !await store.VerifyAsync(artifact, CancellationToken.None)) throw new Exception("이어받기/해시 실패이다.");
        if (await store.VerifyAsync(artifact with { Sha256 = new string('0', 64) }, CancellationToken.None)) throw new Exception("잘못된 해시 통과이다.");
        bool refused = false;
        try { store.Resolve(artifact with { Path = "../outside.gguf" }); } catch (InvalidDataException) { refused = true; }
        if (!refused) throw new Exception("폴더 범위 검증 실패이다.");
        refused = false;
        try { await store.DownloadAsync(artifact, null, CancellationToken.None); } catch (IOException) { refused = true; }
        if (!refused) throw new Exception("기존 파일 보호 실패이다.");
        Console.WriteLine("모델 저장소 모의 검증: Range 이어받기·SHA-256·경로 범위·기존 파일 보호 통과이다. fixture=" + fixture);
    }
    private sealed class FixtureHandler(byte[] data) : HttpMessageHandler
    {
        public long? RangeStart { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RangeStart = request.Headers.Range?.Ranges.Single().From;
            var offset = (int)(RangeStart ?? 0);
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { Content = new ByteArrayContent(data[offset..]) };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }
}
