using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DbSubsetter.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Auto-scroll log to bottom
        if (DataContext is MainViewModel vm)
        {
            vm.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel newVm)
                newVm.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
        };

        // Keyboard shortcuts for project commands
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New,
            (_, _) => (DataContext as MainViewModel)?.NewProjectCommand.Execute(null)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open,
            (_, _) => (DataContext as MainViewModel)?.OpenProjectCommand.Execute(null)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,
            (_, _) => (DataContext as MainViewModel)?.SaveProjectCommand.Execute(null)));
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }

    private void DestPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.DestPassword = pb.Password;
    }
}
