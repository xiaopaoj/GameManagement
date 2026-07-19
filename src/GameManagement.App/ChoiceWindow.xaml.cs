using System.Windows;
using System.Windows.Input;
using GameManagement.Models;

namespace GameManagement;

public partial class ChoiceWindow : Window
{
    public ChoiceItem? SelectedChoice => ChoiceGrid.SelectedItem as ChoiceItem;

    public ChoiceWindow(string title, string prompt, IEnumerable<ChoiceItem> choices)
    {
        InitializeComponent(); Title = title; PromptText.Text = prompt; ChoiceGrid.ItemsSource = choices.ToList();
        if (ChoiceGrid.Items.Count > 0) ChoiceGrid.SelectedIndex = 0;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChoice is null) { MessageBox.Show("请选择一项。", "选择提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }

    private void ChoiceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedChoice is not null) DialogResult = true;
    }
}
