using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalMind.Core;
using LocalMind.Backend;
using LocalMind.Telemetry;

namespace LocalMind.Wpf;

public partial class MainWindow : Window
{
    private readonly LlamaServerBackend backend = new();
    private readonly LocalMind.App.ShellViewModel vm;
    private readonly string root = ProductPaths.FindRoot();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource windowLife = new();
    private CancellationTokenSource? requestLife;
    private Nvml? nvml;
    private bool closing;
    private readonly SettingsStore settings = new();
    public MainWindow()
    {
        InitializeComponent(); vm = new(backend); DataContext = vm;
        try { Price.Text = settings.Load().ReferencePricePerMillion?.ToString() ?? ""; }
        catch { Status.Text = "저장된 설정을 읽지 못했다. 기존 파일은 보존한다."; }
        try { nvml = new Nvml(); } catch (Exception ex) { Status.Text = ex.Message; }
        timer.Tick += (_, _) => UpdateMetrics(); timer.Start(); UpdateMetrics();
        InitializeDemo();
        if (!Environment.GetCommandLineArgs().Contains("--shortcut-clipboard-guard")) SourceInitialized += (_, _) => SetupShortcuts();
        if (Environment.GetCommandLineArgs().Contains("--input-self-test")) Loaded += async (_, _) => await RunInputDiagnostics();
        if (Environment.GetCommandLineArgs().Contains("--shortcut-clipboard-probe")) Loaded += (_, _) => ProbeShortcutClipboard();
        if (Environment.GetCommandLineArgs().Contains("--shortcut-clipboard-guard")) Loaded += async (_, _) => await GuardShortcutClipboard();
        if (Environment.GetCommandLineArgs().Contains("--shortcut-fixture")) Loaded += (_, _) => ShowShortcut("가상 고객에게 내일 오후 세 시까지 견적서를 보내 주세요.", "가짜 입력 직접 전달 검사이다. 전역키·다른 앱 선택 읽기 검사는 아니다.");
    }
    private async void StartServer(object sender, RoutedEventArgs e)
    {
        if (requestLife is not null) return;
        await StartLocalServer();
    }
    private async Task StartLocalServer()
    {
        StartButton.IsEnabled = false; CancelButton.IsEnabled = true;
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token);
        try
        {
            var sample = nvml?.Read() ?? throw new IOException("VRAM 미확인으로 시작을 보류한다.");
            var options = ProductPaths.Demo(root);
            if (options.Context == 8192 && sample.TotalBytes - sample.UsedBytes < 6_000_000_000UL)
            {
                if (MessageBox.Show(this, "여유 VRAM이 6GB 미만이다. E2B·4096 컨텍스트로 전환할지 확인해야 한다. 취소하면 모델을 시작하지 않는다.", "VRAM 폴백 확인", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                options = ProductPaths.Fallback(root);
                if (!File.Exists(options.Model) || !File.Exists(options.Projector)) throw new IOException("E2B 모델 또는 프로젝터가 없다. 모델 다운로드로 사전 준비해야 한다.");
            }
            Status.Text = "모델을 읽는 중이다. 취소할 수 있다.";
            await backend.StartAsync(options, requestLife.Token);
            ModelLabel.Text = options.Context == 4096 ? "Gemma 4 E2B · ggml-org\nQ4_0 · 컨텍스트 4096 (메모리 절약 기본)" : "Gemma 4 E4B · Google QAT\nQ4_0 · 컨텍스트 8192";
            Status.Text = "로컬 서버 준비 완료이다."; SendButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { Status.Text = "시작을 취소했다."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { CancelButton.IsEnabled = false; StartButton.IsEnabled = backend.GetStats().ProcessId is null; requestLife.Dispose(); requestLife = null; }
    }
    private async void SendChat(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) return;
        SendButton.IsEnabled = false; CancelButton.IsEnabled = true;
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token);
        Answer.Text = ""; Status.Text = "생성 중이다.";
        try
        {
            await foreach (var chunk in backend.StreamChatAsync(new(Input.Text), requestLife.Token)) Answer.AppendText(chunk.Content);
            Status.Text = "생성 완료이다. 결과를 검토해야 한다.";
        }
        catch (OperationCanceledException) { Status.Text = "취소했다. 부분 응답은 완료 결과가 아니다."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { requestLife.Dispose(); requestLife = null; SendButton.IsEnabled = true; CancelButton.IsEnabled = false; UpdateMetrics(); }
    }
    private void CancelRequest(object sender, RoutedEventArgs e) => requestLife?.Cancel();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; timer.Stop(); windowLife.Cancel(); requestLife?.Cancel();
        DisposeShortcuts();
        try { await Task.WhenAll(backend.StopAsync(), embeddingBackend.StopAsync()); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { nvml?.Dispose(); nvml = null; Close(); }
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (requestLife is not null) { Status.Text = "작업을 완료하거나 취소한 뒤 화면을 전환해야 한다."; return; }
        if (guide?.Active == true) FinishDemo();
        var name = (string)((Button)sender).Content;
        ShowFeature(name);
    }
    private void ChangeFont(object sender, RoutedEventArgs e)
    {
        if (Answer is null) return;
        var large = LargeText.IsChecked == true;
        Answer.FontSize = large ? 28 : 21; Input.FontSize = large ? 24 : 18; Metrics.FontSize = large ? 19 : 16;
        if (featureResult is not null) featureResult.FontSize = large ? 25 : 20;
        if (vaultQuestion is not null) vaultQuestion.FontSize = large ? 24 : 18;
        DemoProgress.FontSize = large ? 21 : 17; DemoInstruction.FontSize = large ? 18 : 15;
    }
    private void PriceChanged(object sender, TextChangedEventArgs e)
    {
        if (Cost is null) return;
        UpdateCost();
        try
        {
            if (string.IsNullOrWhiteSpace(Price.Text)) settings.Save(new());
            else if (double.TryParse(Price.Text, out var p) && double.IsFinite(p) && p >= 0) settings.Save(new(p));
        }
        catch { Status.Text = "설정 저장 실패이다. 참고 비용은 현재 화면 값 기준이다."; }
    }
    private void UpdateCost() => Cost.Text = double.TryParse(Price.Text, out var price) && double.IsFinite(price) && price >= 0
        ? $"입력한 단가 기준 참고용: {backend.GetStats().TotalTokens / 1_000_000.0 * price:F4}" : "입력한 단가 기준 참고용: 미입력";
    private async void VerifyModels(object sender, RoutedEventArgs e) => await CheckModels(false);
    private async void DownloadModels(object sender, RoutedEventArgs e) => await CheckModels(true);
    private async Task CheckModels(bool downloading)
    {
        if (requestLife is not null) { Status.Text = "다른 작업이 진행 중이다. 먼저 완료하거나 취소해야 한다."; return; }
        if (downloading && MessageBox.Show(this, "없는 E2B 데모 모델·프로젝터·CPU 임베딩을 고정 외부 주소에서 다운로드한다. 추론 API는 호출하지 않는다.", "다운로드 확인", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token);
        var canStart = StartButton.IsEnabled; var canSend = SendButton.IsEnabled;
        StartButton.IsEnabled = false; SendButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try
        {
            var store = new ModelStore(root);
            foreach (var model in store.ReadManifest().Where(m => m.Name is "fallback" or "fallback-mmproj" or "embedding"))
            {
                Status.Text = model.Name + " 검증 중이다.";
                if (downloading && !File.Exists(store.Resolve(model))) await store.DownloadAsync(model,
                    new Progress<long>(n => Status.Text = $"{model.Name}: {n / 1024 / 1024} MiB 다운로드 중이다."), requestLife.Token);
                if (!await store.VerifyAsync(model, requestLife.Token)) throw new IOException(model.Name + " 무결성 미확인이다. 기존 파일은 보존한다.");
            }
            Status.Text = "데모 모델·프로젝터·CPU 임베딩 SHA-256 검증 통과이다.";
            demoModelsVerified = true;
        }
        catch (OperationCanceledException) { Status.Text = "검증/다운로드가 취소됐다."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { requestLife.Dispose(); requestLife = null; CancelButton.IsEnabled = false; StartButton.IsEnabled = canStart; SendButton.IsEnabled = canSend; }
    }
    private void UpdateMetrics()
    {
        UpdateDemoClock();
        try
        {
            var s = backend.GetStats(); var gpu = nvml?.Read();
            Metrics.Text = $"속도: {s.TokensPerSecond?.ToString("F1") ?? "미확인"} tok/s\n출처: {s.RateSource}\n첫 토큰: {s.FirstTokenSeconds?.ToString("F3") ?? "미확인"}초\n누적 처리: {s.TotalTokens}\nVRAM: {(gpu is null ? "미확인" : (gpu.UsedBytes / Math.Pow(1024, 3)).ToString("F3") + " GiB")}\nGPU: {gpu?.Utilization?.ToString() ?? "미확인"}%\n온도: {gpu?.Temperature?.ToString() ?? "미확인"}℃\n전력: {gpu?.PowerWatts?.ToString("F1") ?? "미확인"}W\n인터넷: {NetworkMonitor.InternetStatus()}";
            if (s.ProcessId is int pid)
            {
                var count = NetworkMonitor.CountExternalSockets(pid);
                if (embeddingBackend.GetStats().ProcessId is int embeddingPid) count += NetworkMonitor.CountExternalSockets(embeddingPid);
                Sockets.Text = $"외부 연결 {count}건"; Sockets.Foreground = count == 0 ? Brushes.LightGreen : Brushes.OrangeRed;
            }
            else { Sockets.Text = "외부 연결 미확인 · 서버 중지"; Sockets.Foreground = Brushes.Gray; }
            if (embeddingBackend.GetStats().ProcessId is not null) Metrics.Text += "\n임베딩: CPU · 2048\n감시: 추론+임베딩 서버";
            if (Environment.GetCommandLineArgs().Contains("--diagnostics"))
            {
                var path = Path.Combine(root, "docs/stage1-runs", "gui-telemetry-" + Environment.ProcessId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new { time = DateTimeOffset.Now, gpu, stats = s, metrics = Metrics.Text, sockets = Sockets.Text }));
                File.Move(path + ".tmp", path, true);
            }
            UpdateCost();
        }
        catch { Metrics.Text = "측정 미확인이다. 프로세스/드라이버 상태를 확인해야 한다."; Sockets.Text = "외부 연결 미확인"; Sockets.Foreground = Brushes.Orange; }
    }
}
