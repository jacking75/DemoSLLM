using System.Windows;
namespace LocalMind.Wpf;
public partial class StudioApp : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 추론에 VRAM을 우선 배정하고 드라이버 의존적인 WPF GPU 렌더링을 피한다.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }
}
