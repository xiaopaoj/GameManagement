using System.Windows;

namespace GameManagement;

public partial class GameEditWindow : Window
{
    public string GameName => NameText.Text.Trim();
    public string Note => NoteText.Text.Trim();

    public GameEditWindow(string gameName, string note)
    {
        InitializeComponent();
        NameText.Text = gameName;
        NoteText.Text = note;
        Loaded += (_, _) => { NameText.Focus(); NameText.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameText.Text))
        {
            MessageBox.Show(this, "游戏名称不能为空。", "保存提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
