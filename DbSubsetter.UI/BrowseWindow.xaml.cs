using System.Windows;
using DbSubsetter.Core;

namespace DbSubsetter.UI;

public partial class BrowseWindow : Window
{
    public BrowseWindow(DatabaseProvider provider, string connectionString, Action<string, string> useAsRoot)
    {
        InitializeComponent();
        DataContext = new BrowseViewModel(provider, connectionString, useAsRoot);
    }

    public BrowseWindow(string connectionString, Action<string, string> useAsRoot)
        : this(DatabaseProvider.SqlServer, connectionString, useAsRoot)
    {
    }
}
