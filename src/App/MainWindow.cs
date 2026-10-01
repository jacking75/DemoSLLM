using LocalMind.Backend;
using LocalMind.Core;
using LocalMind.Telemetry;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace LocalMind.App;

public sealed class MainWindow : Window
{
    private readonly LlamaServerBackend backend = new();
    private readonly ShellViewModel vm;
    private readonly string root;
    private readonly Grid layout = new() { Padding = new Thickness(28), ColumnSpacing = 24, RowSpacing = 18 };
    private readonly TextBlock status = Label("", 16);
    private readonly TextBox input = new() { PlaceholderText = "업무 질문을 입력한다. 예: 로컬 AI의 장점을 설명해 줘.", AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap, FontSize = 18 };
    private readonly TextBox answer = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 20, BorderThickness = new Thickness(0), MinHeight = 270 };
    private readonly TextBlock metrics = Label("미확인", 16);
    private readonly TextBlock sockets = Label("외부 연결 미확인", 18);
    private readonly Button start = new() { Content = "로컬 서버 시작" };
    private readonly Button send = new() { Content = "질문 보내기", IsEnabled = false };
    private readonly Button cancel = new() { Content = "취소", IsEnabled = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource windowLife = new();
    private CancellationTokenSource? requestLife;
    private Nvml? nvml;
    private bool closing;

    public MainWindow()
    {
        Title = "LocalMind Studio · 로컬 업무 AI";
        root = ProductPaths.FindRoot(); vm = new ShellViewModel(backend);
        AppWindow.Resize(new SizeInt32(1280, 900));
        layout.RequestedTheme = ElementTheme.Dark;
        layout.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 14, 20, 32));
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(220) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(285) });
        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(Label("LOCALMIND STUDIO", 30));
        header.Children.Add(Label("우리 데이터로, 우리 PC에서 · 1단계 기반", 16));
        Grid.SetColumnSpan(header, 3); layout.Children.Add(header);
        var nav = new StackPanel { Spacing = 12 };
        nav.Children.Add(Label("업무 시나리오", 17));
        foreach (var name in new[] { "로컬 채팅", "A · 화면·문서 읽기", "B · 내 문서 금고", "E · 전역 단축키", "F · AI 한계" })
        {
            var button = new Button { Content = name, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => { status.Text = name == "로컬 채팅" ? vm.Status : name + "는 다음 개발 단계에서 구현한다. 현재는 기반 채팅 화면이다."; };
            nav.Children.Add(button);
        }
        nav.Children.Add(Label("음성·파일 정리: P2 보류", 14));
        var large = new ToggleSwitch { Header = "시연 글자 확대" };
        large.Toggled += (_, _) => { answer.FontSize = large.IsOn ? 28 : 20; input.FontSize = large.IsOn ? 24 : 18; metrics.FontSize = large.IsOn ? 19 : 16; };
        nav.Children.Add(large);
        nav.Children.Add(Label("데모 전 체크\n브라우저·게임·녹화 종료\n모델 사전 설치\n인터넷 단절은 직접 확인", 14));
        Place(nav, 1, 0);
        var workspace = new StackPanel { Spacing = 16 };
        workspace.Children.Add(Label("외부 AI API 없이 대화한다", 26));
        workspace.Children.Add(Label("AI 결과는 검증이 필요하다. 실제 생성 결과만 표시한다.", 14));
        workspace.Children.Add(answer); workspace.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(start); actions.Children.Add(send); actions.Children.Add(cancel);
        workspace.Children.Add(actions); workspace.Children.Add(status);
        var scroll = new ScrollViewer { Content = workspace }; Place(scroll, 1, 1);
        var panel = new StackPanel { Spacing = 18 };
        panel.Children.Add(Label("투명성 패널", 23));
        panel.Children.Add(Label("Gemma 4 E4B · Google QAT\nQ4_0 · 컨텍스트 8192", 16));
        panel.Children.Add(sockets); panel.Children.Add(metrics);
        var cost = new TextBox { Header = "백만 토큰당 참고 단가 (선택)", PlaceholderText = "기본값 없음", InputScope = null };
        var costText = Label("입력한 단가 기준 참고용: 미입력", 14);
        cost.TextChanged += (_, _) => costText.Text = double.TryParse(cost.Text, out var price) && double.IsFinite(price) && price >= 0
            ? $"입력한 단가 기준 참고용: {backend.GetStats().TotalTokens / 1_000_000.0 * price:F4}" : "입력한 단가 기준 참고용: 미입력";
        panel.Children.Add(cost); panel.Children.Add(costText);
        var verify = new Button { Content = "모델 SHA-256 검증" };
        verify.Click += async (_, _) => await CheckModelsAsync(false);
        var download = new Button { Content = "없는 모델 다운로드" };
        download.Click += async (_, _) => await CheckModelsAsync(true);
        panel.Children.Add(verify); panel.Children.Add(download);
        panel.Children.Add(Label("추론: 127.0.0.1 전용\n사전 설치 후 다운로드 불필요\n방화벽 자동 변경 없음", 14));
        Place(new ScrollViewer { Content = panel }, 1, 2);
        Content = layout;
        status.Text = vm.Status;
        start.Click += async (_, _) => await StartAsync();
        send.Click += async (_, _) => await SendAsync();
        cancel.Click += (_, _) => requestLife?.Cancel();
        try { nvml = new Nvml(); } catch (Exception ex) { status.Text = ex.Message; }
        timer.Tick += (_, _) => UpdateMetrics(); timer.Start();
        // Cancel the close until async cleanup has completed. Force-kill is covered by Job handle lifetime.
        AppWindow.Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true; closing = true; timer.Stop(); windowLife.Cancel(); requestLife?.Cancel();
            try { await backend.StopAsync(); } catch (Exception ex) { status.Text = ex.Message; }
            finally { nvml?.Dispose(); nvml = null; Close(); }
        };
    }
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private void Place(FrameworkElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); layout.Children.Add(element); }

    private async Task<bool> Confirm(string title, string text)
    {
        var dialog = new ContentDialog { Title = title, Content = text, PrimaryButtonText = "진행", CloseButtonText = "취소", XamlRoot = layout.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async Task StartAsync()
    {
        start.IsEnabled = false; cancel.IsEnabled = true;
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token);
        try
        {
            var sample = nvml?.Read();
            if (sample is null) throw new IOException("VRAM이 미확인이므로 시작을 보류한다. NVIDIA 드라이버를 확인해야 한다.");
            if (sample.TotalBytes - sample.UsedBytes < 6_000_000_000UL)
                throw new IOException("여유 VRAM이 6GB 미만이다. E2B 폴백 모델은 아직 검증되지 않아 시작을 보류한다. 다른 GPU 프로그램을 종료해야 한다.");
            status.Text = "모델을 읽는 중이다. 취소할 수 있다.";
            await backend.StartAsync(ProductPaths.Primary(root), requestLife.Token);
            vm.Status = "로컬 서버 준비 완료이다."; status.Text = vm.Status; send.IsEnabled = true;
        }
        catch (OperationCanceledException) { status.Text = "시작을 취소했다."; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { cancel.IsEnabled = false; start.IsEnabled = backend.GetStats().ProcessId is null; requestLife.Dispose(); requestLife = null; }
    }
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(input.Text)) return;
        send.IsEnabled = false; cancel.IsEnabled = true;
        requestLife = CancellationTokenSource.CreateLinkedTokenSource(windowLife.Token);
        answer.Text = ""; status.Text = "생성 중이다.";
        try
        {
            await foreach (var chunk in backend.StreamChatAsync(new(input.Text), requestLife.Token)) answer.Text += chunk.Content;
            status.Text = "생성 완료이다. 결과를 검토해야 한다.";
        }
        catch (OperationCanceledException) { status.Text = "취소했다. 부분 응답은 완료 결과가 아니다."; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { requestLife.Dispose(); requestLife = null; send.IsEnabled = true; cancel.IsEnabled = false; UpdateMetrics(); }
    }
    private async Task CheckModelsAsync(bool downloading)
    {
        if (downloading && !await Confirm("외부 다운로드", "AI 추론은 호출하지 않는다. 없는 Google 모델과 프로젝터만 고정 주소에서 다운로드한다.")) return;
        try
        {
            var store = new ModelStore(root);
            foreach (var model in store.ReadManifest().Where(a => a.Name is "google-qat" or "google-mmproj"))
            {
                status.Text = model.Name + " 검증 중이다.";
                if (downloading && !File.Exists(store.Resolve(model)))
                    await store.DownloadAsync(model, new Progress<long>(n => status.Text = $"{model.Name}: {n / 1024 / 1024} MiB 다운로드 중이다."), windowLife.Token);
                if (!await store.VerifyAsync(model, windowLife.Token)) throw new IOException(model.Name + " 무결성 미확인이다. 기존 파일은 보존한다.");
            }
            status.Text = "모델·프로젝터 SHA-256 검증 통과이다.";
        }
        catch (OperationCanceledException) { status.Text = "검증/다운로드가 취소됐다."; }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    private void UpdateMetrics()
    {
        try
        {
            var s = backend.GetStats(); var gpu = nvml?.Read();
            metrics.Text = $"속도: {(s.TokensPerSecond?.ToString("F1") ?? "미확인")} tok/s\n출처: {s.RateSource}\n첫 토큰: {(s.FirstTokenSeconds?.ToString("F3") ?? "미확인")}초\n누적 처리: {s.TotalTokens}\nVRAM: {(gpu is null ? "미확인" : (gpu.UsedBytes / Math.Pow(1024, 3)).ToString("F3") + " GiB")}\nGPU: {gpu?.Utilization}%\n온도: {gpu?.Temperature}℃\n전력: {gpu?.PowerWatts:F1}W\n인터넷: {NetworkMonitor.InternetStatus()}";
            if (s.ProcessId is int pid)
            {
                var count = NetworkMonitor.CountExternalSockets(pid);
                sockets.Text = $"외부 연결 {count}건";
                sockets.Foreground = new SolidColorBrush(count == 0 ? Colors.LightGreen : Colors.OrangeRed);
            }
            else { sockets.Text = "외부 연결 미확인 · 서버 중지"; sockets.Foreground = new SolidColorBrush(Colors.Gray); }
        }
        catch { metrics.Text = "측정 미확인이다. 프로세스/드라이버 상태를 확인해야 한다."; sockets.Text = "외부 연결 미확인"; sockets.Foreground = new SolidColorBrush(Colors.Orange); }
    }
}
