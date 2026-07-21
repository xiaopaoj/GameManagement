using System.Windows;
using System.Windows.Controls;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class ExtractionTemplateWindow : Window
{
    private readonly AppState _state;
    private readonly Action<string> _save;

    public ExtractionTemplateWindow(AppState state, Action<string> save)
    {
        InitializeComponent();
        _state = state;
        _save = save;
        RefreshList();
    }

    private ExtractionTemplateItem? Selected => TemplateGrid.SelectedItem as ExtractionTemplateItem;

    private void RefreshList(Guid? selectedId = null)
    {
        TemplateGrid.ItemsSource = _state.ExtractionTemplates.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (selectedId is Guid id) TemplateGrid.SelectedItem = TemplateGrid.Items.Cast<ExtractionTemplateItem>().FirstOrDefault(item => item.Id == id);
    }

    private void TemplateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } template) return;
        NameText.Text = template.Name;
        FirstPasswordText.Text = CredentialService.Decrypt(template.EncryptedFirstPassword, template.Id);
        SecondPasswordText.Text = CredentialService.Decrypt(template.EncryptedSecondPassword, template.Id);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInput()) return;
        var template = new ExtractionTemplateItem { Name = NameText.Text.Trim() };
        ExtractionTemplateService.SetPasswords(template, FirstPasswordText.Text, SecondPasswordText.Text);
        _state.ExtractionTemplates.Add(template);
        _save("解压流程模板已新增");
        RefreshList(template.Id);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } template || !ValidateInput()) return;
        template.Name = NameText.Text.Trim();
        ExtractionTemplateService.SetPasswords(template, FirstPasswordText.Text, SecondPasswordText.Text);
        _save("解压流程模板已更新");
        RefreshList(template.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } template) return;
        if (_state.Games.Any(game => game.ExtractionTemplateId == template.Id)) { MessageBox.Show("仍有游戏正在使用该模板，请先取消或更换模板。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show($"确定删除模板“{template.Name}”吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.ExtractionTemplates.Remove(template);
        _save("解压流程模板已删除");
        NameText.Clear(); FirstPasswordText.Clear(); SecondPasswordText.Clear(); RefreshList();
    }

    private bool ValidateInput()
    {
        if (!string.IsNullOrWhiteSpace(NameText.Text) && !_state.ExtractionTemplates.Any(item => item.Id != Selected?.Id && item.Name.Equals(NameText.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))) return true;
        MessageBox.Show("模板名称不能为空且不能重复。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
