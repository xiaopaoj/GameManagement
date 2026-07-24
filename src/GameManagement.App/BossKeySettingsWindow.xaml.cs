using System.Windows;
using GameManagement.Services;

namespace GameManagement;

public partial class BossKeySettingsWindow : Window
{
    private sealed record KeyOption(string Name, int VirtualKey);
    public BossKeyConfiguration Configuration { get; private set; }

    public BossKeySettingsWindow(BossKeyConfiguration configuration)
    {
        InitializeComponent();
        Configuration = configuration;
        var options = Enumerable.Range('A', 26).Select(value => new KeyOption(((char)value).ToString(), value))
            .Concat(Enumerable.Range('0', 10).Select(value => new KeyOption(((char)value).ToString(), value)))
            .Concat(Enumerable.Range(1, 12).Select(number => new KeyOption($"F{number}", 0x6F + number))).ToList();
        KeyChoice.ItemsSource = options;
        EnabledCheck.IsChecked = configuration.Enabled;
        ControlCheck.IsChecked = configuration.Modifiers.HasFlag(BossKeyModifiers.Control);
        AltCheck.IsChecked = configuration.Modifiers.HasFlag(BossKeyModifiers.Alt);
        ShiftCheck.IsChecked = configuration.Modifiers.HasFlag(BossKeyModifiers.Shift);
        WindowsCheck.IsChecked = configuration.Modifiers.HasFlag(BossKeyModifiers.Windows);
        KeyChoice.SelectedItem = options.FirstOrDefault(option => option.VirtualKey == configuration.VirtualKey) ?? options[1];
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var modifiers = (ControlCheck.IsChecked == true ? BossKeyModifiers.Control : 0)
            | (AltCheck.IsChecked == true ? BossKeyModifiers.Alt : 0)
            | (ShiftCheck.IsChecked == true ? BossKeyModifiers.Shift : 0)
            | (WindowsCheck.IsChecked == true ? BossKeyModifiers.Windows : 0);
        if (EnabledCheck.IsChecked == true && (modifiers == 0 || KeyChoice.SelectedItem is not KeyOption))
        {
            MessageBox.Show(this, "启用老板键时必须选择至少一个修饰键和一个普通按键。", "组合键无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var key = KeyChoice.SelectedItem as KeyOption;
        Configuration = new BossKeyConfiguration(EnabledCheck.IsChecked == true, modifiers, key?.VirtualKey ?? 0x42);
        DialogResult = true;
    }
}
