using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LocalMind.Wpf;

public partial class MainWindow
{
    // Explicit diagnostic switch only. Uses synthetic images and restores clipboard and cursor.
    private async Task RunInputDiagnostics()
    {
        var output = Path.Combine(root, "docs/stage2-runs", "inputs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output); List<string> passed = []; string? failure = null;
        DataObject? saved = null; bool clipboardChanged = false; Window? fixture = null;
        var cursorAvailable = GetCursorPos(out var cursor);
        void Check(bool value, string name) { if (!value) throw new IOException(name); passed.Add(name); }
        Button Find(string id) => ((StackPanel)FeatureHost.Content).Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Button>()).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == id);
        try
        {
            ShowFeature("A · 화면·문서 읽기");
            var panel = (StackPanel)FeatureHost.Content;
            var path = Path.Combine(root, "assets/samples/images/receipt.png");
            var drop = new DataObject(DataFormats.FileDrop, new[] { path });
            var constructor = typeof(DragEventArgs).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            var dropEvent = (DragEventArgs)constructor.Invoke(new object[] { drop, DragDropKeyStates.None, DragDropEffects.Copy, panel, new Point(0, 0) });
            dropEvent.RoutedEvent = DragDrop.DropEvent; panel.RaiseEvent(dropEvent);
            Check(selectedImage!.Data.SequenceEqual(File.ReadAllBytes(path)), "파일 드롭 routed-event와 원본 이미지 읽기");
            saved = new DataObject();
            var original = Clipboard.GetDataObject();
            if (original is not null) foreach (var format in original.GetFormats(false)) { var value = original.GetData(format, false); if (value is not null) saved.SetData(format, value, false); }
            var pixels = new byte[120 * 80 * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 220; pixels[i + 1] = 100; pixels[i + 2] = 20; pixels[i + 3] = 255; }
            var image = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 120 * 4);
            clipboardChanged = true; Clipboard.SetImage(image);
            Find("ImagePaste").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(preview!.Source is BitmapSource { PixelWidth: 120, PixelHeight: 80 }, "실제 Windows 클립보드 이미지 붙여넣기 버튼");
            if (!cursorAvailable) throw new IOException("입력 데스크톱 접근 미확인: GetCursorPos 실패로 실제 마우스 검증을 중단한다");
            passed.Add("입력 데스크톱 커서 접근 성공");
            fixture = new Window { Owner = this, Left = 200, Top = 200, Width = 320, Height = 220, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(20, 100, 220)), ShowInTaskbar = false };
            fixture.Show(); fixture.Activate(); await Task.Delay(350);
            var first = fixture.PointToScreen(new Point(30, 30)); var last = fixture.PointToScreen(new Point(130, 110));
            var capture = new RegionCapture { Owner = this };
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            int step = 0;
            tick.Tick += (_, _) =>
            {
                if (step++ == 0) { SetCursorPos((int)first.X, (int)first.Y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); }
                else { SetCursorPos((int)last.X, (int)last.Y); mouse_event(4, 0, 0, 0, UIntPtr.Zero); tick.Stop(); }
            };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timeout.Tick += (_, _) => { timeout.Stop(); capture.Close(); };
            tick.Start(); timeout.Start(); var accepted = capture.ShowDialog(); tick.Stop(); timeout.Stop();
            Check(accepted == true && capture.Result is not null, "실제 마우스 영역 선택 후 ShowDialog 결과 준비");
            File.WriteAllBytes(Path.Combine(output, "region.png"), capture.Result!.Data);
            using (var stream = new MemoryStream(capture.Result.Data))
            {
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4]; converted.CopyPixels(new Int32Rect(converted.PixelWidth / 2, converted.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Check(decoded.PixelWidth == (int)(last.X - first.X) && decoded.PixelHeight == (int)(last.Y - first.Y) && pixel[0] == 220 && pixel[1] == 100 && pixel[2] == 20, "캡처 실제 픽셀과 크기 원본 대조");
            }
            var cancelCapture = new RegionCapture { Owner = this };
            var cancelTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            cancelTick.Tick += (_, _) => { cancelTick.Stop(); keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); };
            var cancelTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            cancelTimeout.Tick += (_, _) => { cancelTimeout.Stop(); cancelCapture.Close(); };
            cancelTick.Start(); cancelTimeout.Start(); var cancelled = cancelCapture.ShowDialog(); cancelTick.Stop(); cancelTimeout.Stop();
            Check(cancelled == false && cancelCapture.Result is null, "영역 캡처 Esc 취소");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            fixture?.Close(); if (cursorAvailable) SetCursorPos(cursor.X, cursor.Y);
            if (clipboardChanged && saved is not null) { try { if (saved.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); passed.Add("기존 클립보드 복원 호출 성공"); } catch (Exception ex) { failure = (failure ?? "") + " 클립보드 복원 실패: " + ex.Message; } }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed, total = passed.Count, error = failure, osDragGesture = "routed-event만 검증, OS 드래그 제스처 미확인", time = DateTimeOffset.Now }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
