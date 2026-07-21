using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class GameRecordDeletionWindow : Window
{
    private readonly GameItem _game;

    public GameRecordDeletionOptions Options => new()
    {
        DeleteVersions = VersionsCheck.IsChecked == true,
        DeletePlayableDirectory = PlayableCheck.IsChecked == true,
        DeleteLocalSave = LocalSaveCheck.IsChecked == true,
        DeleteSystemSaveConfiguration = SystemSaveCheck.IsChecked == true,
        DeleteExternalBackups = ExternalBackupCheck.IsChecked == true,
        DeleteTemporaryDirectories = TemporaryCheck.IsChecked == true,
        DetachRetainedSources = SourceLinksCheck.IsChecked == true
    };

    public GameRecordDeletionWindow(AppState state, GameItem game)
    {
        InitializeComponent();
        _game = game;
        DescriptionText.Text = $"删除游戏“{game.DisplayName}”的主记录。请选择需要同步清理的关联数据；未勾选且仍存在的关联数据会阻止删除。";
        NamePromptText.Text = $"请输入完整游戏名称“{game.DisplayName}”完成最终确认：";
        VersionsCheck.IsEnabled = game.Versions.Count > 0;
        PlayableCheck.IsEnabled = !string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath);
        LocalSaveCheck.IsEnabled = GameRecordDeletionService.HasLocalSaveData(state, game);
        SystemSaveCheck.IsEnabled = GameRecordDeletionService.HasSystemSaveConfiguration(state, game);
        ExternalBackupCheck.IsEnabled = state.ExternalBackups.Any(item => item.GameId == game.Id);
        TemporaryCheck.IsEnabled = GameRecordDeletionService.HasTemporaryDirectories(state, game);
        SourceLinksCheck.IsEnabled = game.RetainedSourcePaths.Any(path => File.Exists(path) || Directory.Exists(path));
        Loaded += (_, _) => GameNameText.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!GameNameText.Text.Equals(_game.DisplayName, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "游戏名称不匹配，删除操作已取消。", "确认失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
