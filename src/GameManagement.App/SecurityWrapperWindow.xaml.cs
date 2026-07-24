using System.Windows;

namespace GameManagement;

public partial class SecurityWrapperWindow : Window
{
    public SecurityWrapperWindow() { InitializeComponent(); }
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var unlock = new SecurityUnlockWindow { Owner = this };
        if (unlock.ShowDialog() == true) DialogResult = true;
    }
}
