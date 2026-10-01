using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LocalMind.Backend;
using LocalMind.Core;
using LocalMind.Features;

namespace LocalMind.Wpf;

public partial class MainWindow
{
    private readonly LlamaServerBackend embeddingBackend = new();
    private ImageInput? selectedImage;
    private ImageExtraction? extraction;
    private Image? preview;
    private TextBox? featureResult;
    private TextBox? vaultQuestion;
    private DataGrid? extractedGrid;
    private DocumentVault Vault => new(embeddingBackend, backend,
        guide?.Active == true ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMindStudio", "demo-vault.sqlite") :
        Environment.GetCommandLineArgs().Contains("--diagnostics")
            ? Path.Combine(root, "docs/stage2-runs", $"gui-vault-{Environment.ProcessId}.sqlite")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalMindStudio", "vault.sqlite"));
    private Button FeatureButton(string title, string id, Action action)
    {
        var button = new Button { Content = title };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => { if (requestLife is null) action(); else Status.Text = "작업을 완료하거나 취소한 뒤 사용해야 한다."; };
        return button;
    }
    private TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = LargeText.IsChecked == true ? 18 : 15, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 8, 0, 12) };
    private void ShowFeature(string name)
    {
        if (requestLife is not null) { Status.Text = "작업 중에는 화면을 전환하지 않는다."; return; }
        var chat = name == "로컬 채팅";
        FeatureHost.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
        Answer.Visibility = Input.Visibility = SendButton.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        MainTitle.Text = chat ? "외부 AI API 없이 대화한다" : name;
        if (chat) { Status.Text = "로컬 채팅 화면이다."; return; }
        var panel = new StackPanel(); FeatureHost.Content = panel;
        featureResult = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontSize = LargeText.IsChecked == true ? 25 : 20, MinHeight = 140, MaxHeight = 320, Margin = new Thickness(0, 10, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(featureResult, "FeatureResult");
        if (name.StartsWith('A'))
        {
            panel.AllowDrop = true;
            panel.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            panel.Drop += (_, e) => { if (requestLife is null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) TryLoadImage(files[0]); };
            panel.Children.Add(Note("민감한 화면을 외부 AI로 보내지 않고 읽는다. PNG/JPEG 드롭 · 16MiB 이하. 수치는 원본과 대조해야 한다."));
            var files = new WrapPanel(); panel.Children.Add(files);
            files.Children.Add(FeatureButton("이미지 선택", "ImageOpen", () => { var dialog = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg" }; if (dialog.ShowDialog(this) == true) TryLoadImage(dialog.FileName); }));
            files.Children.Add(FeatureButton("붙여넣기", "ImagePaste", () => { try { SetImage(Encode(Clipboard.GetImage() ?? throw new IOException("클립보드에 이미지가 없다."))); } catch (Exception ex) { Status.Text = ex.Message; } }));
            files.Children.Add(FeatureButton("영역 캡처", "ImageCapture", () => { var capture = new RegionCapture { Owner = this }; if (capture.ShowDialog() == true && capture.Result is { } image) SetImage(image); }));
            var samples = new WrapPanel(); panel.Children.Add(samples);
            foreach (var sample in new[] { "receipt", "error", "table" }) { var s = sample; samples.Children.Add(FeatureButton(s == "receipt" ? "가짜 영수증" : s == "error" ? "가짜 오류" : "가짜 표", "Sample-" + s, () => TryLoadImage(Path.Combine(root, "assets/samples/images", s + ".png")))); }
            preview = new Image { Height = 145, Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(preview);
            if (selectedImage is not null) ShowPreview(selectedImage);
            var modes = new WrapPanel(); panel.Children.Add(modes);
            modes.Children.Add(FeatureButton("설명", "ImageDescribe", () => RunFeature(async ct => featureResult.Text = await new ImageReader(backend).ReadAsync(RequireImage(), false, ct))));
            modes.Children.Add(FeatureButton("오류 진단", "ImageDiagnose", () => RunFeature(async ct => featureResult.Text = await new ImageReader(backend).ReadAsync(RequireImage(), true, ct))));
            modes.Children.Add(FeatureButton("표·영수증 추출", "ImageExtract", () => RunFeature(async ct => { extraction = await new ImageReader(backend).ExtractAsync(RequireImage(), ct); extractedGrid!.ItemsSource = extraction.Rows; featureResult.Text = $"합계: {extraction.Total?.ToString("N0") ?? "없음"}\nJSON 형식 검증 완료 ({extraction.Attempts}회 시도). 내용 정확성은 원본과 대조해야 한다.\n" + extraction.Raw; })));
            modes.Children.Add(FeatureButton("CSV 저장", "ImageCsv", SaveCsv));
            panel.Children.Add(featureResult);
            extractedGrid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, MaxHeight = 220, Background = Brushes.White, Foreground = Brushes.Black };
            System.Windows.Automation.AutomationProperties.SetAutomationId(extractedGrid, "ExtractedGrid");
            var cellText = new Style(typeof(TextBlock)); cellText.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black)); cellText.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16.0));
            foreach (var column in new[] { ("항목", "Name"), ("수량/첫 열", "Quantity"), ("금액/둘째 열", "Amount") }) extractedGrid.Columns.Add(new DataGridTextColumn { Header = new TextBlock { Text = column.Item1, Foreground = Brushes.Black, FontSize = 16 }, Binding = new System.Windows.Data.Binding(column.Item2), ElementStyle = cellText, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            panel.Children.Add(extractedGrid);
            panel.Children.Add(Note(IdeaText("A")));
        }
        else if (name.StartsWith('B'))
        {
            panel.AllowDrop = true;
            panel.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            panel.Drop += (_, e) => { if (requestLife is null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && Directory.Exists(files[0])) IndexFolder(files[0]); };
            panel.Children.Add(Note("폴더 드롭으로 로컬 색인을 교체한다. TXT/MD/PDF/DOCX · 최상위 100개/20MiB 이하. 스캔 PDF는 미지원이다. 문서와 임베딩은 이 PC에만 저장한다."));
            var buttons = new WrapPanel(); panel.Children.Add(buttons);
            buttons.Children.Add(FeatureButton("폴더 선택", "VaultFolder", () => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) IndexFolder(dialog.FolderName); }));
            buttons.Children.Add(FeatureButton("가상 문서 5개 색인", "VaultSamples", () => IndexFolder(Path.Combine(root, "assets/documents"))));
            vaultQuestion = new TextBox { MinHeight = 80, AcceptsReturn = true, Margin = new Thickness(0, 8, 0, 8), Text = "국내 출장의 하루 식비 한도는?" }; panel.Children.Add(vaultQuestion);
            System.Windows.Automation.AutomationProperties.SetAutomationId(vaultQuestion, "VaultQuestion");
            panel.Children.Add(FeatureButton("문서에 질문", "VaultAsk", () => RunFeature(async ct => { await EnsureEmbedding(ct); var answer = await Vault.AnswerAsync(vaultQuestion.Text, ct); featureResult.Text = answer.Answer + "\n\n" + string.Join("\n\n", answer.Sources.Select(s => $"출처: {s.File} · 문단 {s.Paragraph}\n{s.Text}")); })));
            panel.Children.Add(featureResult);
            panel.Children.Add(Note(IdeaText("B")));
        }
        else if (name.StartsWith('E'))
        {
            panel.Children.Add(Note("다른 앱에서 텍스트를 선택하고 Ctrl+Alt+Space를 누른다. 선택 텍스트만 이 PC의 모델로 처리한다. 기존 클립보드는 읽은 직후 복원하며 원래 앱의 내용을 자동으로 바꾸지 않는다."));
            panel.Children.Add(Note(hotkeyReady ? "전역 단축키 등록 완료이다. 창을 최소화하면 트레이에서 계속 동작한다." : "전역 단축키 등록 미완료이다. 충돌 또는 데스크톱 권한을 확인해야 한다."));
            panel.Children.Add(FeatureButton("트레이로 최소화", "ShortcutTray", () => WindowState = WindowState.Minimized));
            panel.Children.Add(FeatureButton("사용 방법 팝업", "ShortcutHelp", () => ShowShortcut("", "다른 앱에서 선택한 텍스트로 Ctrl+Alt+Space를 눌러야 한다.")));
            panel.Children.Add(Note(IdeaText("E")));
        }
        else
        {
            panel.Children.Add(Note(LimitsDemo.Notice));
            foreach (var example in LimitsDemo.Examples) { var item = example; panel.Children.Add(FeatureButton(item.Name, "Limit-" + Array.IndexOf(LimitsDemo.Examples, item), () => RunFeature(async ct => { var answer = await ImageReader.CompleteAsync(backend, new(item.Prompt, 1024), ct); featureResult.Text = "실제 모델 응답:\n" + answer + "\n\n" + item.Reference + "\n" + item.Caution; })));
            }
            panel.Children.Add(featureResult);
            panel.Children.Add(Note(IdeaText("F")));
        }
        Status.Text = "AI 결과는 검증이 필요하다. 작업 전 로컬 서버를 시작해야 한다.";
    }
    private ImageInput RequireImage() => selectedImage ?? throw new IOException("이미지를 먼저 선택해야 한다.");
    private void TryLoadImage(string path)
    {
        try { if (new FileInfo(path).Length > 16_777_216) throw new IOException("이미지 상한은 16MiB이다."); var ext = Path.GetExtension(path).ToLowerInvariant(); if (ext is not (".png" or ".jpg" or ".jpeg")) throw new IOException("PNG/JPEG만 지원한다."); SetImage(new(File.ReadAllBytes(path), ext == ".png" ? "image/png" : "image/jpeg")); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void SetImage(ImageInput image)
    {
        if (image.Data.Length > 16_777_216) throw new IOException("이미지 상한은 16MiB이다.");
        ShowPreview(image); selectedImage = image; extraction = null; if (extractedGrid is not null) extractedGrid.ItemsSource = null; if (featureResult is not null) featureResult.Clear(); Status.Text = "이미지가 준비됐다. 선택한 모드를 실행해야 한다.";
    }
    private void ShowPreview(ImageInput image)
    {
        using var stream = new MemoryStream(image.Data); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); if (preview is not null) preview.Source = bitmap;
    }
    internal static ImageInput Encode(BitmapSource image) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream); return new(stream.ToArray()); }
    private void SaveCsv()
    {
        try { if (extraction is null) throw new IOException("추출을 먼저 실행해야 한다."); var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "추출결과.csv" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, ImageReader.Csv(extraction), new UTF8Encoding(true)); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async Task EnsureEmbedding(CancellationToken ct) { if (embeddingBackend.GetStats().ProcessId is null) await embeddingBackend.StartAsync(ProductPaths.Embedding(root), ct); }
    private void IndexFolder(string folder) => RunFeature(async ct => { await EnsureEmbedding(ct); var count = await Vault.IndexAsync(folder, new Progress<string>(f => Status.Text = "CPU 색인 중: " + f), ct); featureResult!.Text = $"{count}개 청크 로컬 저장 완료이다.\nAI 결과는 검증이 필요하다."; }, false);
    private async void RunFeature(Func<CancellationToken, Task> action, bool needsInference = true)
        => await RunFeatureTask(action, needsInference);
    private async Task<bool> RunFeatureTask(Func<CancellationToken, Task> action, bool needsInference = true)
    {
        if (requestLife is not null) return false;
        if (needsInference && backend.GetStats().ProcessId is null) { Status.Text = "로컬 서버를 먼저 시작해야 한다."; return false; }
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token); CancelButton.IsEnabled = true; StartButton.IsEnabled = false;
        try { Status.Text = "실제 로컬 처리 중이다."; await action(requestLife.Token); Status.Text = "처리 완료이다. AI 결과는 검증이 필요하다."; SaveVisualDiagnostic(); return true; }
        catch (OperationCanceledException) { Status.Text = "작업을 취소했다. 부분 결과는 완료 결과가 아니다."; return false; }
        catch (Exception ex) { Status.Text = ex.Message; return false; }
        finally { requestLife.Dispose(); requestLife = null; CancelButton.IsEnabled = false; StartButton.IsEnabled = backend.GetStats().ProcessId is null; UpdateMetrics(); }
    }
    private void SaveVisualDiagnostic()
    {
        if (!Environment.GetCommandLineArgs().Contains("--diagnostics") || Content is not FrameworkElement visual) return;
        try
        {
            visual.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(Background, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = Path.Combine(root, "docs/stage2-runs"); Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, $"gui-render-{Environment.ProcessId}-{MainTitle.Text[0]}.png")); encoder.Save(output);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("GUI 시각 진단 미확인: " + ex.Message); }
    }
}
