using System.Collections.ObjectModel;
using System.Windows.Threading;
using PS3Transfer.App.Dialogs;
using PS3Transfer.App.Mvvm;
using PS3Transfer.App.Services;
using PS3Transfer.Core;

namespace PS3Transfer.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const long SpaceMargin = 32L * 1024 * 1024;
    private readonly IUserDialogs _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private readonly ManifestStore _manifests;
    private readonly DispatcherTimer _driveTimer;
    private CancellationTokenSource? _copies;
    private CancellationTokenSource? _joinRun;
    private bool _closeAfterStop;
    private int _activeCopies;
    private int _waveFailures;
    private bool _waveCanceled;
    private string? _waveFragment;
    private long _runTotal;
    private long _settledBytes;
    private DateTime _runStart;
    private DriveRow? _selectedDrive;
    private QueueItemViewModel? _selectedItem;
    private LibraryRowViewModel? _selectedLibrary;
    private readonly List<QueueItemViewModel> _queueSelection = [];
    private readonly List<LibraryRowViewModel> _librarySelection = [];
    private int _tab;
    private bool _transferring;
    private bool _joining;
    private bool _adding;
    private int _addDepth;
    private bool _verifyCrc;
    private bool _hideExtraParts;
    private bool _useTitleNames;
    private string _status = "Plug in the USB drive the PS3 will use.";
    private string _startHint = "Add a game to begin.";
    private string _playTitle = "Add a PKG, an ISO, or an extracted package folder";
    private string _playBody = "Split PKG parts, including name_1.pkg pieces, are joined into one package when the drive can hold the file. An extracted package is copied to \\GAMEI so webMAN can launch it from the SSD. A PS3 ISO goes in \\PS3ISO and is split for FAT32 at the size webMAN already mounts. A ROM keeps its file name and goes in \\ROMS\\<system> for RetroArch.";
    private double _overallFraction;
    private string _overallDetail = "";
    private string _libraryStatus = "Choose a drive.";
    private string _joinSummary = "Pick one piece. The other pieces in that folder are found automatically.";
    private string _joinStatus = "";
    private double _joinFraction;
    private IReadOnlyList<string> _joinFiles = [];
    private readonly HashSet<string> _mergeSeen = new(StringComparer.OrdinalIgnoreCase);
    private int _driveGeneration;
    private int _driveRunning;
    private int _driveRetries;
    private bool _driveKeepSelection = true;

    public MainViewModel(IUserDialogs dialogs, Dispatcher dispatcher)
    {
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = SettingsStore.Load();
        _verifyCrc = _settings.VerifyCrc32;
        _hideExtraParts = _settings.HideExtraParts;
        _useTitleNames = _settings.UseTitleNames;
        _manifests = new ManifestStore(AppPaths.Manifests);
        _driveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _driveTimer.Tick += (_, _) =>
        {
            _driveTimer.Stop();
            KickDriveRefresh(keepSelection: true, resetRetries: false);
        };

        AddFilesCommand = new RelayCommand(AddFiles);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveCommand = new RelayCommand(RemoveSelected, () => _queueSelection.Any(item => !item.IsRunning));
        StartCommand = new RelayCommand(Start, () => CanStart());
        StopCommand = new RelayCommand(Stop, () => Transferring || Joining);
        ReplaceCommand = new RelayCommand<QueueItemViewModel>(Replace, item => item is { IsRunning: false, ShowReplace: true });
        SelectDriveCommand = new RelayCommand<DriveRow>(SelectDrive, _ => CanEdit);
        EjectCommand = new RelayCommand(Eject, () => CanEdit && SelectedDrive is { Verdict: not DriveVerdict.Blocked });
        RefreshCommand = new RelayCommand(Refresh, () => CanEdit);
        ShowTransferCommand = new RelayCommand(() => Tab = 0);
        ShowLibraryCommand = new RelayCommand(() => Tab = 1);
        ShowJoinCommand = new RelayCommand(() => Tab = 2);
        DeleteLibraryCommand = new RelayCommand(DeleteLibrary, () => CanEdit && _librarySelection.Count > 0);
        OrganizeRomsCommand = new RelayCommand(() => _ = OrganizeAsync(), () => CanEdit && SelectedDrive is { Verdict: not DriveVerdict.Blocked });
        MergeLibraryCommand = new RelayCommand(() => _ = MergeSelectedAsync(), () => CanEdit && _librarySelection.Any(row => row.Entry.OfferMerge));
        PickJoinCommand = new RelayCommand(PickJoin, () => !_joining);
        JoinCommand = new RelayCommand(StartJoin, () => !_joining && _joinFiles.Count > 0);

        KickDriveRefresh(keepSelection: false, resetRetries: true);
    }

    public ObservableCollection<DriveRow> Drives { get; } = [];
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];
    public ObservableCollection<LibraryRowViewModel> Library { get; } = [];
    public ObservableCollection<string> JoinParts { get; } = [];

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand<QueueItemViewModel> ReplaceCommand { get; }
    public RelayCommand<DriveRow> SelectDriveCommand { get; }
    public RelayCommand EjectCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ShowTransferCommand { get; }
    public RelayCommand ShowLibraryCommand { get; }
    public RelayCommand ShowJoinCommand { get; }
    public RelayCommand DeleteLibraryCommand { get; }
    public RelayCommand OrganizeRomsCommand { get; }
    public RelayCommand MergeLibraryCommand { get; }
    public RelayCommand PickJoinCommand { get; }
    public RelayCommand JoinCommand { get; }

    public event Action? CloseRequested;

    public DriveRow? SelectedDrive => _selectedDrive;
    public string DriveReason => _selectedDrive?.Reason ?? "Plug in a USB drive. The Windows drive is never used.";
    public int Tab
    {
        get => _tab;
        private set
        {
            if (!Set(ref _tab, value))
                return;
            if (value == 1)
                _ = ScanLibraryAsync();
        }
    }

    public QueueItemViewModel? SelectedItem => _selectedItem;

    public LibraryRowViewModel? SelectedLibrary => _selectedLibrary;

    public void SetQueueSelection(IReadOnlyList<QueueItemViewModel> items, QueueItemViewModel? current)
    {
        _queueSelection.Clear();
        foreach (var item in items)
            _queueSelection.Add(item);
        _selectedItem = current;
        Raise(nameof(SelectedItem));
        if (current?.Plan is { } plan)
        {
            PlayTitle = plan.PlayTitle;
            PlayBody = plan.PlayBody;
        }

        RemoveCommand.Raise();
    }

    public void SetLibrarySelection(IReadOnlyList<LibraryRowViewModel> items)
    {
        _librarySelection.Clear();
        foreach (var item in items)
            _librarySelection.Add(item);
        _selectedLibrary = items.Count == 0 ? null : items[^1];
        Raise(nameof(SelectedLibrary));
        DeleteLibraryCommand.Raise();
        MergeLibraryCommand.Raise();
    }

    public bool Transferring => _transferring;
    public bool Joining => _joining;
    public bool CanEdit => !_transferring && !_joining && !_adding;
    public bool VerifyCrc
    {
        get => _verifyCrc;
        set
        {
            if (!Set(ref _verifyCrc, value))
                return;
            _settings.VerifyCrc32 = value;
            SettingsStore.Save(_settings);
        }
    }

    public bool HideExtraParts
    {
        get => _hideExtraParts;
        set
        {
            if (!Set(ref _hideExtraParts, value))
                return;
            _settings.HideExtraParts = value;
            SettingsStore.Save(_settings);
        }
    }

    public bool UseTitleNames
    {
        get => _useTitleNames;
        set
        {
            if (!Set(ref _useTitleNames, value))
                return;
            _settings.UseTitleNames = value;
            SettingsStore.Save(_settings);
            Replan();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string StartHint
    {
        get => _startHint;
        private set => Set(ref _startHint, value);
    }

    public string PlayTitle
    {
        get => _playTitle;
        private set => Set(ref _playTitle, value);
    }

    public string PlayBody
    {
        get => _playBody;
        private set => Set(ref _playBody, value);
    }

    public double OverallFraction
    {
        get => _overallFraction;
        private set => Set(ref _overallFraction, value);
    }

    public string OverallDetail
    {
        get => _overallDetail;
        private set => Set(ref _overallDetail, value);
    }

    public string LibraryStatus
    {
        get => _libraryStatus;
        private set => Set(ref _libraryStatus, value);
    }

    public string JoinSummary
    {
        get => _joinSummary;
        private set => Set(ref _joinSummary, value);
    }

    public string JoinStatus
    {
        get => _joinStatus;
        private set => Set(ref _joinStatus, value);
    }

    public double JoinFraction
    {
        get => _joinFraction;
        private set => Set(ref _joinFraction, value);
    }

    public void ScheduleDriveRefresh()
    {
        _driveRetries = 0;
        _driveTimer.Interval = TimeSpan.FromMilliseconds(700);
        _driveTimer.Stop();
        _driveTimer.Start();
    }

    public void RequestClose()
    {
        if (_closeAfterStop)
            return;
        if (!_dialogs.Confirm("Stop the transfer?", "A copy is still running. Stop it and close? Finished bytes stay on the drive and can be resumed."))
            return;
        _closeAfterStop = true;
        _copies?.Cancel();
        _joinRun?.Cancel();
    }

    public void SaveSettings() => SettingsStore.Save(_settings);

    public bool TryReadPlacement(out double left, out double top, out double width, out double height, out bool maximized)
    {
        left = _settings.WindowLeft ?? 0;
        top = _settings.WindowTop ?? 0;
        width = _settings.WindowWidth ?? 0;
        height = _settings.WindowHeight ?? 0;
        maximized = _settings.WindowMaximized;
        return _settings.WindowLeft is not null
            && _settings.WindowTop is not null
            && _settings.WindowWidth is >= 200
            && _settings.WindowHeight is >= 200;
    }

    public void WritePlacement(double left, double top, double width, double height, bool maximized)
    {
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowMaximized = maximized;
    }

    public Task AddPathsAsync(IEnumerable<string> paths) => AddAsync(paths);

    private void AddFiles()
    {
        var files = _dialogs.PickFiles();
        if (files is { Length: > 0 })
            _ = AddAsync(files);
    }

    private void AddFolder()
    {
        var folder = _dialogs.PickFolder();
        if (!string.IsNullOrWhiteSpace(folder))
            _ = AddAsync([folder]);
    }

    private async Task AddAsync(IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return;
        _addDepth++;
        _adding = true;
        RaiseBusy();
        var extracting = false;
        foreach (var path in list)
        {
            if (path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            {
                extracting = true;
                break;
            }
        }

        Status = extracting ? "Extracting the archive…" : "Reading the files…";
        try
        {
            var result = await Task.Run(() => SourceDiscovery.Collect(list));
            var sources = await Task.Run(() => result.Groups.Select(DiscInspector.Inspect).ToList());
            var known = new HashSet<string>(Queue.Select(item => item.Source.DisplayPath), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var source in sources)
            {
                if (!known.Add(source.DisplayPath))
                    continue;
                Queue.Add(new QueueItemViewModel(source, Replan));
                added++;
            }

            foreach (var note in result.Notes)
                AppLog.Write(note);
            Replan();
            if (added == 0 && result.Notes.Count > 0)
                Status = result.Notes[0];
            else if (added == 0)
                Status = "Nothing in that selection can be copied.";
            else
                Status = added == 1 ? "Added 1 item." : "Added " + added + " items.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            AppLog.Write(ex.ToString());
        }
        finally
        {
            _addDepth = Math.Max(0, _addDepth - 1);
            _adding = _addDepth > 0;
            RaiseBusy();
        }
    }

    private void RemoveSelected()
    {
        var doomed = _queueSelection.Where(item => !item.IsRunning).ToList();
        if (doomed.Count == 0)
            return;
        foreach (var item in doomed)
        {
            Queue.Remove(item);
            ReleaseExtract(item, stillQueued: false);
        }

        Replan();
    }

    private void Replace(QueueItemViewModel? item)
    {
        if (item is null || item.IsRunning || !item.ShowReplace)
            return;
        if (!_dialogs.Confirm("Replace the files on the drive?", item.Title + " will be copied again. The existing files with that name will be deleted."))
            return;
        item.ArmReplace();
    }

    private void Start() => _ = StartAsync();

    private async Task StartAsync()
    {
        if (!CanStart() || _selectedDrive is null)
            return;
        var drive = _selectedDrive;
        if (_activeCopies == 0
            && drive.Verdict == DriveVerdict.Caution
            && !_dialogs.Confirm("Copy to this drive?", drive.Reason))
            return;

        Replan();
        var pending = new List<(QueueItemViewModel Item, TransferPlan Plan)>();
        foreach (var item in Queue)
        {
            if (item.IsRunning || item.Plan is not { } plan)
                continue;
            if (plan.Action is TransferAction.Copy or TransferAction.Resume or TransferAction.Replace)
                pending.Add((item, plan));
        }

        if (pending.Count == 0)
            return;

        long free;
        try
        {
            free = new DriveInfo(drive.Root).AvailableFreeSpace;
        }
        catch (IOException ex)
        {
            Status = ex.Message;
            return;
        }

        var need = pending.Sum(entry => entry.Plan.BytesToWrite);
        if (need > free - SpaceMargin)
        {
            Status = "Not enough free space. This queue writes " + ByteSize.Format(need) + " and the drive has " + ByteSize.Format(free) + " free.";
            return;
        }

        if (_activeCopies == 0)
        {
            _waveFailures = 0;
            _waveCanceled = false;
            _waveFragment = null;
            _settledBytes = 0;
            _runTotal = 0;
            _runStart = DateTime.UtcNow;
            OverallFraction = 0;
            OverallDetail = "";
            _copies?.Dispose();
            _copies = new CancellationTokenSource();
        }

        _runTotal += need;
        var token = _copies!.Token;
        foreach (var (item, plan) in pending)
        {
            item.BeginRun();
            _activeCopies++;
            _transferring = true;
            _ = CopyOneAsync(item, plan, token);
        }

        Status = _activeCopies == 1 ? "Copying 1 item." : "Copying " + _activeCopies + " items.";
        RaiseBusy();
        await Task.CompletedTask;
    }

    private async Task CopyOneAsync(QueueItemViewModel item, TransferPlan plan, CancellationToken token)
    {
        var options = new TransferOptions
        {
            VerifyCrc32 = VerifyCrc,
            HideExtraParts = HideExtraParts,
            Manifests = _manifests,
        };
        var progress = new UiProgress(_dispatcher, value =>
        {
            item.Report(value);
            ShowOverall(OverallDone(), value.Detail);
        });
        TransferResult result;
        try
        {
            result = await TransferEngine.CopyAsync(plan, options, progress, token);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex.ToString());
            item.Fail(ex.Message);
            result = new TransferResult(TransferOutcome.Failed, 0, ex.Message, null);
        }

        _settledBytes += result.BytesWritten;
        AppLog.Write(item.Title + " " + result.Outcome + " " + (result.Error ?? result.Warning ?? ""));
        if (result.Outcome == TransferOutcome.Canceled)
            _waveCanceled = true;
        else if (result.Outcome == TransferOutcome.Failed)
        {
            if (item.Tone != "Bad")
                item.Fail(result.Error ?? "The copy failed.");
            _waveFailures++;
        }
        else if (result.Outcome == TransferOutcome.Copied)
        {
            var warning = FragmentWarning(plan);
            item.ClearSticky();
            if (warning is not null)
            {
                _waveFragment = warning;
                item.Warn(warning);
            }

            ReleaseExtract(item, stillQueued: true);
        }
        else if (result.Outcome == TransferOutcome.Skipped)
        {
            ReleaseExtract(item, stillQueued: true);
        }

        item.EndRun();
        _activeCopies--;
        if (_activeCopies < 0)
            _activeCopies = 0;
        _transferring = _activeCopies > 0;
        ShowOverall(OverallDone(), item.Title);
        if (_activeCopies == 0)
        {
            _copies?.Dispose();
            _copies = null;
            if (_closeAfterStop)
            {
                RaiseBusy();
                CloseRequested?.Invoke();
                return;
            }

            if (_waveCanceled)
                Status = "Stopped. What landed on the drive can be resumed.";
            else if (_waveFailures == 1)
                Status = "1 item needs attention. The rest were copied.";
            else if (_waveFailures > 1)
                Status = _waveFailures + " items need attention. The rest were copied.";
            else
                Status = _waveFragment ?? "Finished. Plug the drive into the PS3.";
            if (!_waveCanceled && _waveFailures == 0)
                OverallFraction = 1;
            KickDriveRefresh(keepSelection: true, resetRetries: true);
        }
        else
        {
            Status = _activeCopies == 1 ? "Copying 1 item." : "Copying " + _activeCopies + " items.";
        }

        Replan();
        RaiseBusy();
    }

    private void Stop()
    {
        _copies?.Cancel();
        _joinRun?.Cancel();
    }

    private long OverallDone()
    {
        var done = _settledBytes;
        foreach (var item in Queue)
        {
            if (item.IsRunning)
                done += item.SessionBytes;
        }

        return done;
    }

    private void ReleaseExtract(QueueItemViewModel item, bool stillQueued)
    {
        var root = item.Source.ExtractRoot;
        if (string.IsNullOrEmpty(root))
            return;
        foreach (var other in Queue)
        {
            if (stillQueued && ReferenceEquals(other, item))
                continue;
            if (string.Equals(other.Source.ExtractRoot, root, StringComparison.OrdinalIgnoreCase))
                return;
        }

        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Could not remove the extracted archive. " + ex.Message);
        }
    }

    private void Replan()
    {
        var drive = _selectedDrive;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Queue)
        {
            if (item.IsRunning && item.Plan is { } running)
            {
                foreach (var path in running.OutputPaths)
                    reserved.Add(path);
                foreach (var copy in running.Copies)
                    reserved.Add(copy.DestinationBase);
                continue;
            }

            if (drive is null || drive.Verdict == DriveVerdict.Blocked)
            {
                item.Hold(drive is null ? "Choose a USB drive." : drive.Reason);
                continue;
            }

            TransferPlan plan;
            try
            {
                plan = TransferPlanner.Plan(new PlanRequest
                {
                    Source = item.Source,
                    DriveRoot = drive.Root,
                    FileSystem = drive.FileSystem,
                    VolumeSerial = drive.Serial,
                    UseTitleNames = UseTitleNames,
                    ForcedKind = item.SelectedKind.Kind,
                    RomFolder = item.SelectedKind.RomFolder,
                    Replace = item.Replace,
                    Reserved = reserved,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item.Hold(ex.Message);
                continue;
            }

            item.Apply(plan);
        }

        if (SelectedItem?.Plan is { } selected)
        {
            PlayTitle = selected.PlayTitle;
            PlayBody = selected.PlayBody;
        }

        UpdateHint();
        StartCommand.Raise();
    }

    private void UpdateHint()
    {
        if (_selectedDrive is null)
        {
            StartHint = "Plug in a USB drive.";
            return;
        }

        if (_selectedDrive.Verdict == DriveVerdict.Blocked)
        {
            StartHint = _selectedDrive.Reason;
            return;
        }

        long need = 0;
        var count = 0;
        foreach (var item in Queue)
        {
            if (item.Plan?.Action is TransferAction.Copy or TransferAction.Resume or TransferAction.Replace)
            {
                count++;
                need += item.Plan.BytesToWrite;
            }
        }

        if (count == 0)
        {
            StartHint = Queue.Count == 0 ? "Add a game to begin." : "Nothing in the queue will be written.";
            return;
        }

        if (need > _selectedDrive.FreeBytes - SpaceMargin)
        {
            StartHint = "Needs " + ByteSize.Format(need) + ". The drive has " + ByteSize.Format(_selectedDrive.FreeBytes) + " free.";
            return;
        }

        StartHint = count == 1
            ? "Ready to copy 1 item, " + ByteSize.Format(need) + "."
            : "Ready to copy " + count + " items, " + ByteSize.Format(need) + ".";
    }

    private bool CanStart()
    {
        if (_selectedDrive is null || _selectedDrive.Verdict == DriveVerdict.Blocked)
            return false;
        long need = 0;
        var any = false;
        foreach (var item in Queue)
        {
            if (item.IsRunning)
                continue;
            if (item.Plan?.Action is TransferAction.Copy or TransferAction.Resume or TransferAction.Replace)
            {
                any = true;
                need += item.Plan.BytesToWrite;
            }
        }

        return any && need <= _selectedDrive.FreeBytes - SpaceMargin;
    }

    private void ShowOverall(long done, string detail)
    {
        OverallFraction = _runTotal <= 0 ? 0 : Math.Clamp(done / (double)_runTotal, 0, 1);
        var elapsed = Math.Max(0.001, (DateTime.UtcNow - _runStart).TotalSeconds);
        var speed = done / elapsed;
        var left = Math.Max(0, _runTotal - done);
        OverallDetail = ByteSize.FormatSpeed(speed) + " · " + ByteSize.FormatEta(left, speed) + " · " + detail;
    }

    private void SelectDrive(DriveRow? row)
    {
        if (!CanEdit || row is null)
            return;
        ApplySelection(row, replan: true);
        _settings.LastDriveRoot = row.Root;
        SettingsStore.Save(_settings);
    }

    private void Refresh() => KickDriveRefresh(keepSelection: true, resetRetries: true);

    private void KickDriveRefresh(bool keepSelection, bool resetRetries)
    {
        if (resetRetries)
        {
            _driveRetries = 0;
            _driveTimer.Interval = TimeSpan.FromMilliseconds(700);
        }

        _driveKeepSelection = keepSelection;
        Interlocked.Increment(ref _driveGeneration);
        _ = PumpDriveRefreshAsync();
    }

    private async Task PumpDriveRefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _driveRunning, 1, 0) != 0)
            return;

        var applied = -1;
        try
        {
            while (true)
            {
                var generation = _driveGeneration;
                var keep = _driveKeepSelection;
                DriveScan scan;
                try
                {
                    scan = await Task.Run(DriveService.List);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write(ex.Message);
                    applied = generation;
                    return;
                }

                if (generation != _driveGeneration)
                    continue;

                ApplyDriveRows(scan.Rows, keep);
                applied = generation;
                if (scan.Again && generation == _driveGeneration && _driveRetries < 15)
                {
                    _driveRetries++;
                    _driveTimer.Interval = TimeSpan.FromSeconds(2);
                    _driveTimer.Stop();
                    _driveTimer.Start();
                }

                if (generation != _driveGeneration)
                    continue;
                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _driveRunning, 0);
            if (applied != _driveGeneration)
                _ = PumpDriveRefreshAsync();
        }
    }

    private void ApplyDriveRows(IReadOnlyList<DriveRow> rows, bool keepSelection)
    {
        var previous = _selectedDrive?.Root;
        Drives.Clear();
        foreach (var row in rows)
            Drives.Add(row);

        DriveRow? match = null;
        if (!string.IsNullOrEmpty(previous))
            match = Drives.FirstOrDefault(row => string.Equals(row.Root, previous, StringComparison.OrdinalIgnoreCase));
        if (_transferring)
        {
            if (match is null)
            {
                _copies?.Cancel();
                Status = "The drive was disconnected. The copy stopped and can be resumed.";
                ApplySelection(null, replan: false);
            }
            else
            {
                ApplySelection(match, replan: false);
            }

            return;
        }

        if (keepSelection && match is not null)
        {
            ApplySelection(match, replan: true);
            return;
        }

        var remembered = Drives.FirstOrDefault(row =>
            row.Verdict != DriveVerdict.Blocked
            && string.Equals(row.Root, _settings.LastDriveRoot, StringComparison.OrdinalIgnoreCase));
        var pick = remembered
            ?? Drives.FirstOrDefault(row => row.Verdict == DriveVerdict.Ready)
            ?? Drives.FirstOrDefault(row => row.Verdict == DriveVerdict.Caution);
        ApplySelection(pick, replan: true);
    }

    private void ApplySelection(DriveRow? row, bool replan)
    {
        foreach (var drive in Drives)
            drive.IsSelected = ReferenceEquals(drive, row);
        _selectedDrive = row;
        Raise(nameof(SelectedDrive));
        Raise(nameof(DriveReason));
        if (replan)
            Replan();
        else
            UpdateHint();
        if (Tab == 1 && replan)
            _ = ScanLibraryAsync();
        EjectCommand.Raise();
        StartCommand.Raise();
        OrganizeRomsCommand.Raise();
        MergeLibraryCommand.Raise();
    }

    private void Eject()
    {
        if (!CanEdit || _selectedDrive is null)
            return;
        var drive = _selectedDrive;
        if (!_dialogs.Confirm("Eject " + drive.Title + "?", "Windows will release the drive so it can be unplugged. Close any Explorer window that is showing it."))
            return;
        var error = DriveService.Eject(drive.Root);
        Status = error ?? "The drive can be unplugged.";
        if (error is null)
            AppLog.Write("Ejected " + drive.Root);
        KickDriveRefresh(keepSelection: true, resetRetries: true);
    }

    private async Task ScanLibraryAsync()
    {
        var drive = _selectedDrive;
        if (drive is null || drive.Verdict == DriveVerdict.Blocked)
        {
            Library.Clear();
            LibraryStatus = drive is null ? "Plug in a USB drive." : drive.Reason;
            return;
        }

        var root = drive.Root;
        LibraryStatus = "Reading the drive...";
        try
        {
            var entries = await Task.Run(() => LibraryCatalog.Scan(root));
            if (!string.Equals(_selectedDrive?.Root, root, StringComparison.OrdinalIgnoreCase))
                return;
            Library.Clear();
            foreach (var entry in entries)
                Library.Add(new LibraryRowViewModel(entry));
            MergeLibraryCommand.Raise();
            var loose = entries.Count(entry => entry.Category == "Loose ROM");
            var mergeable = entries.Count(entry => entry.OfferMerge);
            var status = entries.Count == 0
                ? "No PS3 games or ROMs on this drive yet."
                : loose == 0
                    ? entries.Count + " entries on the drive."
                    : entries.Count + " entries on the drive. " + loose + (loose == 1 ? " ROM is" : " ROMs are") + " not in \\ROMS yet.";
            if (mergeable > 0)
                status += mergeable == 1
                    ? " 1 split package can be merged."
                    : " " + mergeable + " split packages can be merged.";
            LibraryStatus = status;
            var fresh = new List<LibraryEntry>();
            foreach (var entry in entries)
            {
                if (!entry.OfferMerge || !_mergeSeen.Add(MergeKey(entry)))
                    continue;
                fresh.Add(entry);
            }

            if (fresh.Count > 0)
                await OfferMergesAsync(drive, fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LibraryStatus = ex.Message;
        }
    }

    private void DeleteLibrary()
    {
        if (!CanEdit || _selectedDrive is null || _librarySelection.Count == 0)
            return;
        var rows = _librarySelection.ToList();
        var files = rows.Sum(row => row.Entry.Files.Count);
        var noun = files == 1 ? "1 file" : files + " files";
        var heading = rows.Count == 1 ? "Remove " + rows[0].Title + "?" : "Remove " + rows.Count + " items?";
        var names = new List<string>();
        foreach (var row in rows.Take(8))
            names.Add(row.Title);
        if (rows.Count > 8)
            names.Add("…");
        if (!_dialogs.Confirm(heading, "This deletes " + noun + " from the drive.\n\n" + string.Join("\n", names)))
            return;

        string? failed = null;
        foreach (var row in rows)
        {
            try
            {
                LibraryCatalog.Delete(_selectedDrive.Root, row.Entry);
                AppLog.Write("Deleted " + row.Entry.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed = ex.Message;
                break;
            }
        }

        if (failed is not null)
            _dialogs.Tell("Could not delete", failed);
        _ = ScanLibraryAsync();
    }

    private async Task MergeSelectedAsync()
    {
        if (!CanEdit || _selectedDrive is null)
            return;
        var rows = _librarySelection.Where(row => row.Entry.OfferMerge).ToList();
        if (rows.Count == 0)
            return;

        var plans = new List<PkgMergePlan>();
        foreach (var row in rows)
        {
            var entry = row.Entry;
            _mergeSeen.Add(MergeKey(entry));
            if (!PkgMerger.TryPlan(_selectedDrive.Root, _selectedDrive.FileSystem, entry.Files, out var plan, out var error) || plan is null)
            {
                _dialogs.Tell("Cannot merge " + entry.Title, error ?? "Those pieces do not form one package.");
                continue;
            }

            plans.Add(plan);
        }

        if (plans.Count == 0)
            return;
        var title = plans.Count == 1 ? "Merge " + plans[0].Title + "?" : "Merge " + plans.Count + " packages?";
        if (!_dialogs.Confirm(title, string.Join("\n\n", plans.Select(item => item.Summary))))
            return;
        await RunMergesAsync(_selectedDrive, plans);
    }

    private async Task OfferMergesAsync(DriveRow drive, IReadOnlyList<LibraryEntry> fresh)
    {
        if (!CanEdit || !string.Equals(_selectedDrive?.Root, drive.Root, StringComparison.OrdinalIgnoreCase))
            return;

        var plans = new List<PkgMergePlan>();
        foreach (var entry in fresh)
        {
            if (!PkgMerger.TryPlan(drive.Root, drive.FileSystem, entry.Files, out var plan, out var error) || plan is null)
            {
                _dialogs.Tell("Cannot merge " + entry.Title, error ?? "Those pieces do not form one package.");
                continue;
            }

            plans.Add(plan);
        }

        if (plans.Count == 0 || !string.Equals(_selectedDrive?.Root, drive.Root, StringComparison.OrdinalIgnoreCase))
            return;

        var title = plans.Count == 1 ? "Merge " + plans[0].Title + "?" : "Merge split packages?";
        if (!_dialogs.Confirm(title, string.Join("\n\n", plans.Select(item => item.Summary))))
            return;
        await RunMergesAsync(drive, plans);
    }

    private async Task RunMergesAsync(DriveRow drive, IReadOnlyList<PkgMergePlan> plans)
    {
        if (_joining || plans.Count == 0)
            return;

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingList = new List<string>();
        foreach (var plan in plans)
        {
            foreach (var output in plan.Outputs)
            {
                if (File.Exists(output) && existing.Add(output))
                    existingList.Add(output);
            }
        }

        if (existingList.Count > 0 && !_dialogs.Confirm("Replace the existing package?", string.Join("\n", existingList) + "\n\nThese files will be overwritten."))
            return;

        _joining = true;
        _joinRun = new CancellationTokenSource();
        RaiseBusy();
        OverallFraction = 0;
        try
        {
            foreach (var plan in plans)
            {
                if (!string.Equals(_selectedDrive?.Root, drive.Root, StringComparison.OrdinalIgnoreCase))
                    return;

                string fileSystem;
                long free;
                try
                {
                    var info = new DriveInfo(drive.Root);
                    fileSystem = info.DriveFormat;
                    free = info.AvailableFreeSpace;
                }
                catch (IOException ex)
                {
                    LibraryStatus = ex.Message;
                    return;
                }

                long reclaim = 0;
                foreach (var output in plan.Outputs)
                {
                    if (File.Exists(output))
                        reclaim += new FileInfo(output).Length;
                }

                if (plan.Bytes - reclaim > free - SpaceMargin)
                {
                    LibraryStatus = "The drive has " + ByteSize.Format(free) + " free. " + plan.Title + " needs " + ByteSize.Format(plan.Bytes) + " free while the merged package is written.";
                    _dialogs.Tell("Not enough free space", LibraryStatus);
                    return;
                }

                var progress = new UiProgress(_dispatcher, value =>
                {
                    OverallFraction = value.FileLength <= 0 ? 0 : Math.Clamp((double)value.FilePosition / value.FileLength, 0, 1);
                    OverallDetail = value.Detail;
                    LibraryStatus = "Merging " + plan.Title + " · " + value.Detail;
                });
                var result = await PkgMerger.ApplyAsync(drive.Root, plan, fileSystem, VerifyCrc, progress, _joinRun.Token);
                if (result.Outcome == TransferOutcome.Canceled)
                {
                    LibraryStatus = "Merge stopped. The unfinished file was removed.";
                    Status = LibraryStatus;
                    return;
                }

                if (result.Outcome != TransferOutcome.Copied)
                {
                    LibraryStatus = result.Error ?? "The merge failed.";
                    _dialogs.Tell("Could not merge " + plan.Title, LibraryStatus);
                    return;
                }

                OverallFraction = 1;
                var done = plan.OneFile
                    ? "Merged " + plan.Title + " into " + plan.Outputs[0] + "."
                    : "Merged " + plan.Title + ". multiMAN can install the parts in " + Path.GetDirectoryName(plan.Outputs[0]) + ".";
                if (!string.IsNullOrEmpty(result.Warning))
                    done += " " + result.Warning;
                Status = done;
                LibraryStatus = done;
                AppLog.Write("Merge " + done);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LibraryStatus = ex.Message;
            _dialogs.Tell("Could not merge", ex.Message);
        }
        finally
        {
            _joining = false;
            _joinRun?.Dispose();
            _joinRun = null;
            RaiseBusy();
            if (_closeAfterStop && _activeCopies == 0)
                CloseRequested?.Invoke();
        }

        if (string.Equals(_selectedDrive?.Root, drive.Root, StringComparison.OrdinalIgnoreCase))
            await ScanLibraryAsync();
    }

    private static string MergeKey(LibraryEntry entry) =>
        string.Join("|", entry.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

    private async Task OrganizeAsync()
    {
        if (!CanEdit || _selectedDrive is null || _selectedDrive.Verdict == DriveVerdict.Blocked)
            return;

        var root = _selectedDrive.Root;
        _adding = true;
        RaiseBusy();
        try
        {
            RomOrganizePlan plan;
            try
            {
                plan = await Task.Run(() => RomOrganizer.Plan(root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.Tell("Could not read the drive", ex.Message);
                return;
            }

            if (!string.Equals(_selectedDrive?.Root, root, StringComparison.OrdinalIgnoreCase))
                return;

            if (plan.Moves.Count == 0)
            {
                var detail = plan.Notes.Count == 0
                    ? "Every ROM already sits in \\ROMS\\<system>. The drive was not formatted."
                    : string.Join("\n", plan.Notes.Take(8));
                _dialogs.Tell("ROMs are already organized", detail);
                return;
            }

            var lines = new List<string>
            {
                (plan.Moves.Count == 1 ? "1 ROM" : plan.Moves.Count + " ROMs")
                    + " will move into \\ROMS so RetroArch and webMAN can launch them. The drive is not formatted, and PS3 games stay where they are.",
                "",
            };
            foreach (var move in plan.Moves.Take(10))
                lines.Add(Path.GetFileName(move.Source) + "  →  ROMS\\" + move.Folder);
            if (plan.Moves.Count > 10)
                lines.Add("…");
            if (!_dialogs.Confirm("Organize for RetroArch", string.Join("\n", lines)))
                return;

            int moved;
            try
            {
                moved = await Task.Run(() => RomOrganizer.Apply(root, plan.Moves));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _dialogs.Tell("Could not organize", ex.Message);
                return;
            }

            Status = moved == 1 ? "Organized 1 ROM into \\ROMS." : "Organized " + moved + " ROMs into \\ROMS.";
            await ScanLibraryAsync();
        }
        finally
        {
            _adding = _addDepth > 0;
            RaiseBusy();
        }
    }

    private void PickJoin()
    {
        var path = _dialogs.PickPart();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!FileJoiner.TryDiscover(path, out var parts, out var error))
        {
            JoinStatus = error ?? "Those pieces could not be read.";
            _joinFiles = [];
            JoinParts.Clear();
            JoinCommand.Raise();
            return;
        }

        _joinFiles = parts;
        JoinParts.Clear();
        long total = 0;
        foreach (var part in parts)
        {
            var length = new FileInfo(part).Length;
            total += length;
            JoinParts.Add(Path.GetFileName(part) + "  ·  " + ByteSize.Format(length));
        }

        JoinSummary = parts.Count + " pieces · " + ByteSize.Format(total);
        JoinStatus = "Choose where to save the joined file. FAT32 cannot hold a result over 4 GB.";
        JoinFraction = 0;
        JoinCommand.Raise();
    }

    private void StartJoin() => _ = JoinAsync();

    private async Task JoinAsync()
    {
        if (_joining || _joinFiles.Count == 0)
            return;
        var suggested = "joined.bin";
        var firstName = Path.GetFileName(_joinFiles[0]);
        if (SplitNames.TryParse(firstName, out var baseName, out _, out _))
            suggested = baseName;
        else if (PkgParts.TryParse(firstName, out var joined, out _))
            suggested = joined;
        var output = _dialogs.PickSave(suggested);
        if (string.IsNullOrWhiteSpace(output))
            return;
        if (_joinFiles.Any(part => string.Equals(Path.GetFullPath(part), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)))
        {
            JoinStatus = "Pick a new file. The joined copy cannot replace one of the pieces.";
            return;
        }

        if (File.Exists(output) && !_dialogs.Confirm("Replace the existing file?", output + " will be overwritten."))
            return;

        var root = Path.GetPathRoot(output);
        if (string.IsNullOrEmpty(root))
        {
            JoinStatus = "That save location has no drive.";
            return;
        }

        string fileSystem;
        long free;
        try
        {
            var info = new DriveInfo(root);
            fileSystem = info.DriveFormat;
            free = info.AvailableFreeSpace;
        }
        catch (IOException ex)
        {
            JoinStatus = ex.Message;
            return;
        }

        long total = 0;
        foreach (var part in _joinFiles)
            total += new FileInfo(part).Length;
        if (Ps3Layout.IsFat(fileSystem) && total > Ps3Layout.SplitSize)
        {
            JoinStatus = "The joined file is " + ByteSize.Format(total) + ". Save it on an NTFS disk.";
            return;
        }

        if (total > free - SpaceMargin)
        {
            JoinStatus = "That disk has " + ByteSize.Format(free) + " free. The joined file needs " + ByteSize.Format(total) + ".";
            return;
        }

        _joining = true;
        _joinRun = new CancellationTokenSource();
        RaiseBusy();
        JoinFraction = 0;
        var progress = new UiProgress(_dispatcher, value =>
        {
            JoinFraction = value.FileLength <= 0 ? 0 : Math.Clamp((double)value.FilePosition / value.FileLength, 0, 1);
            JoinStatus = value.Detail;
        });
        try
        {
            var result = await FileJoiner.JoinAsync(_joinFiles, output, fileSystem, VerifyCrc, progress, _joinRun.Token);
            JoinStatus = result.Outcome switch
            {
                TransferOutcome.Copied => "Joined file saved. " + output,
                TransferOutcome.Canceled => "Join stopped. The unfinished file was removed.",
                _ => result.Error ?? "The join failed.",
            };
            if (result.Outcome == TransferOutcome.Copied)
                JoinFraction = 1;
            AppLog.Write("Join " + result.Outcome + " " + output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            JoinStatus = ex.Message;
        }
        finally
        {
            _joining = false;
            _joinRun?.Dispose();
            _joinRun = null;
            RaiseBusy();
            if (_closeAfterStop && _activeCopies == 0)
                CloseRequested?.Invoke();
        }
    }

    private void RaiseBusy()
    {
        Raise(nameof(Transferring));
        Raise(nameof(Joining));
        Raise(nameof(CanEdit));
        AddFilesCommand.Raise();
        AddFolderCommand.Raise();
        RemoveCommand.Raise();
        StartCommand.Raise();
        StopCommand.Raise();
        ReplaceCommand.Raise();
        SelectDriveCommand.Raise();
        EjectCommand.Raise();
        RefreshCommand.Raise();
        DeleteLibraryCommand.Raise();
        OrganizeRomsCommand.Raise();
        MergeLibraryCommand.Raise();
        PickJoinCommand.Raise();
        JoinCommand.Raise();
    }

    private static string? FragmentWarning(TransferPlan plan)
    {
        var notes = new List<string>();
        foreach (var path in plan.OutputPaths)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 32L * 1024 * 1024)
                continue;
            var extents = VolumeApi.ExtentCount(path);
            if (extents is > 32)
                notes.Add(Path.GetFileName(path) + " is in " + extents + " pieces");
            if (notes.Count == 3)
                break;
        }

        return notes.Count == 0
            ? null
            : string.Join(", ", notes) + ". A badly fragmented file can fail to mount on the PS3. Optimize the drive in Windows, then copy the game again.";
    }
}
