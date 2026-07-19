using System.Windows;

namespace GameManagement;

public partial class TextInputWindow : Window
{
    private readonly bool _allowEmpty;
    public string Value => ValueText.Text;
    public TextInputWindow(string title, string prompt, string initialValue = "", bool allowEmpty = false)
    {
        _allowEmpty = allowEmpty;
        InitializeComponent(); Title = title; PromptText.Text = prompt; ValueText.Text = initialValue;
        Loaded += (_, _) => { ValueText.Focus(); ValueText.SelectAll(); };
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowEmpty && string.IsNullOrWhiteSpace(ValueText.Text)) { MessageBox.Show("内容不能为空。", "输入提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
