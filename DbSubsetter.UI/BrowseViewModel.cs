using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DbSubsetter.Core;

namespace DbSubsetter.UI;

public class BrowseViewModel : INotifyPropertyChanged
{
    private readonly string _connectionString;
    private readonly Action<string, string> _useAsRoot;
    private readonly ISchemaExplorer _explorer;
    private CancellationTokenSource? _cts;

    private DataTable? _currentTable;

    public BrowseViewModel(DatabaseProvider provider, string connectionString, Action<string, string> useAsRoot)
    {
        _connectionString = connectionString;
        _useAsRoot = useAsRoot;
        _explorer = SchemaExplorerFactory.Create(provider);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UseAsRootCommand = new RelayCommand(UseAsRoot, CanUseAsRoot);
        ApplyWhereCommand = new AsyncRelayCommand(ApplyWhereAsync, () => SelectedTable is not null);

        _ = RefreshAsync();
    }

    [Obsolete("Use constructor with DatabaseProvider")]
    public BrowseViewModel(string connectionString, Action<string, string> useAsRoot)
        : this(DatabaseProvider.SqlServer, connectionString, useAsRoot)
    {
    }

    public ObservableCollection<string> Tables { get; } = new();
    public ObservableCollection<ColumnInfo> Columns { get; } = new();

    private string? _selectedTable;
    public string? SelectedTable
    {
        get => _selectedTable;
        set
        {
            _selectedTable = value;
            OnPropertyChanged();
            WhereClause = string.Empty;
            FilterText = string.Empty;
            if (value is not null)
                _ = LoadTableDataAsync(value);
        }
    }

    private DataView? _rowsView;
    public DataView? RowsView
    {
        get => _rowsView;
        set { _rowsView = value; OnPropertyChanged(); }
    }

    private int _rowLimit = 100;
    public int RowLimit
    {
        get => _rowLimit;
        set { _rowLimit = Math.Max(1, value); OnPropertyChanged(); }
    }

    private DataRowView? _selectedRow;
    public DataRowView? SelectedRow
    {
        get => _selectedRow;
        set { _selectedRow = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value;
            OnPropertyChanged();
            ApplyClientFilter();
        }
    }

    private string _whereClause = string.Empty;
    public string WhereClause
    {
        get => _whereClause;
        set { _whereClause = value; OnPropertyChanged(); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public ICommand RefreshCommand { get; }
    public ICommand UseAsRootCommand { get; }
    public ICommand ApplyWhereCommand { get; }

    private async Task RefreshAsync()
    {
        Status = "Loading tables...";
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var tables = await _explorer.GetTablesAsync(_connectionString, _cts.Token);
            Tables.Clear();
            foreach (var t in tables)
                Tables.Add(t);

            Status = $"{tables.Count} tables loaded.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    private async Task LoadTableDataAsync(string table)
    {
        Status = $"Loading {table}...";
        Columns.Clear();
        RowsView = null;
        SelectedRow = null;
        _currentTable = null;

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var colsTask = _explorer.GetTableColumnsAsync(_connectionString, table, ct);
            var rowsTask = _explorer.GetTableRowsAsync(_connectionString, table, RowLimit, whereClause: null, ct);

            await Task.WhenAll(colsTask, rowsTask);

            foreach (var col in colsTask.Result)
                Columns.Add(col);

            _currentTable = rowsTask.Result;
            RowsView = _currentTable.DefaultView;
            Status = $"{Columns.Count} columns, {_currentTable.Rows.Count} rows (limit {RowLimit}).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    private async Task ApplyWhereAsync()
    {
        if (SelectedTable is null) return;

        Status = $"Fetching with WHERE...";
        RowsView = null;
        SelectedRow = null;
        _currentTable = null;
        FilterText = string.Empty;

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var dt = await _explorer.GetTableRowsAsync(
                _connectionString, SelectedTable, RowLimit, WhereClause, ct);

            _currentTable = dt;
            RowsView = _currentTable.DefaultView;
            Status = $"{_currentTable.Rows.Count} rows (limit {RowLimit})" +
                     (string.IsNullOrWhiteSpace(WhereClause) ? "." : " with WHERE filter.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"WHERE error: {ex.Message}";
        }
    }

    private void ApplyClientFilter()
    {
        if (RowsView is null) return;

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            RowsView.RowFilter = string.Empty;
            return;
        }

        try
        {
            var escaped = FilterText.Replace("'", "''");
            var conditions = RowsView.Table!.Columns
                .Cast<DataColumn>()
                .Select(c => $"CONVERT([{c.ColumnName.Replace("]", "]]")}], System.String) LIKE '%{escaped}%'");
            RowsView.RowFilter = string.Join(" OR ", conditions);
        }
        catch
        {
            // Ignore malformed filter expressions
        }
    }

    private bool CanUseAsRoot() => SelectedRow is not null && SelectedTable is not null;

    private void UseAsRoot()
    {
        if (SelectedRow is null || SelectedTable is null) return;

        var pkCol = Columns.FirstOrDefault(c => c.IsPrimaryKey);
        if (pkCol is null)
        {
            Status = "No PK column found for this table.";
            return;
        }

        var pkValue = SelectedRow[pkCol.Name]?.ToString() ?? string.Empty;
        _useAsRoot(SelectedTable, pkValue);
        Status = $"Sent: table={SelectedTable}, PK={pkValue}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
