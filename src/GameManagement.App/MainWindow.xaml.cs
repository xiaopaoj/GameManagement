using System.Windows;
using GameManagement.ViewModels;

namespace GameManagement;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
