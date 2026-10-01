using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LocalMind.Core;

namespace LocalMind.Wpf;

// 사용자가 지정한 주 모니터 영역만 캡처한다. Esc로 취소하며 자동 캡처/저장은 하지 않는다.
public sealed class RegionCapture : Window
{
    private readonly Canvas canvas = new();
    private readonly Rectangle rectangle = new() { Stroke = Brushes.White, StrokeThickness = 2 };
    private Point? start;
    private bool finishing;
    public ImageInput? Result { get; private set; }
    public RegionCapture()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)); Topmost = true; ShowInTaskbar = false;
        Left = Top = 0; Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
        Content = canvas; canvas.Children.Add(rectangle); canvas.Children.Add(new TextBlock { Text = "캡처할 영역을 드래그한다 · Esc 취소 · 주 모니터만 지원한다", Foreground = Brushes.White, Background = Brushes.Black, FontSize = 22 });
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !finishing) DialogResult = false; };
        MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); CaptureMouse(); };
        MouseMove += (_, e) => { if (start is not Point s) return; var p = e.GetPosition(canvas); Canvas.SetLeft(rectangle, Math.Min(p.X, s.X)); Canvas.SetTop(rectangle, Math.Min(p.Y, s.Y)); rectangle.Width = Math.Abs(p.X - s.X); rectangle.Height = Math.Abs(p.Y - s.Y); };
        MouseLeftButtonUp += async (_, e) =>
        {
            if (start is not Point s) return;
            var p = e.GetPosition(canvas); var a = PointToScreen(new(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y))); var b = PointToScreen(new(Math.Max(s.X, p.X), Math.Max(s.Y, p.Y))); ReleaseMouseCapture(); start = null;
            if (b.X - a.X < 8 || b.Y - a.Y < 8) { DialogResult = false; return; }
            // Hide ends ShowDialog before the asynchronous capture has a result.
            // Keep the modal window open, but remove its overlay from the desktop.
            finishing = true; Opacity = 0; IsHitTestVisible = false; await Task.Delay(150);
            try { Result = Capture((int)a.X, (int)a.Y, (int)(b.X - a.X), (int)(b.Y - a.Y)); DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(Owner, ex.Message, "캡처 실패"); DialogResult = false; }
        };
    }
    public static ImageInput Capture(int x, int y, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var screen = GetDC(IntPtr.Zero); IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) throw new Win32Exception();
            memory = CreateCompatibleDC(screen); if (memory == IntPtr.Zero) throw new Win32Exception();
            bitmap = CreateCompatibleBitmap(screen, width, height); if (bitmap == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(memory, bitmap); if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020)) throw new Win32Exception();
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); return MainWindow.Encode(image);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dest, int dx, int dy, int w, int h, IntPtr source, int sx, int sy, uint operation);
}
