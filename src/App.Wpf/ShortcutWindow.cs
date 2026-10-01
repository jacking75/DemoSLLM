using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace LocalMind.Wpf;

internal sealed class ShortcutWindow : Window
{
    private readonly List<Button> actions = [];
    public TextBox Selection { get; } = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 130 };
    public TextBox Output { get; } = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public TextBlock State { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 8, 0, 8) };
    public Button Cancel { get; } = new() { Content = "취소", IsEnabled = false };
    public ShortcutWindow(string selection, string state, Action<string> execute, Action cancel)
    {
        Title = "어디서나 부르는 AI"; Width = 620; Height = 590; MinWidth = 480; MinHeight = 430; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(14, 20, 32)); FontFamily = new FontFamily("맑은 고딕");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "ShortcutPopup");
        AutomationProperties.SetAutomationId(Selection, "ShortcutSelection"); AutomationProperties.SetAutomationId(Output, "ShortcutOutput"); AutomationProperties.SetAutomationId(State, "ShortcutState");
        var grid = new Grid { Margin = new Thickness(20) }; Content = grid;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        grid.Children.Add(new TextBlock { Text = "선택한 문장을 이 PC에서 처리한다", FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
        Selection.Text = selection; Grid.SetRow(Selection, 1); grid.Children.Add(Selection);
        var panel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(panel, 2); grid.Children.Add(panel);
        foreach (var name in new[] { "요약", "맞춤법 교정", "정중한 어투", "영어 번역" })
        {
            var button = new Button { Content = name }; AutomationProperties.SetAutomationId(button, "Shortcut-" + name);
            button.Click += (_, _) => execute(name); panel.Children.Add(button); actions.Add(button);
        }
        State.Text = state; Grid.SetRow(State, 3); grid.Children.Add(State); Grid.SetRow(Output, 4); grid.Children.Add(Output);
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(footer, 5); grid.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "AI 결과는 검증이 필요하다. 원래 앱의 내용은 자동으로 변경하지 않는다.", FontSize = 14 });
        Cancel.Click += (_, _) => cancel(); footer.Children.Add(Cancel); Closed += (_, _) => cancel(); SetBusy(false);
    }
    public void SetBusy(bool busy) { foreach (var button in actions) button.IsEnabled = !busy && Selection.Text.Length > 0; Cancel.IsEnabled = busy; }
}
