using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DbSubsetter.Core;
using Microsoft.Win32;

namespace DbSubsetter.UI;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ProfileManager _profileManager = new();

    private ISchemaExplorer GetExplorer() => SchemaExplorerFactory.Create(Provider);
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _cts;
    private Stopwatch? _stopwatch;

    public MainViewModel()
    {
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_stopwatch is not null)
                ElapsedTime = _stopwatch.Elapsed.ToString(@"mm\:ss");
        };

        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsRunning);
        TestDestConnectionCommand = new AsyncRelayCommand(TestDestConnectionAsync, () => !IsRunning);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        RunSubsetCommand = new AsyncRelayCommand(RunSubsetAsync, CanRunSubset);
        CancelCommand = new RelayCommand(CancelRun, () => IsRunning);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !string.IsNullOrWhiteSpace(Server));
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        RefreshTablesCommand = new AsyncRelayCommand(RefreshTablesAsync, () => !IsRunning && ConnectionOk);
        SelectAllTablesCommand = new RelayCommand(SelectAllTables);
        DeselectAllTablesCommand = new RelayCommand(DeselectAllTables);
        LoadRootCandidatesCommand = new AsyncRelayCommand(LoadRootCandidatesAsync, CanLoadRootCandidates);
        OpenBrowserCommand = new RelayCommand(OpenBrowser, () => ConnectionOk);
        BrowseDatabaseFileCommand = new RelayCommand(BrowseDatabaseFile);

        GoToStepCommand = new RelayCommand(param =>
        {
            if (int.TryParse(param?.ToString(), out int step) && CanGoToStep(step))
                CurrentStep = step;
        }, param => int.TryParse(param?.ToString(), out int s) && CanGoToStep(s));
        NextStepCommand = new RelayCommand(() => CurrentStep++, () => CanGoNext);
        PreviousStepCommand = new RelayCommand(() => CurrentStep--, () => CanGoBack);

        Profiles = new ObservableCollection<ConnectionProfile>(_profileManager.Load());
        MaxRowsPerTable = 1000;
        IntegratedSecurity = true;
        DestIntegratedSecurity = true;
        IsFileMode = true;
        OutputFile = "subset.sql";
    }

    // Connection
    private DatabaseProvider _provider = DatabaseProvider.SqlServer;
    public DatabaseProvider Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            if (value == DatabaseProvider.MySql || value == DatabaseProvider.Postgres)
                IntegratedSecurity = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSqlServer));
            OnPropertyChanged(nameof(IsSQLite));
            OnPropertyChanged(nameof(IsMySqlOrPostgres));
            OnPropertyChanged(nameof(ShowServerDatabase));
            OnPropertyChanged(nameof(ShowIntegratedSecurity));
            OnPropertyChanged(nameof(UserPasswordEnabled));
            OnPropertyChanged(nameof(ServerLabel));
        }
    }
    public Array ProviderList => Enum.GetValues(typeof(DatabaseProvider));
    public bool IsSqlServer => Provider == DatabaseProvider.SqlServer;
    public bool IsSQLite => Provider == DatabaseProvider.SQLite;
    public bool IsMySqlOrPostgres => Provider == DatabaseProvider.MySql || Provider == DatabaseProvider.Postgres;
    /// <summary>Show Server + Database fields (SQL Server, MySQL, Postgres).</summary>
    public bool ShowServerDatabase => IsSqlServer || IsMySqlOrPostgres;
    /// <summary>Show Integrated Security checkbox (SQL Server only).</summary>
    public bool ShowIntegratedSecurity => IsSqlServer;
    /// <summary>User/Password enabled when SQL auth (SQL Server) or always for MySQL/Postgres.</summary>
    public bool UserPasswordEnabled => IsMySqlOrPostgres || (IsSqlServer && SqlAuthEnabled);
    public string ServerLabel => IsSQLite ? "Database file:" : "Server:";

    private string _server = string.Empty;
    public string Server
    {
        get => _server;
        set { _server = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionOk)); }
    }

    private string _database = string.Empty;
    public string Database
    {
        get => _database;
        set { _database = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionOk)); }
    }

    private bool _integratedSecurity = true;
    public bool IntegratedSecurity
    {
        get => _integratedSecurity;
        set { _integratedSecurity = value; OnPropertyChanged(); OnPropertyChanged(nameof(SqlAuthEnabled)); }
    }

    public bool SqlAuthEnabled => !IntegratedSecurity;

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }

    private string _connectionStatus = string.Empty;
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set { _connectionStatus = value; OnPropertyChanged(); }
    }

    private bool _connectionOk;
    public bool ConnectionOk
    {
        get => _connectionOk;
        set { _connectionOk = value; OnPropertyChanged(); NotifyStepCompletion(); }
    }

    // Destination connection
    private string _destServer = string.Empty;
    public string DestServer
    {
        get => _destServer;
        set { _destServer = value; OnPropertyChanged(); }
    }

    private string _destDatabase = string.Empty;
    public string DestDatabase
    {
        get => _destDatabase;
        set { _destDatabase = value; OnPropertyChanged(); }
    }

    private bool _destIntegratedSecurity = true;
    public bool DestIntegratedSecurity
    {
        get => _destIntegratedSecurity;
        set { _destIntegratedSecurity = value; OnPropertyChanged(); OnPropertyChanged(nameof(DestSqlAuthEnabled)); }
    }

    public bool DestSqlAuthEnabled => !DestIntegratedSecurity;

    private string _destUsername = string.Empty;
    public string DestUsername
    {
        get => _destUsername;
        set { _destUsername = value; OnPropertyChanged(); }
    }

    private string _destPassword = string.Empty;
    public string DestPassword
    {
        get => _destPassword;
        set { _destPassword = value; OnPropertyChanged(); }
    }

    private string _destConnectionStatus = string.Empty;
    public string DestConnectionStatus
    {
        get => _destConnectionStatus;
        set { _destConnectionStatus = value; OnPropertyChanged(); }
    }

    private bool _destConnectionOk;
    public bool DestConnectionOk
    {
        get => _destConnectionOk;
        set { _destConnectionOk = value; OnPropertyChanged(); NotifyStepCompletion(); }
    }

    // Output mode
    private bool _isFileMode = true;
    public bool IsFileMode
    {
        get => _isFileMode;
        set
        {
            _isFileMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDbMode));
            OnPropertyChanged(nameof(FilePanelVisibility));
            OnPropertyChanged(nameof(DbPanelVisibility));
        }
    }

    public bool IsDbMode
    {
        get => !_isFileMode;
        set
        {
            _isFileMode = !value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFileMode));
            OnPropertyChanged(nameof(FilePanelVisibility));
            OnPropertyChanged(nameof(DbPanelVisibility));
            NotifyStepCompletion();
        }
    }

    public Visibility FilePanelVisibility => _isFileMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DbPanelVisibility => !_isFileMode ? Visibility.Visible : Visibility.Collapsed;

    private string _outputFile = "subset.sql";
    public string OutputFile
    {
        get => _outputFile;
        set { _outputFile = value; OnPropertyChanged(); NotifyStepCompletion(); }
    }

    // Profiles
    public ObservableCollection<ConnectionProfile> Profiles { get; }

    private ConnectionProfile? _selectedProfile;
    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            _selectedProfile = value;
            OnPropertyChanged();
            if (value is not null)
            {
                Provider = value.Provider;
                Server = value.Server;
                Database = value.Database;
                IntegratedSecurity = value.IntegratedSecurity;
                Username = value.Username;
                Password = value.Password;
            }
        }
    }

    // Configuration
    public ObservableCollection<string> Tables { get; } = new();
    public ObservableCollection<TableSelection> TableSelections { get; } = new();

    private string? _selectedTable;
    public string? SelectedTable
    {
        get => _selectedTable;
        set
        {
            _selectedTable = value;
            OnPropertyChanged();
            NotifyStepCompletion();
            if (value is not null)
                _ = LoadPrimaryKeyAsync(value);
        }
    }

    private string _primaryKeyColumn = string.Empty;
    public string PrimaryKeyColumn
    {
        get => _primaryKeyColumn;
        set { _primaryKeyColumn = value; OnPropertyChanged(); }
    }

    private string _rootPkValue = string.Empty;
    public string RootPkValue
    {
        get => _rootPkValue;
        set { _rootPkValue = value; OnPropertyChanged(); NotifyStepCompletion(); }
    }

    /// <summary>Sample rows from the root table for choosing the root entry (PK value).</summary>
    public ObservableCollection<RootRowCandidate> RootRowCandidates { get; } = new();

    private RootRowCandidate? _selectedRootRow;
    public RootRowCandidate? SelectedRootRow
    {
        get => _selectedRootRow;
        set
        {
            _selectedRootRow = value;
            OnPropertyChanged();
            if (value is not null)
                RootPkValue = value.PkValue;
        }
    }

    private int _maxRowsPerTable = 1000;
    public int MaxRowsPerTable
    {
        get => _maxRowsPerTable;
        set { _maxRowsPerTable = Math.Max(1, value); OnPropertyChanged(); }
    }

    // Execution state
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotRunning)); }
    }

    public bool IsNotRunning => !IsRunning;

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set { _isProgressIndeterminate = value; OnPropertyChanged(); }
    }

    private string _currentTable = string.Empty;
    public string CurrentTable
    {
        get => _currentTable;
        set { _currentTable = value; OnPropertyChanged(); }
    }

    private string _elapsedTime = "00:00";
    public string ElapsedTime
    {
        get => _elapsedTime;
        set { _elapsedTime = value; OnPropertyChanged(); }
    }

    private long _totalInserts;
    public long TotalInserts
    {
        get => _totalInserts;
        set { _totalInserts = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> LogEntries { get; } = new();

    // Commands
    public ICommand TestConnectionCommand { get; }
    public ICommand TestDestConnectionCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand RunSubsetCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand RefreshTablesCommand { get; }
    public ICommand SelectAllTablesCommand { get; }
    public ICommand DeselectAllTablesCommand { get; }
    public ICommand LoadRootCandidatesCommand { get; }
    public ICommand OpenBrowserCommand { get; }
    public ICommand BrowseDatabaseFileCommand { get; }
    public ICommand GoToStepCommand { get; }
    public ICommand NextStepCommand { get; }
    public ICommand PreviousStepCommand { get; }

    private void OpenBrowser()
    {
        var win = new BrowseWindow(Provider, BuildConnectionString(), (table, pkVal) =>
        {
            SelectedTable = table;
            RootPkValue = pkVal;
            AddLog($"Browser: selected {table} PK={pkVal}");
        });
        win.Owner = Application.Current.MainWindow;
        win.Show();
    }

    private void BrowseOutput()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
            DefaultExt = ".sql",
            FileName = Path.GetFileName(OutputFile)
        };
        if (dlg.ShowDialog() == true)
            OutputFile = dlg.FileName;
    }

    private string BuildConnectionString()
    {
        var profile = new ConnectionProfile
        {
            Provider = Provider,
            Server = Server,
            Database = Database,
            IntegratedSecurity = IntegratedSecurity,
            Username = Username,
            Password = Password
        };
        return profile.ToConnectionString();
    }

    private string BuildDestConnectionString()
    {
        var profile = new ConnectionProfile
        {
            Server = DestServer,
            Database = DestDatabase,
            IntegratedSecurity = DestIntegratedSecurity,
            Username = DestUsername,
            Password = DestPassword
        };
        return profile.ToConnectionString();
    }

    private async Task TestConnectionAsync()
    {
        ConnectionStatus = "Testing...";
        ConnectionOk = false;
        try
        {
            await GetExplorer().TestConnectionAsync(BuildConnectionString());
            ConnectionStatus = "Connected";
            ConnectionOk = true;
            await RefreshTablesAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Failed: {ex.Message}";
            ConnectionOk = false;
        }
    }

    private async Task TestDestConnectionAsync()
    {
        DestConnectionStatus = "Testing...";
        DestConnectionOk = false;
        try
        {
            await new SchemaExplorer().TestConnectionAsync(BuildDestConnectionString());
            DestConnectionStatus = "Connected";
            DestConnectionOk = true;
        }
        catch (Exception ex)
        {
            DestConnectionStatus = $"Failed: {ex.Message}";
            DestConnectionOk = false;
        }
    }

    private async Task RefreshTablesAsync()
    {
        try
        {
            var tables = await GetExplorer().GetTablesAsync(BuildConnectionString());
            Tables.Clear();
            TableSelections.Clear();
            foreach (var t in tables)
            {
                Tables.Add(t);
                var ts = new TableSelection { Name = t, IsIncluded = true };
                ts.PropertyChanged += (_, _) => NotifyStepCompletion();
                TableSelections.Add(ts);
            }
            AddLog($"Loaded {tables.Count} tables");
        }
        catch (Exception ex)
        {
            AddLog($"Error loading tables: {ex.Message}");
        }
    }

    private void SelectAllTables()
    {
        foreach (var ts in TableSelections)
            ts.IsIncluded = true;
    }

    private void DeselectAllTables()
    {
        foreach (var ts in TableSelections)
            ts.IsIncluded = false;
    }

    private async Task LoadPrimaryKeyAsync(string table)
    {
        try
        {
            var pk = await GetExplorer().GetPrimaryKeyColumnAsync(BuildConnectionString(), table);
            PrimaryKeyColumn = pk ?? "(no PK found)";
        }
        catch (Exception ex)
        {
            PrimaryKeyColumn = $"Error: {ex.Message}";
        }
        // Re-evaluate CanLoadRootCandidates (and other commands) now that PK is known
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanLoadRootCandidates() =>
        !IsRunning && ConnectionOk &&
        !string.IsNullOrWhiteSpace(SelectedTable) &&
        !string.IsNullOrWhiteSpace(PrimaryKeyColumn) &&
        PrimaryKeyColumn != "(no PK found)";

    private async Task LoadRootCandidatesAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedTable) || string.IsNullOrWhiteSpace(PrimaryKeyColumn) ||
            PrimaryKeyColumn == "(no PK found)")
            return;
        try
        {
            RootRowCandidates.Clear();
            SelectedRootRow = null;
            AddLog("Loading sample rows from root table...");
            var rows = await GetExplorer().GetSampleRowsAsync(BuildConnectionString(), SelectedTable!, PrimaryKeyColumn, 200);
            foreach (var r in rows)
                RootRowCandidates.Add(r);
            AddLog($"Loaded {rows.Count} row(s). Select one as root entry.");
        }
        catch (Exception ex)
        {
            AddLog($"Error loading rows: {ex.Message}");
        }
    }

    private bool CanRunSubset() =>
        !IsRunning &&
        ConnectionOk &&
        !string.IsNullOrWhiteSpace(SelectedTable) &&
        !string.IsNullOrWhiteSpace(RootPkValue) &&
        (IsSQLite || Provider == DatabaseProvider.MySql || Provider == DatabaseProvider.Postgres
            ? !string.IsNullOrWhiteSpace(OutputFile)
            : (IsFileMode ? !string.IsNullOrWhiteSpace(OutputFile) : DestConnectionOk));

    private async Task RunSubsetAsync()
    {
        IsRunning = true;
        IsProgressIndeterminate = true;
        LogEntries.Clear();
        TotalInserts = 0;
        _cts = new CancellationTokenSource();
        _stopwatch = Stopwatch.StartNew();
        _elapsedTimer.Start();

        AddLog("Starting subset operation...");

        var progress = new Progress<SubsetProgress>(p =>
        {
            AddLog(p.Message);
            CurrentTable = p.CurrentTable;
            TotalInserts = p.TotalRows;
            if (p.TablesProcessed + p.TablesQueued > 0)
            {
                IsProgressIndeterminate = false;
                ProgressValue = (double)p.TablesProcessed / (p.TablesProcessed + p.TablesQueued) * 100;
            }
        });

        try
        {
            var excludedTables = TableSelections
                .Where(ts => !ts.IsIncluded)
                .Select(ts => ts.Name)
                .ToList();

            if (IsSQLite)
            {
                var engineSqlite = new SubsetEngineSqlite(
                    BuildConnectionString(), SelectedTable!, RootPkValue, OutputFile!,
                    MaxRowsPerTable, progress, excludedTables);
                await Task.Run(() => engineSqlite.RunAsync(_cts.Token));
                TotalInserts = engineSqlite.TotalOut;
                AddLog($"Complete! {engineSqlite.TotalOut:N0} INSERTs written to {OutputFile}");
            }
            else if (Provider == DatabaseProvider.MySql)
            {
                var engineMySql = new SubsetEngineMySql(
                    BuildConnectionString(), SelectedTable!, RootPkValue, OutputFile!,
                    MaxRowsPerTable, progress, excludedTables);
                await Task.Run(() => engineMySql.RunAsync(_cts.Token));
                TotalInserts = engineMySql.TotalOut;
                AddLog($"Complete! {engineMySql.TotalOut:N0} INSERTs written to {OutputFile}");
            }
            else if (Provider == DatabaseProvider.Postgres)
            {
                var enginePostgres = new SubsetEnginePostgres(
                    BuildConnectionString(), SelectedTable!, RootPkValue, OutputFile!,
                    MaxRowsPerTable, progress, excludedTables);
                await Task.Run(() => enginePostgres.RunAsync(_cts.Token));
                TotalInserts = enginePostgres.TotalOut;
                AddLog($"Complete! {enginePostgres.TotalOut:N0} INSERTs written to {OutputFile}");
            }
            else
            {
                var engine = new SubsetEngine(
                    BuildConnectionString(),
                    SelectedTable!,
                    RootPkValue,
                    destConnStr: IsDbMode ? BuildDestConnectionString() : null,
                    outFile: IsFileMode ? OutputFile : null,
                    MaxRowsPerTable,
                    progress,
                    excludedTables);
                await Task.Run(() => engine.RunAsync(_cts.Token));
                TotalInserts = engine.TotalOut;
                if (IsFileMode)
                    AddLog($"Complete! {engine.TotalOut:N0} INSERTs written to {OutputFile}");
                else
                    AddLog($"Complete! {engine.TotalOut:N0} rows copied to {DestDatabase} on {DestServer}");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            AddLog($"Error: {ex.Message}");
        }
        finally
        {
            _stopwatch?.Stop();
            _elapsedTimer.Stop();
            ElapsedTime = _stopwatch?.Elapsed.ToString(@"mm\:ss") ?? "00:00";
            IsRunning = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelRun()
    {
        _cts?.Cancel();
        AddLog("Cancellation requested...");
    }

    private void SaveProfile()
    {
        var name = Provider switch
        {
            DatabaseProvider.SQLite => "SQLite: " + Path.GetFileName(Server),
            DatabaseProvider.MySql => "MySQL: " + Server + "/" + Database,
            DatabaseProvider.Postgres => "Postgres: " + Server + "/" + Database,
            _ => Server + "/" + Database
        };
        var existing = Profiles.FirstOrDefault(p => p.Name == name);
        var profile = new ConnectionProfile
        {
            Name = name,
            Provider = Provider,
            Server = Server,
            Database = Database,
            IntegratedSecurity = IntegratedSecurity,
            Username = Username,
            Password = Password
        };

        if (existing is not null)
            Profiles.Remove(existing);

        Profiles.Add(profile);
        _profileManager.Save(Profiles.ToList());
        SelectedProfile = profile;
        AddLog($"Profile '{name}' saved");
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var name = SelectedProfile.Name;
        Profiles.Remove(SelectedProfile);
        _profileManager.Save(Profiles.ToList());
        SelectedProfile = null;
        AddLog($"Profile '{name}' deleted");
    }

    private void BrowseDatabaseFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite databases (*.db;*.db3;*.sqlite)|*.db;*.db3;*.sqlite|All files (*.*)|*.*",
            DefaultExt = ".db",
            Title = "Select SQLite database file"
        };
        if (dlg.ShowDialog() == true)
            Server = dlg.FileName;
    }

    private void AddLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogEntries.Add(entry);
    }

    private void NotifyStepCompletion()
    {
        OnPropertyChanged(nameof(Step1Completed));
        OnPropertyChanged(nameof(Step2Completed));
        OnPropertyChanged(nameof(Step3Completed));
        OnPropertyChanged(nameof(Step4Completed));
        OnPropertyChanged(nameof(Step2Enabled));
        OnPropertyChanged(nameof(Step3Enabled));
        OnPropertyChanged(nameof(Step4Enabled));
        OnPropertyChanged(nameof(Step5Enabled));
        OnPropertyChanged(nameof(HighestCompletedStep));
        OnPropertyChanged(nameof(CanGoNext));
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Wizard navigation ────────────────────────────────────────────────

    private int _currentStep = 1;
    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            _currentStep = Math.Clamp(value, 1, 5);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(StepSubtitle));
            OnPropertyChanged(nameof(Step1Visible));
            OnPropertyChanged(nameof(Step2Visible));
            OnPropertyChanged(nameof(Step3Visible));
            OnPropertyChanged(nameof(Step4Visible));
            OnPropertyChanged(nameof(Step5Visible));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StepTitle => CurrentStep switch
    {
        1 => "Connection",
        2 => "Root Data",
        3 => "Tables",
        4 => "Destination",
        5 => "Run",
        _ => string.Empty
    };

    public string StepSubtitle => CurrentStep switch
    {
        1 => "Configure source database connection",
        2 => "Select root table and entry point",
        3 => "Choose which tables to include",
        4 => "Configure output destination",
        5 => "Execute and monitor the subset",
        _ => string.Empty
    };

    public Visibility Step1Visible => CurrentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step2Visible => CurrentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step3Visible => CurrentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step4Visible => CurrentStep == 4 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Step5Visible => CurrentStep == 5 ? Visibility.Visible : Visibility.Collapsed;

    public bool CanGoBack => CurrentStep > 1;

    /// <summary>True when the current step is completed so the user can proceed to Next.</summary>
    public bool CanGoNext => CurrentStep < 5 && IsStepCompleted(CurrentStep);

    /// <summary>Step N can be navigated to only if all previous steps are completed.</summary>
    public bool CanGoToStep(int step) => step switch
    {
        1 => true,
        2 => Step1Completed,
        3 => Step2Completed,
        4 => Step3Completed,
        5 => Step4Completed,
        _ => false
    };

    public bool Step1Enabled => true;
    public bool Step2Enabled => Step1Completed;
    public bool Step3Enabled => Step2Completed;
    public bool Step4Enabled => Step3Completed;
    public bool Step5Enabled => Step4Completed;

    private bool IsStepCompleted(int step) => step switch
    {
        1 => Step1Completed,
        2 => Step2Completed,
        3 => Step3Completed,
        4 => Step4Completed,
        _ => false
    };

    public bool Step1Completed => ConnectionOk;

    public bool Step2Completed =>
        !string.IsNullOrWhiteSpace(SelectedTable) &&
        !string.IsNullOrWhiteSpace(RootPkValue);

    public bool Step3Completed => TableSelections.Any(ts => ts.IsIncluded);

    public bool Step4Completed =>
        IsFileMode
            ? !string.IsNullOrWhiteSpace(OutputFile)
            : DestConnectionOk;

    /// <summary>Highest completed step index (1–4). Used for green circle on completed steps.</summary>
    public int HighestCompletedStep =>
        Step4Completed ? 4 : Step3Completed ? 3 : Step2Completed ? 2 : Step1Completed ? 1 : 0;

    // ─────────────────────────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
