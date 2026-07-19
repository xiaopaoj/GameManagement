using System.Windows;
using GameManagement.Models;

namespace GameManagement;

public partial class DeletionHistoryWindow : Window
{
    public DeletionHistoryWindow(AppState state)
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = state.DeletionHistory.OrderByDescending(item => item.CreatedAt).ToList();
    }
}
