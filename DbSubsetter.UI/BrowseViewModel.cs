using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using DbSubsetter.Core;

namespace DbSubsetter.UI;

public class BrowseViewModel : INotifyPropertyChanged
{
    private readonly string _connectionString;
    private readonly Action<string, string> _useAsRoot;
    private readonly ISchemaExplorer _explorer;
    private readonly IList<TableSelection> _tableSelections;
    private CancellationTokenSource? _cts;

    private DataTable? _currentTable;
    private ListCollectionView? _tablesView;

    public BrowseViewModel(
        DatabaseProvider provider,
        string connectionString,
        Action<string, string> useAsRoot,
        IList<TableSelection> tableSelections,
        int browserRowLimit = 100)
    {
        _connectionString = connectionString;
        _useAsRoot = useAsRoot;
        _explorer = SchemaExplorerFactory.Create(provider);
        _tableSelections = tableSelections;
        _rowLimit = Math.Max(1, browserRowLimit);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UseAsRootCommand = new RelayCommand(UseAsRoot, CanUseAsRoot);
        ApplyWhereCommand = new AsyncRelayCommand(ApplyWhereAsync, () => SelectedTable is not null);
        ExecuteQueryCommand = new AsyncRelayCommand(ExecuteQueryAsync, () => !string.IsNullOrWhiteSpace(QueryText) && !IsQueryRunning);
        ClearTableFilterCommand = new RelayCommand(() => TableFilterText = string.Empty);

        // Build the filtered/sorted view over the shared TableSelections list
        _tablesView = new ListCollectionView((System.Collections.IList)_tableSelections);
        _tablesView.Filter = o => o is TableSelection ts &&
            (string.IsNullOrEmpty(_tableFilterText) ||
             ts.Name.Contains(_tableFilterText, StringComparison.OrdinalIgnoreCase));
        _tablesView.SortDescriptions.Add(new SortDescription(nameof(TableSelection.Name), ListSortDirection.Ascending));

        _ = RefreshAsync();
    }

    public ListCollectionView? TablesView => _tablesView;
    public ObservableCollection<ColumnInfo> Columns { get; } = new();

    // ── Table filter (live search) ───────────────────────────────────────────

    private string _tableFilterText = string.Empty;
    public string TableFilterText
    {
        get => _tableFilterText;
        set
        {
            _tableFilterText = value;
            OnPropertyChanged();
            _tablesView?.Refresh();
        }
    }

    // ── Selected table / columns / rows ─────────────────────────────────────

    private TableSelection? _selectedTableItem;
    public TableSelection? SelectedTableItem
    {
        get => _selectedTableItem;
        set
        {
            _selectedTableItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTable));
            WhereClause = value?.WhereClause ?? string.Empty;
            FilterText = string.Empty;
            if (value is not null)
                _ = LoadTableDataAsync(value.Name);
        }
    }

    public string? SelectedTable => _selectedTableItem?.Name;

    private DataView? _rowsView;
    public DataView? RowsView
    {
        get => _rowsView;
        set { _rowsView = value; OnPropertyChanged(); }
    }

    private int _rowLimit;
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
        set
        {
            _whereClause = value;
            OnPropertyChanged();
            // Keep the TableSelection's WhereClause in sync so it's saved to the project
            if (_selectedTableItem is not null)
                _selectedTableItem.WhereClause = value;
        }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    // ── SQL Query panel ──────────────────────────────────────────────────────

    private string _queryText = string.Empty;
    public string QueryText
    {
        get => _queryText;
        set { _queryText = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    private DataView? _queryResultsView;
    public DataView? QueryResultsView
    {
        get => _queryResultsView;
        set { _queryResultsView = value; OnPropertyChanged(); }
    }

    private string _queryStatus = string.Empty;
    public string QueryStatus
    {
        get => _queryStatus;
        set { _queryStatus = value; OnPropertyChanged(); }
    }

    private bool _isQueryRunning;
    public bool IsQueryRunning
    {
        get => _isQueryRunning;
        set { _isQueryRunning = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand RefreshCommand { get; }
    public ICommand UseAsRootCommand { get; }
    public ICommand ApplyWhereCommand { get; }
    public ICommand ExecuteQueryCommand { get; }
    public ICommand ClearTableFilterCommand { get; }

    // ── Data loading ─────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        Status = "Loading tables...";
        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var tables = await _explorer.GetTablesAsync(_connectionString, ct);

            // Sync: add any new tables to the shared TableSelections list
            var existingNames = _tableSelections.Select(ts => ts.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables.Where(t => !existingNames.Contains(t)))
                _tableSelections.Add(new TableSelection { Name = t, IsIncluded = true });

            _tablesView?.Refresh();
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

        Status = "Fetching with WHERE...";
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

    private async Task ExecuteQueryAsync()
    {
        if (string.IsNullOrWhiteSpace(QueryText)) return;

        IsQueryRunning = true;
        QueryResultsView = null;
        QueryStatus = "Executing...";

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            var dt = await _explorer.ExecuteQueryAsync(_connectionString, QueryText, ct);
            QueryResultsView = dt.DefaultView;
            QueryStatus = $"{dt.Rows.Count} row(s) returned, {dt.Columns.Count} column(s).";
        }
        catch (OperationCanceledException)
        {
            QueryStatus = "Query cancelled.";
        }
        catch (Exception ex)
        {
            QueryStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsQueryRunning = false;
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
