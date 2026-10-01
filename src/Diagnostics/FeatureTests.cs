using System.Runtime.CompilerServices;
using System.Text.Json;
using LocalMind.Backend;
using LocalMind.Core;
using LocalMind.Features;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

public static class FeatureTests
{
    public static async Task Run(string root)
    {
        var folder = Path.Combine(root, "docs/stage2-runs", "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(folder);
        List<string> passed = [];
        void Check(bool condition, string name) { if (!condition) throw new Exception(name + " 실패이다."); passed.Add(name); }
        var request = new ChatRequest("text", Images: [new([1, 2, 3])]);
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(LlamaServerBackend.BuildContent(request)))) Check(json.RootElement[0].GetProperty("type").GetString() == "image_url" && json.RootElement[1].GetProperty("type").GetString() == "text", "이미지가 텍스트보다 앞선다");
        try { LlamaServerBackend.BuildContent(new("", Images: [new([])])); throw new Exception("빈 이미지가 통과했다."); } catch (InvalidDataException) { passed.Add("빈 이미지 거부"); }
        var fake = new FakeBackend();
        const string valid = "{\"kind\":\"receipt\",\"total\":9000,\"rows\":[{\"name\":\"=cmd\",\"quantity\":2,\"amount\":9000}]}";
        fake.Outputs.Enqueue("bad"); fake.Outputs.Enqueue("{}"); fake.Outputs.Enqueue(valid);
        var extraction = await new ImageReader(fake).ExtractAsync(new([1]), default); Check(extraction.Attempts == 3 && fake.ChatCalls == 3 && extraction.Total == 9000, "JSON 최대 2회 재시도 성공");
        Check(ImageReader.Csv(extraction).Contains("'="), "CSV 수식 주입 차단");
        fake.ChatCalls = 0; fake.Outputs.Enqueue("bad"); fake.Outputs.Enqueue("bad"); fake.Outputs.Enqueue("bad");
        try { await new ImageReader(fake).ExtractAsync(new([1]), default); throw new Exception("잘못된 JSON이 통과했다."); } catch (InvalidDataException) { Check(fake.ChatCalls == 3, "JSON 세 번 실패 후 중단"); }
        var longText = new string('가', 1900); var chunks = DocumentVault.Chunk([longText]).ToArray();
        Check(chunks.Length == 3 && chunks.All(c => c.Text.Length <= 800) && chunks[0].Text[^100..] == chunks[1].Text[..100], "800자 상한과 100자 중첩");
        Check(DocumentVault.Cosine([1, 0], [1, 0]) == 1 && DocumentVault.Cosine([1, 0], [0, 1]) == 0, "코사인 유사도");
        var samples = Path.Combine(root, "assets/documents");
        var vault = new DocumentVault(fake, fake, Path.Combine(folder, "vault.sqlite"));
        var count = await vault.IndexAsync(samples, null, default); Check(count == 5, "가짜 문서 5개 MD 색인");
        var search = await vault.SearchAsync("규정", default); Check(search.Count == 5 && search.All(s => s.Text.Length > 0 && s.File.EndsWith(".md")), "SQLite 저장 재조회와 출처");
        using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); try { await vault.IndexAsync(samples, null, cancel.Token); throw new Exception("취소를 무시했다."); } catch (OperationCanceledException) { Check((await vault.SearchAsync("규정", default)).Count == 5, "색인 취소 시 기존 색인 유지"); } }
        fake.Outputs.Enqueue("{\"supported\":true}"); fake.Outputs.Enqueue("{\"answer\":\"없는 자료\",\"citations\":[99]}");
        try { await vault.AnswerAsync("규정", default); throw new Exception("없는 출처를 허용했다."); } catch (VaultResponseException ex) { Check(ex.Raw.Contains("99") && ex.InnerException is InvalidDataException, "허위 출처 번호 거부 및 실패 원문 보존"); }
        fake.Outputs.Enqueue("{\"supported\":true}"); fake.Outputs.Enqueue("{\"answer\":\"문서에서 찾지 못했습니다\",\"citations\":[]}"); Check((await vault.AnswerAsync("규정", default)).Sources.Count == 0, "근거 없음 출처 비움");
        var txtFolder = Path.Combine(folder, "txt"); Directory.CreateDirectory(txtFolder); var txt = Path.Combine(txtFolder, "가짜.txt"); File.WriteAllText(txt, "가짜 규정\n\n두 번째 문단"); Check(DocumentVault.ReadParagraphs(txt).Count == 2, "UTF-8 TXT 문단 읽기");
        try { ImageReader.Parse("{\"kind\":\"receipt\",\"total\":\"9000\",\"rows\":[]}"); throw new Exception("잘못된 스키마를 허용했다."); } catch (InvalidDataException) { passed.Add("숫자 타입/빈 행 거부"); }
        var sources = new[] { new SourceHit(1, "가짜.md", 1, "가짜 근거", 1) };
        const string answerJson = "{\"answer\":\"가짜 답변\",\"citations\":[1]}";
        foreach (var raw in new[] { answerJson, "```json\n" + answerJson + "\n```", "```\r\n" + answerJson + "\r\n```", " \n```JSON\n" + answerJson + "\n```\n " })
            Check(DocumentVault.ParseAnswer(raw, sources).Sources.Single().Id == 1 && DocumentVault.ParseAnswer(raw, sources).Raw == raw, "단일 JSON 정규화와 원문 보존 " + passed.Count);
        foreach (var raw in new[] { "설명\n```json\n" + answerJson + "\n```", "```json\n" + answerJson + "\n```\n설명", "```json\n" + answerJson, "```python\n" + answerJson + "\n```", "```json\n" + answerJson + "\n```\n```json\n{}\n```", answerJson + "{}", "{\"answer\":\"가짜\",\"citations\":[\"1\"]}", "{\"answer\":\"가짜\",\"citations\":[99]}", "{\"answer\":\"가짜\",\"answer\":\"중복\",\"citations\":[1]}", "[]" })
        {
            bool rejected = false;
            try { DocumentVault.ParseAnswer(raw, sources); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException) { rejected = true; }
            Check(rejected, "설명·부분 JSON·다중 블록·허위/문자열 출처·중복 키 거부 " + passed.Count);
        }
        fake.Outputs.Enqueue("{\"supported\":true}"); fake.Outputs.Enqueue("```json\n잘못된 JSON\n```");
        try { await vault.AnswerAsync("규정", default); throw new Exception("잘못된 JSON을 허용했다."); } catch (VaultResponseException ex) { Check(ex.Raw == "```json\n잘못된 JSON\n```", "파싱 실패 원문 보존"); }
        fake.Outputs.Enqueue("{\"supported\":false}"); var beforeCalls = fake.ChatCalls;
        Check((await vault.AnswerAsync("없는 규정", default)).Answer == DocumentVault.NotFound && fake.ChatCalls == beforeCalls + 1, "근거 없는 판별 후 답변 생성 차단");
        fake.Outputs.Enqueue("{\"supported\":\"false\"}");
        try { await vault.AnswerAsync("없는 규정", default); throw new Exception("문자열 판별을 허용했다."); } catch (VaultResponseException) { passed.Add("근거 판별 Boolean 타입 검증"); }
        // Parser acceptance fixtures, not documents supplied to end users. No production vault is touched.
        var formats = Path.Combine(folder, "formats"); Directory.CreateDirectory(formats);
        var docxPath = Path.Combine(formats, "한국어.docx");
        using (var doc = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("가상 출장 규정"))), new Paragraph(new Run(new Text("식비 한도는 30000원이다.")))));
            main.Document.Save();
            Check(!new OpenXmlValidator().Validate(doc).Any(), "DOCX fixture OOXML 유효성");
        }
        Check(DocumentVault.ReadParagraphs(docxPath).SequenceEqual(new[] { "가상 출장 규정", "식비 한도는 30000원이다." }), "DOCX 한국어 두 문단 정확한 읽기");
        var pdfPath = Path.Combine(formats, "한국어.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "malgun.ttf")));
        builder.AddPage(612, 792).AddText("가상 출장 식비 한도 30000원", 12, new PdfPoint(72, 720), font);
        File.WriteAllBytes(pdfPath, builder.Build());
        Check(string.Join(" ", DocumentVault.ReadParagraphs(pdfPath)).Contains("가상 출장 식비 한도 30000원"), "텍스트 PDF 한국어와 숫자 정확한 읽기");
        var rejectFolder = Path.Combine(folder, "rejected-formats"); Directory.CreateDirectory(rejectFolder);
        var scanned = new PdfDocumentBuilder();
        scanned.AddPage(612, 792).AddPng(File.ReadAllBytes(Path.Combine(root, "assets/samples/images/receipt.png")), new PdfRectangle(72, 400, 400, 700));
        var scannedPath = Path.Combine(rejectFolder, "scan.pdf"); File.WriteAllBytes(scannedPath, scanned.Build());
        try { DocumentVault.ReadParagraphs(scannedPath); throw new Exception("스캔 PDF가 통과했다."); } catch (InvalidDataException ex) { Check(ex.Message.Contains("스캔 PDF"), "이미지만 있는 스캔 PDF 명시 거부"); }
        var formatsVaultPath = Path.Combine(folder, "formats.sqlite");
        var formatsVault = new DocumentVault(fake, fake, formatsVaultPath);
        Check(await formatsVault.IndexAsync(formats, null, default) == 2, "PDF와 DOCX 함께 색인");
        var reopened = new DocumentVault(fake, fake, formatsVaultPath);
        Check((await reopened.SearchAsync("식비", default)).Select(h => h.File).Order().SequenceEqual(new[] { "한국어.docx", "한국어.pdf" }), "다른 저장소 인스턴스로 PDF/DOCX SQLite 재조회");
        try { await reopened.IndexAsync(rejectFolder, null, default); throw new Exception("스캔 PDF 색인이 통과했다."); } catch (InvalidDataException) { Check((await reopened.SearchAsync("식비", default)).Count == 2, "PDF 읽기 실패 시 기존 SQLite 색인 보존"); }
        var badUtf8 = Path.Combine(rejectFolder, "invalid.txt"); File.WriteAllBytes(badUtf8, [0xC3, 0x28]);
        try { DocumentVault.ReadParagraphs(badUtf8); throw new Exception("잘못된 UTF-8이 통과했다."); } catch (System.Text.DecoderFallbackException) { passed.Add("TXT 잘못된 UTF-8 거부"); }
        var mapped = DocumentVault.Chunk([new string('가', 800), new string('나', 900)]).ToArray();
        Check(mapped[1].Paragraph == 1 && mapped[^1].Paragraph == 2, "중첩 청크 시작 원문 문단 번호 유지");
        var result = new { passed, total = passed.Count, positivePdfDocx = "한국어 정상 파일 읽기 및 SQLite 통과", documentVisualLayout = "전용 문서 스킬 의존성 로더 부재로 미확인, 사용자 문서 납품 아님", time = DateTimeOffset.Now };
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(result));
    }
    private sealed class FakeBackend : IInferenceBackend
    {
        public Queue<string> Outputs { get; } = new(); public int ChatCalls;
        public Task StartAsync(BackendOptions options, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public BackendStats GetStats() => new(null, null, 0, "모의", null, 2048);
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new float[] { 1, 0, 0 }); }
        public async IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct) { ct.ThrowIfCancellationRequested(); ChatCalls++; await Task.Yield(); yield return new(Outputs.Dequeue()); yield return new("", true); }
    }
}
