using System.Windows;

namespace DbSubsetter.UI;

public partial class BrowseWindow : Window
{
    public BrowseWindow(string connectionString, Action<string, string> useAsRoot)
    {
        InitializeComponent();
        DataContext = new BrowseViewModel(connectionString, useAsRoot);
    }
}
