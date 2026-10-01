using Microsoft.UI.Xaml;

namespace LocalMind.App;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new StudioApplication();
            });
        }
        catch (Exception ex) { RecordError(ex.ToString()); throw; }
    }
    internal static void RecordError(string text)
    {
        var folder = Path.Combine(LocalMind.Core.ProductPaths.FindRoot(), "docs/stage1-runs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app-startup-error.txt"), DateTimeOffset.Now + "\n" + text);
    }
}
public sealed partial class StudioApplication : Application
{
    private Window? window;
    public StudioApplication()
    {
        UnhandledException += (_, e) => Program.RecordError(e.Exception.ToString());
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(); window.Activate();
    }
}
