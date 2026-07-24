using System.Windows;

namespace GameManagement;

public partial class SecurityWrapperWindow : Window
{
    public SecurityWrapperWindow() { InitializeComponent(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void IntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        var unlock = new SecurityUnlockWindow { Owner = this };
        if (unlock.ShowDialog() == true) DialogResult = true;
    }
}
