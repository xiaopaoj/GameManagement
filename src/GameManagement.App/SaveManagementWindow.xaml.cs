using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class SaveManagementWindow : Window
{
    private readonly AppState _state;
    private readonly Action<string> _save;
    private readonly GameItem? _lockedGame;

    public SaveManagementWindow(AppState state, Action<string> save, GameItem? game = null, int initialTab = 0, string snapshotFilter = "全部")
    {
        InitializeComponent();
        _state = state;
        _save = save;
        _lockedGame = game;
        DecisionCombo.ItemsSource = new[] { "全部", SaveCandidateDecisions.Pending, SaveCandidateDecisions.Confirmed, SaveCandidateDecisions.Excluded };
        DecisionCombo.SelectedIndex = 0;
        SnapshotKindCombo.ItemsSource = new[] { "全部", SaveSnapshotKinds.Normal, SaveSnapshotKinds.Abnormal };
        SnapshotKindCombo.SelectedItem = snapshotFilter;
        LoadGameChoices(game);
        ManagementTabs.SelectedIndex = initialTab;
        RefreshLists();
    }

    private void LoadGameChoices(GameItem? selectedGame)
    {
        var choices = new List<ChoiceItem>();
        if (_lockedGame is null) choices.Add(new ChoiceItem { Name = "所有游戏", Value = null });
        choices.AddRange((_lockedGame is null ? _state.Games : [_lockedGame]).OrderBy(game => game.DisplayName).Select(game => new ChoiceItem { Name = game.DisplayName, Value = game }));
        GameCombo.ItemsSource = choices;
        GameCombo.SelectedItem = selectedGame is null ? choices.FirstOrDefault() : choices.FirstOrDefault(item => item.Value is GameItem game && game.Id == selectedGame.Id);
        GameCombo.IsEnabled = _lockedGame is null;
    }

    private GameItem? SelectedGame => (GameCombo.SelectedItem as ChoiceItem)?.Value as GameItem;
    private SaveCandidateItem? SelectedCandidate => CandidateGrid.SelectedItem as SaveCandidateItem;
    private SaveSnapshotItem? SelectedSnapshot => SnapshotGrid.SelectedItem as SaveSnapshotItem;

    private void RefreshLists()
    {
        var game = SelectedGame;
        var decision = DecisionCombo.SelectedItem as string ?? "全部";
        var candidates = _state.SaveCandidates.AsEnumerable();
        if (game is not null) candidates = candidates.Where(item => item.GameId == game.Id);
        if (decision != "全部") candidates = candidates.Where(item => item.Decision == decision);
        CandidateGrid.ItemsSource = candidates.OrderByDescending(item => item.DetectedAt).ThenBy(item => item.RelativePath).ToList();

        var snapshotKind = SnapshotKindCombo.SelectedItem as string ?? "全部";
        var snapshots = _state.SaveSnapshots.AsEnumerable();
        if (game is not null) snapshots = snapshots.Where(item => item.GameId == game.Id);
        if (snapshotKind != "全部") snapshots = snapshots.Where(item => item.SnapshotKind == snapshotKind);
        SnapshotGrid.ItemsSource = snapshots.OrderByDescending(item => item.CreatedAt).ToList();
        StatusText.Text = $"候选 {_state.SaveCandidates.Count(item => item.Decision == SaveCandidateDecisions.Pending)} 个｜快照 {_state.SaveSnapshots.Count} 个";
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) RefreshLists();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame;
        if (game is null) { ShowError("请先选择一个具体游戏。 "); return; }
        var kindChoice = MessageBox.Show("本次人工扫描结果应标记为什么类型？\n\n“是”=正常快照候选\n“否”=异常快照候选\n“取消”=不扫描", "选择快照类型", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (kindChoice == MessageBoxResult.Cancel) return;
        var kind = kindChoice == MessageBoxResult.Yes ? SaveSnapshotKinds.Normal : SaveSnapshotKinds.Abnormal;
        var progress = new PreparationProgressWindow("正在扫描存档变化") { Owner = this };
        try
        {
            IsEnabled = false; progress.Show(); progress.UpdateStatus("正在比较游戏目录与首次文件基线…", 25);
            var candidates = await SaveChangeDetectionService.DetectAsync(_state, game, kind);
            SaveChangeDetectionService.ReplaceDetectedCandidates(_state, game, candidates);
            var automaticCandidates = candidates.Where(item => item.PreviouslyConfirmed && item.ChangeType != "删除" && item.Decision == SaveCandidateDecisions.Pending).Select(item => item.Id).ToList();
            SaveSnapshotCreationResult? automaticResult = null;
            if (automaticCandidates.Count > 0) automaticResult = await SaveSnapshotService.ApplyAndCreateAsync(_state, game, automaticCandidates);
            _save(automaticResult?.ContentChanged == true ? $"扫描完成并自动创建{kind}存档快照" : $"存档变化扫描完成，发现 {candidates.Count} 个变化");
            progress.UpdateStatus("扫描完成", 100);
            RefreshLists();
            var pendingCount = candidates.Count(item => item.Decision == SaveCandidateDecisions.Pending);
            MessageBox.Show(candidates.Count == 0 ? "没有发现游戏目录变化。" : automaticResult?.ContentChanged == true ? $"已确认规则发生变化并自动创建{kind}快照；另有 {pendingCount} 个候选需要人工处理。" : $"发现 {pendingCount} 个待确认变化，请在候选列表中确认或排除。", "扫描完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { AppLogger.Error("扫描存档变化失败", ex); ShowError(ex.Message); }
        finally { IsEnabled = true; progress.CloseSafely(); }
    }

    private async void ConfirmCandidates_Click(object sender, RoutedEventArgs e)
    {
        var selected = CandidateGrid.SelectedItems.OfType<SaveCandidateItem>().Where(item => item.Decision == SaveCandidateDecisions.Pending).ToList();
        if (selected.Count == 0) { ShowError("请选择至少一个待确认候选。 "); return; }
        if (selected.Select(item => item.GameId).Distinct().Count() != 1) { ShowError("一次只能确认同一个游戏的候选文件。 "); return; }
        if (selected.Select(item => item.SnapshotKind).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) { ShowError("一次只能确认同一种正常或异常快照候选。 "); return; }
        var game = _state.Games.FirstOrDefault(item => item.Id == selected[0].GameId);
        if (game is null) { ShowError("候选关联的游戏不存在。 "); return; }
        if (selected.Any(item => item.DefaultExcluded) && MessageBox.Show("所选文件中包含默认排除项。确认后将把这些具体文件标记为存档，并覆盖默认排除规则。是否继续？", "默认排除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (selected.Any(item => item.ChangeType == "删除") && MessageBox.Show("所选变化包含已删除文件。继续会从当前本地存档中永久删除对应副本，但已有历史快照不受影响。是否继续？", "删除存档文件确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var progress = new PreparationProgressWindow("正在创建本地存档快照") { Owner = this };
        try
        {
            IsEnabled = false; progress.Show(); progress.UpdateStatus("正在更新 current 存档并计算 Hash…", 35);
            var result = await SaveSnapshotService.ApplyAndCreateAsync(_state, game, selected.Select(item => item.Id).ToList());
            _save(result.ContentChanged ? $"已创建{result.Snapshot?.SnapshotKind}存档快照" : "候选已确认，但本地存档内容没有变化，未创建快照");
            progress.UpdateStatus("存档处理完成", 100); RefreshLists();
            MessageBox.Show(result.ContentChanged ? $"快照创建并校验完成：\n{result.Snapshot?.DirectoryPath}" : "候选已确认，但 current 内容没有变化，因此未生成重复快照。", "处理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { AppLogger.Error("创建本地存档快照失败", ex); ShowError(ex.Message); }
        finally { IsEnabled = true; progress.CloseSafely(); }
    }

    private void ExcludeCandidates_Click(object sender, RoutedEventArgs e)
    {
        var selected = CandidateGrid.SelectedItems.OfType<SaveCandidateItem>().Where(item => item.Decision == SaveCandidateDecisions.Pending).ToList();
        if (selected.Count == 0) { ShowError("请选择至少一个待确认候选。 "); return; }
        if (selected.Select(item => item.GameId).Distinct().Count() != 1) { ShowError("一次只能排除同一个游戏的候选文件。 "); return; }
        var game = _state.Games.First(item => item.Id == selected[0].GameId);
        SaveSnapshotService.ExcludeCandidates(_state, game, selected.Select(item => item.Id));
        _save($"已按具体文件排除 {selected.Count} 个存档候选"); RefreshLists();
    }

    private void CompleteInitialScan_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame;
        if (game is null) { ShowError("请先选择一个具体游戏。 "); return; }
        if (game.SystemSaveInitialScanCompleted) { StatusText.Text = "该游戏的首次系统存档扫描已经确认完成。"; return; }
        var pendingSystemCandidates = _state.SaveCandidates.Count(item => item.GameId == game.Id && item.SourceKind == "系统目录" && item.Decision == SaveCandidateDecisions.Pending);
        if (pendingSystemCandidates > 0 && MessageBox.Show($"仍有 {pendingSystemCandidates} 个系统目录候选未处理。确认完成后，后续运行将只监控已配置目录。是否仍要完成首次扫描？", "首次扫描完成确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SystemSaveMonitoringService.MarkInitialScanCompleted(game); _save("首次系统存档扫描已确认完成"); RefreshLists();
    }

    private void PreviewCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null) return;
        new FilePreviewWindow(SelectedCandidate.SourcePath) { Owner = this }.ShowDialog();
    }

    private void CandidateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PreviewCandidate_Click(sender, e);

    private void LocateCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null) return;
        if (File.Exists(SelectedCandidate.SourcePath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SelectedCandidate.SourcePath}\"") { UseShellExecute = true });
        else if (Directory.Exists(Path.GetDirectoryName(SelectedCandidate.SourcePath))) ShellService.OpenFolder(Path.GetDirectoryName(SelectedCandidate.SourcePath)!);
        else ShowError("候选文件及其目录已经不存在。 ");
    }

    private void OpenSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSnapshot is null) return;
        if (!Directory.Exists(SelectedSnapshot.DirectoryPath)) { ShowError("快照目录不存在。 "); return; }
        ShellService.OpenFolder(SelectedSnapshot.DirectoryPath);
    }

    private void OpenCurrentSave_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame;
        if (game is null) { ShowError("请先选择一个具体游戏。 "); return; }
        try
        {
            var path = GameSavePathService.GetCurrentDirectory(_state, game);
            if (!Directory.Exists(path)) { ShowError("该游戏尚未生成当前本地存档。 "); return; }
            ShellService.OpenFolder(path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenCurrentManifest_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame;
        if (game is null) { ShowError("请先选择一个具体游戏。 "); return; }
        try
        {
            var path = Path.Combine(GameSavePathService.GetGameSaveRoot(_state, game), "manifest.json");
            if (!File.Exists(path)) { ShowError("该游戏尚未生成当前存档清单。 "); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenManifest_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSnapshot is null) return;
        if (!File.Exists(SelectedSnapshot.ManifestPath)) { ShowError("快照清单不存在。 "); return; }
        Process.Start(new ProcessStartInfo(SelectedSnapshot.ManifestPath) { UseShellExecute = true });
    }

    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot;
        if (snapshot is null) return;
        if (MessageBox.Show($"将永久删除所选本地快照，此操作不可恢复：\n{snapshot.DirectoryPath}\n\n是否继续？", "删除快照确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { SaveSnapshotService.RemoveSnapshot(_state, snapshot); _save("本地存档快照已删除"); RefreshLists(); }
        catch (Exception ex) { AppLogger.Error("删除本地存档快照失败", ex); ShowError(ex.Message); }
    }

    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
