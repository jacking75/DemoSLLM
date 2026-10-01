using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalMind.Core;
using LocalMind.Features;
using LocalMind.Telemetry;

namespace LocalMind.Wpf;

public partial class MainWindow
{
    private DemoGuide? guide;
    private readonly Stopwatch demoClock = new();
    private readonly Dictionary<string, string> demoResults = [];
    private bool replayMode, changingReplay, demoModelsVerified;
    private bool DemoIdle()
    {
        if (requestLife is null && !shortcutReading && shortcutLife is null) return true;
        Status.Text = "작업을 완료하거나 취소한 뒤 데모를 전환해야 한다."; return false;
    }
    private void InitializeDemo()
    {
        try { guide = new DemoGuide(root); }
        catch (Exception ex) { DemoBeginButton.IsEnabled = false; DemoProgress.Text = "데모 구성 오류: " + ex.Message; }
        UpdateDemoClock();
    }
    private string IdeaText(string key) => guide is null ? "아이디어 카드 구성을 읽지 못했다. 데모 구성을 확인해야 한다."
        : "우리 회사라면? (문구 초안)\n" + string.Join(" · ", guide.Content.Ideas[key]);
    private void BeginDemo(object sender, RoutedEventArgs e)
    {
        if (guide is null || !DemoIdle()) return;
        shortcutPopup?.Close(); guide.Begin(); demoClock.Restart(); demoResults.Clear();
        replayMode = SafeReplay.IsChecked == true; ShowDemoStep();
    }
    private void PreviousDemo(object sender, RoutedEventArgs e) => MoveDemo(-1);
    private void NextDemo(object sender, RoutedEventArgs e) => MoveDemo(1);
    private void MoveDemo(int direction)
    {
        if (!DemoIdle() || guide is null || !guide.Move(direction)) return;
        shortcutPopup?.Close(); ShowDemoStep();
    }
    private void EndDemo(object sender, RoutedEventArgs e)
    {
        if (!DemoIdle()) return;
        FinishDemo(); ShowFeature("로컬 채팅"); Status.Text = "데모를 종료했다. 진행표를 다시 시작할 수 있다.";
    }
    private void FinishDemo()
    {
        guide?.End(); demoClock.Stop(); demoResults.Clear(); shortcutPopup?.Close();
        ReplayBanner.Visibility = DemoInstruction.Visibility = DemoNotes.Visibility = Visibility.Collapsed;
        DemoPreviousButton.IsEnabled = DemoNextButton.IsEnabled = DemoRunButton.IsEnabled = DemoEndButton.IsEnabled = false;
        DemoProgress.Text = "7분 진행 도우미 · 샘플과 실행 안내를 자동으로 준비한다.";
        StartButton.IsEnabled = backend.GetStats().ProcessId is null;
    }
    private void ChangeReplay(object sender, RoutedEventArgs e)
    {
        if (changingReplay || guide?.Active != true) return;
        if (!DemoIdle())
        {
            changingReplay = true; SafeReplay.IsChecked = replayMode; changingReplay = false; return;
        }
        shortcutPopup?.Close(); replayMode = SafeReplay.IsChecked == true; demoResults.Clear(); ShowDemoStep();
    }
    private void ChangeNotes(object sender, RoutedEventArgs e)
    {
        if (DemoNotes is not null) DemoNotes.Visibility = guide?.Active == true && PresenterNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowDemoStep()
    {
        if (guide?.Active != true) return;
        var step = guide.Current;
        DemoInstruction.Text = step.Instruction; DemoInstruction.Visibility = Visibility.Visible;
        DemoNotes.Text = step.Notes; ChangeNotes(this, new RoutedEventArgs());
        ReplayBanner.Visibility = replayMode ? Visibility.Visible : Visibility.Collapsed;
        DemoPreviousButton.IsEnabled = guide.Position > 0;
        DemoNextButton.IsEnabled = guide.Position < guide.Content.Steps.Length - 1;
        DemoRunButton.IsEnabled = DemoEndButton.IsEnabled = true;
        StartButton.IsEnabled = !replayMode && backend.GetStats().ProcessId is null;
        if (!replayMode && step.Screen is "A" or "B" or "E" or "F")
        {
            ShowFeature(step.Screen switch { "A" => "A · 화면·문서 읽기", "B" => "B · 내 문서 금고", "E" => "E · 전역 단축키", _ => "F · AI 한계" });
            // 단계별 샘플을 이전 화면의 사용자 입력보다 우선하며, 실제 추론은 실행 버튼으로 시작한다.
            featureResult?.Clear(); extraction = null; if (extractedGrid is not null) extractedGrid.ItemsSource = null;
            if (step.Sample is not null) TryLoadImage(guide.ResolveAsset(step.Sample));
            if (step.Screen == "B" && vaultQuestion is not null) vaultQuestion.Text = step.Question ?? "";
        }
        else ShowDemoPanel(step);
        MainTitle.Text = step.Title;
        Status.Text = replayMode ? "안전 재생이다. 현재 단계 실행으로 저장된 실제 응답을 표시한다." : "실제 모드이다. 샘플이 준비됐으며 현재 단계 실행을 누르면 처리한다.";
        UpdateDemoClock();
    }
    private void ShowDemoPanel(DemoStep step)
    {
        FeatureHost.Visibility = Visibility.Visible; Answer.Visibility = Input.Visibility = SendButton.Visibility = Visibility.Collapsed;
        var panel = new StackPanel(); FeatureHost.Content = panel;
        panel.Children.Add(Note(step.Action == "setup" ? "모델·프로젝터·CPU 임베딩을 사전에 준비한다. 관리자 권한이나 인터넷 접속 없이 사전 설치한 모델을 실행한다." : step.Instruction));
        if (step.Sample is not null)
        {
            preview = new Image { Height = 145, Stretch = Stretch.Uniform }; panel.Children.Add(preview);
            ShowPreview(new ImageInput(File.ReadAllBytes(guide!.ResolveAsset(step.Sample))));
        }
        featureResult = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontSize = LargeText.IsChecked == true ? 25 : 20, MinHeight = 150, MaxHeight = 350, Margin = new Thickness(0, 8, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(featureResult, "FeatureResult"); panel.Children.Add(featureResult);
        if (step.Action == "ideas") foreach (var key in new[] { "A", "B", "E", "F" }) panel.Children.Add(Note(key + " · " + IdeaText(key)));
        else if (guide!.Content.Ideas.ContainsKey(step.Screen)) panel.Children.Add(Note(IdeaText(step.Screen)));
    }
    private async void RunDemo(object sender, RoutedEventArgs e)
    {
        if (guide?.Active != true || !DemoIdle()) return;
        var step = guide.Current;
        try
        {
            if (replayMode)
            {
                if (step.Action == "review")
                {
                    var receipt = guide.ReadReplay("receipt"); var vault = guide.ReadReplay("vault");
                    featureResult!.Text = receipt.Provenance + "\n\n영수증:\n" + receipt.Output + "\n\n문서:\n" + vault.Output;
                }
                else if (step.Replay is { } id)
                {
                    var replay = guide.ReadReplay(id);
                    featureResult!.Text = replay.Provenance + "\n\n입력:\n" + replay.Input + "\n\n저장된 응답:\n" + replay.Output;
                }
                else featureResult!.Text = step.Action == "setup" ? "안전 재생 준비 완료이다. 서버 시작·추론·다운로드를 수행하지 않는다. 다음 단계로 진행한다." : "아이디어 카드에서 적용할 업무와 검토 단계를 함께 선택한다.";
                Status.Text = "재생 결과이다. 현재 추론·성능 측정이 아니다."; return;
            }
            switch (step.Action)
            {
                case "setup":
                    var verified = await RunFeatureTask(async ct =>
                    {
                        var store = new ModelStore(root); var artifacts = store.ReadManifest().Where(a => a.Name is "fallback" or "fallback-mmproj" or "embedding").ToArray();
                        if (artifacts.Length != 3) throw new InvalidDataException("데모 모델 목록이 불완전하다.");
                        foreach (var artifact in artifacts) if (!await store.VerifyAsync(artifact, ct)) throw new IOException(artifact.Name + " 모델이 없거나 무결성이 다르다. 기존 파일은 보존한다.");
                        demoModelsVerified = true; featureResult!.Text = "데모 모델 3개 SHA-256 검증 통과이다.";
                    }, false);
                    if (verified && backend.GetStats().ProcessId is null) await StartLocalServer();
                    if (backend.GetStats().ProcessId is not null) featureResult!.AppendText("\n로컬 추론 서버 준비 완료이다. 다음 단계로 진행한다.");
                    break;
                case "receipt":
                    await RunFeatureTask(async ct =>
                    {
                        extraction = await new ImageReader(backend).ExtractAsync(RequireImage(), ct);
                        extractedGrid!.ItemsSource = extraction.Rows;
                        featureResult!.Text = $"실제 생성 결과 · 합계: {extraction.Total:N0}\n검산값: 9,000\n" + extraction.Raw;
                        demoResults["receipt"] = featureResult.Text;
                    }); break;
                case "error": await RunFeatureTask(async ct => featureResult!.Text = await new ImageReader(backend).ReadAsync(RequireImage(), true, ct)); break;
                case "vault":
                    await RunFeatureTask(async ct =>
                    {
                        await EnsureEmbedding(ct); Status.Text = "가상 문서 5개를 별도 데모 금고에 색인 중이다.";
                        await Vault.IndexAsync(Path.Combine(root, "assets/documents"), null, ct);
                        var answer = await Vault.AnswerAsync(vaultQuestion!.Text, ct);
                        featureResult!.Text = "실제 생성 결과:\n" + answer.Answer + "\n\n" + string.Join("\n\n", answer.Sources.Select(s => $"출처: {s.File} · 문단 {s.Paragraph}\n{s.Text}"));
                        demoResults["vault"] = featureResult.Text;
                    }); break;
                case "shortcut": ShowShortcut(step.Question!, "데모 가짜 문장을 직접 전달했다. 다른 앱의 선택 읽기 검사는 아니다. 변환 동작을 선택해야 한다."); break;
                case "review":
                    featureResult!.Text = string.Join("\n\n", new[] { "receipt", "vault" }.Select(key => demoResults.TryGetValue(key, out var result) ? result : key + ": 이번 데모의 완료 결과가 없다. 해당 단계를 먼저 실행해야 한다.")); break;
                case "limits":
                    await RunFeatureTask(async ct => { var example = LimitsDemo.Examples[0]; featureResult!.Text = "실제 모델 응답:\n" + await ImageReader.CompleteAsync(backend, new(example.Prompt, 256, 0.2), ct) + "\n\n" + example.Reference + "\n" + example.Caution; }); break;
                case "ideas": featureResult!.Text = "업무 하나를 선택하고 입력 자료·검토 담당자·성공 기준을 논의한다. 데모 종료 버튼으로 마친다."; break;
            }
        }
        catch (Exception ex) { Status.Text = "데모 처리 실패: " + ex.Message + " 안전 재생을 선택할 수 있다."; }
        finally { UpdateDemoClock(); SaveDemoDiagnostic(); }
    }
    private static string DemoTime(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
    private void SaveDemoDiagnostic()
    {
        if (!Environment.GetCommandLineArgs().Contains("--diagnostics") || guide?.Active != true || Content is not FrameworkElement visual) return;
        try
        {
            visual.UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) { context.DrawRectangle(Background, null, new Rect(0, 0, width, height)); context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, width, height)); }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var directory = Path.Combine(root, "docs/stage4-runs"); Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, $"render-{Environment.ProcessId}-{guide.Current.Id}-{(replayMode ? "replay" : "live")}.png")); encoder.Save(stream);
        }
        catch (Exception ex) { Debug.WriteLine("데모 렌더링 진단 미확인: " + ex.Message); }
    }
    private void UpdateDemoClock()
    {
        if (DemoChecks is null) return;
        var options = ProductPaths.Demo(root);
        var present = new[] { options.Model, options.Projector, ProductPaths.Embedding(root).Model }.All(File.Exists);
        DemoChecks.Text = $"모델 파일: {(present ? "3개 있음" : "사전 준비 필요")}\nSHA-256: {(demoModelsVerified ? "검증 통과" : "현재 검증 전")}\nVRAM: {(nvml is null ? "미확인" : "패널에서 확인")}\n인터넷: {NetworkMonitor.InternetStatus()}\n전역 단축키: {(hotkeyReady ? "등록됨 · 사전 동작 확인" : "미등록")}";
        if (guide?.Active != true) return;
        var start = guide.PlannedStartSeconds;
        DemoProgress.Text = $"{guide.Position + 1}/{guide.Content.Steps.Length} · {DemoTime(start)}–{DemoTime(start + guide.Current.Seconds)} · {guide.Current.Title} · 경과 {DemoTime((int)demoClock.Elapsed.TotalSeconds)} / 7:00";
        if (demoClock.Elapsed.TotalSeconds > 420) DemoProgress.Text += " · 배정 시간 초과";
    }
}
