using System.Windows;
using GameManagement.Models;

namespace GameManagement;

public partial class FeatureStatusWindow : Window
{
    public FeatureStatusWindow(string featureName, string description, GameItem? game = null)
    {
        InitializeComponent();
        Title = featureName;
        FeatureNameText.Text = featureName;
        DescriptionText.Text = description;
        GameText.Text = game is null ? "全局功能" : $"当前游戏：{game.DisplayName}\n当前版本：{game.CurrentVersionName}";
    }
}
