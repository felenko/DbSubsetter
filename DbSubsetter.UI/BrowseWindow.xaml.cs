using System.Collections.Generic;
using System.Windows;
using DbSubsetter.Core;

namespace DbSubsetter.UI;

public partial class BrowseWindow : Window
{
    private readonly BrowseViewModel _vm;

    public BrowseWindow(
        DatabaseProvider provider,
        string connectionString,
        Action<string, string> useAsRoot,
        IList<TableSelection> tableSelections,
        int browserRowLimit = 100)
    {
        InitializeComponent();
        _vm = new BrowseViewModel(provider, connectionString, useAsRoot, tableSelections, browserRowLimit);
        DataContext = _vm;
    }

    /// <summary>Returns the row limit the user had set when closing the browser, so it can be persisted.</summary>
    public int CurrentRowLimit => _vm.RowLimit;
}
