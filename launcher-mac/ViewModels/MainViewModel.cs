using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DustoreLauncherV.Mac.Services;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly LauncherServices _services;
    private IReadOnlyList<GameEntry> _entries = Array.Empty<GameEntry>();
    private readonly List<string> _log = new();
    private CancellationTokenSource? _operation;
    private GameItemViewModel? _selectedGame;
    private string _search = "", _source = "", _gameName = "", _output = "";
    private string _status = "Добавьте игру в библиотеку или выберите сборку в eX.";
    private string _error = "", _result = "", _runtimeVersion = "", _runtimePath = "";
    private bool _busy;
    private string _section = "library";
    private ConversionPlan? _plan;
    private TargetChoice _target;
    private ArchitectureChoice _architecture;

    public MainViewModel() : this(new LauncherServices()) { }

    public MainViewModel(LauncherServices services)
    {
        _services = services;
        Targets = new[] { new TargetChoice("macOS", TargetPlatform.MacOS), new TargetChoice("Windows", TargetPlatform.Windows) };
        Architectures = new[] { new ArchitectureChoice("Apple Silicon · ARM64", "arm64"), new ArchitectureChoice("Intel · x64", "x64") };
        _target = Targets[0];
        _architecture = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? Architectures[1] : Architectures[0];
        LibraryCommand = new RelayCommand(() => Section = "library");
        ExCommand = new RelayCommand(() => Section = "ex");
        SettingsCommand = new RelayCommand(() => Section = "settings");
        StoreCommand = new AsyncCommand(() => PerformAsync("Открываю Dustore…", ct => _services.OpenUrlAsync("https://dustore.ru/explore", ct)), () => !IsBusy);
        LaunchCommand = new AsyncCommand(LaunchSelectedAsync, () => CanLaunch);
        RevealGameCommand = new AsyncCommand(() => SelectedGame is null ? Task.CompletedTask : PerformAsync("Открываю папку игры…", ct => _services.RevealAsync(SelectedGame.SourcePath, ct)), () => SelectedGame is not null && !IsBusy);
        RemoveGameCommand = new AsyncCommand(RemoveSelectedAsync, () => SelectedGame is not null && !IsBusy);
        ConvertGameCommand = new AsyncCommand(async () => { if (SelectedGame is null) return; Section = "ex"; await SetSourceAsync(SelectedGame.SourcePath); }, () => SelectedGame is not null && !IsBusy);
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync, () => CanAnalyze);
        ConvertCommand = new AsyncCommand(ConvertAsync, () => CanConvert);
        CancelCommand = new RelayCommand(() => { _operation?.Cancel(); Status = "Запрошена отмена. Ожидаю завершения текущего шага…"; }, () => IsBusy);
        RevealOutputCommand = new AsyncCommand(() => PerformAsync("Открываю готовый пакет…", ct => _services.RevealAsync(ResultPath, ct)), () => HasResult && !IsBusy);
        RevealDataCommand = new AsyncCommand(() => PerformAsync("Открываю папку библиотеки…", ct => _services.RevealAsync(DataDirectory, ct)), () => !IsBusy);
        RevealCacheCommand = new AsyncCommand(() => PerformAsync("Открываю кэш движков…", async ct => { Directory.CreateDirectory(RuntimeCatalog.CacheDirectory); await _services.RevealAsync(RuntimeCatalog.CacheDirectory, ct); }), () => !IsBusy);
        RefreshCommand = new AsyncCommand(() => PerformAsync("Обновляю библиотеку…", ReloadLibraryAsync), () => !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<GameItemViewModel> Games { get; } = new();
    public IReadOnlyList<TargetChoice> Targets { get; }
    public IReadOnlyList<ArchitectureChoice> Architectures { get; }
    public ICommand LibraryCommand { get; }
    public ICommand ExCommand { get; }
    public ICommand StoreCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand RevealGameCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ConvertGameCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand ConvertCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RevealOutputCommand { get; }
    public ICommand RevealDataCommand { get; }
    public ICommand RevealCacheCommand { get; }
    public ICommand RefreshCommand { get; }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) UpdateActions(); } }
    public bool NotBusy => !IsBusy;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultPath);
    public bool HasPlan => _plan is not null;
    public bool HasSelection => SelectedGame is not null;
    public bool NoSelection => !HasSelection;
    public bool IsLibrary => Section == "library";
    public bool IsEx => Section == "ex";
    public bool IsSettings => Section == "settings";
    public bool CanAnalyze => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath);
    public bool CanConvert => !IsBusy && _plan?.CanConvert == true && !string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(GameName);
    public bool CanLaunch => !IsBusy && SelectedGame?.CanLaunch == true;
    public string LibraryCount => $"{_entries.Count} игр";
    public string FilterCount => Games.Count == 0 ? (_entries.Count == 0 ? "Библиотека пока пуста" : "Ничего не найдено") : $"Показано: {Games.Count}";
    public string DataDirectory => _services.DataDirectory;
    public string CacheDirectory => RuntimeCatalog.CacheDirectory;
    public string PlatformLabel => "macOS · " + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Apple Silicon" : "Intel / x64");
    public string PlanTitle => _plan?.Title ?? "Выберите игру и нажмите «Анализировать»";
    public string PlanDetail => _plan?.Detail ?? "eX проверит движок и определит доступный способ переноса. Исходные файлы останутся на месте.";
    public string PlanFacts => _plan is null ? "Godot · LÖVE · Ren’Py · NW.js" : $"{_plan.Engine}   ·   {_plan.SourcePlatform} → {_plan.Target}";
    public string PlanWarnings => _plan is null ? "" : string.Join("\n\n", _plan.Warnings);
    public bool HasWarnings => _plan?.Warnings.Count > 0;
    public string ProgressLog => string.Join("\n", _log);
    public string SelectedTitle => SelectedGame?.Name ?? "Ваша библиотека";
    public string SelectedSource => SelectedGame?.SourcePath ?? "";
    public string SelectedStatus => SelectedGame?.Status ?? "";
    public string SelectedAdded => SelectedGame is null ? "" : SelectedGame.Entry.AddedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string SelectedLastPlayed => SelectedGame?.Entry.LastPlayedUtc is { } when ? when.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "Ещё не запускалась";
    public string LaunchLabel => "Играть";
    public string LaunchHint => SelectedGame is null ? "" : SelectedGame.CanLaunch ? "Приложение откроется обычным способом macOS." : "Для Windows-сборки сначала создайте macOS-пакет через eX. Для неизвестного формата нужен готовый Mac-порт.";

    public string Section { get => _section; set { if (Set(ref _section, value)) { Notify(nameof(IsLibrary)); Notify(nameof(IsEx)); Notify(nameof(IsSettings)); } } }
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyFilter(); } }
    public string SourcePath { get => _source; set { if (Set(ref _source, value)) { ResultPath = ""; ResetPlan(); } } }
    public string GameName { get => _gameName; set { if (Set(ref _gameName, value)) UpdateActions(); } }
    public string OutputPath { get => _output; set { if (Set(ref _output, value)) UpdateActions(); } }
    public string RuntimeVersion { get => _runtimeVersion; set => Set(ref _runtimeVersion, value); }
    public string RuntimePath { get => _runtimePath; set => Set(ref _runtimePath, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) Notify(nameof(HasError)); } }
    public string ResultPath { get => _result; private set { if (Set(ref _result, value)) { Notify(nameof(HasResult)); UpdateActions(); } } }
    public TargetChoice SelectedTarget { get => _target; set { if (value is not null && Set(ref _target, value)) { ResetPlan(); SetDefaultOutput(); Notify(nameof(IsMacTarget)); } } }
    public ArchitectureChoice SelectedArchitecture { get => _architecture; set { if (value is not null && Set(ref _architecture, value)) ResetPlan(); } }
    public bool IsMacTarget => SelectedTarget.Platform == TargetPlatform.MacOS;
    public bool CanChooseArchitecture => IsMacTarget && !IsBusy;
    public GameItemViewModel? SelectedGame { get => _selectedGame; set { if (Set(ref _selectedGame, value)) { foreach (string property in new[] { nameof(HasSelection), nameof(NoSelection), nameof(SelectedTitle), nameof(SelectedSource), nameof(SelectedStatus), nameof(SelectedAdded), nameof(SelectedLastPlayed), nameof(LaunchHint) }) Notify(property); UpdateActions(); } } }

    public Task InitializeAsync() => PerformAsync("Загружаю библиотеку…", ReloadLibraryAsync);

    public async Task ImportGameAsync(string path)
    {
        await PerformAsync("Добавляю игру…", async ct =>
        {
            var entry = await _services.AddGameAsync(path, ct);
            await ReloadLibraryAsync(ct, entry.Id);
            Section = "library";
            Status = "Игра добавлена. Исходные файлы сохранены.";
        });
    }

    public async Task SetSourceAsync(string path)
    {
        if (IsBusy) return;
        SourcePath = path;
        GameName = Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        RuntimePath = "";
        RuntimeVersion = "";
        ResultPath = "";
        SetDefaultOutput();
        await AnalyzeAsync();
    }

    public void CancelOperation() => _operation?.Cancel();

    public void ReportError(Exception error)
    {
        Error = error.Message;
        Status = "Операция не завершена.";
    }

    public Task AnalyzeAsync() => PerformAsync("Анализирую сборку…", async ct =>
    {
        _plan = await _services.InspectAsync(SourcePath, SelectedTarget.Platform, EffectiveArchitecture, ct);
        RuntimeVersion = _plan.RuntimeVersion ?? "";
        foreach (string property in new[] { nameof(HasPlan), nameof(PlanTitle), nameof(PlanDetail), nameof(PlanFacts), nameof(PlanWarnings), nameof(HasWarnings) }) Notify(property);
        Status = _plan.CanConvert ? "Анализ завершён. Проверьте систему и путь готового ZIP." : _plan.Title;
    });

    private async Task ConvertAsync()
    {
        if (!CanConvert || _plan is null) return;
        await PerformAsync("Создаю пакет…", async ct =>
        {
            var request = new ConversionRequest(SourcePath, SelectedTarget.Platform, GameName.Trim(), OutputPath,
                string.IsNullOrWhiteSpace(RuntimeVersion) ? null : RuntimeVersion.Trim(), EffectiveArchitecture,
                string.IsNullOrWhiteSpace(RuntimePath) ? null : RuntimePath.Trim(), _plan.Method);
            var result = await _services.ConvertAsync(request, new Progress<string>(AppendLog), ct);
            ResultPath = result.OutputPath;
            foreach (string warning in result.Warnings) AppendLog(warning);
            if (request.Target == TargetPlatform.MacOS)
            {
                var original = _entries.FirstOrDefault(g => string.Equals(g.SourcePath, request.InputPath, StringComparison.Ordinal));
                original ??= await _services.AddGameAsync(request.InputPath, CancellationToken.None);
                var ready = await _services.PrepareConvertedMacAsync(original.Id, result, CancellationToken.None);
                await ReloadLibraryAsync(CancellationToken.None, ready.Id);
                Status = "macOS-пакет готов. Игра добавлена в библиотеку для запуска.";
            }
            else Status = "Windows-пакет готов. Откройте ZIP на Windows для проверки запуска.";
        });
    }

    private Task LaunchSelectedAsync() => SelectedGame is null ? Task.CompletedTask : PerformAsync("Запускаю игру…", async ct =>
    {
        var entry = SelectedGame.Entry;
        await _services.LaunchAsync(entry, ct);
        await ReloadLibraryAsync(ct, entry.Id);
        Status = "Игра передана macOS для запуска.";
    });

    private Task RemoveSelectedAsync() => SelectedGame is null ? Task.CompletedTask : PerformAsync("Удаляю запись из библиотеки…", async ct =>
    {
        await _services.RemoveGameAsync(SelectedGame.Entry.Id, ct);
        await ReloadLibraryAsync(ct);
        Status = "Запись удалена из библиотеки. Исходная игра осталась на диске.";
    });

    private async Task ReloadLibraryAsync(CancellationToken ct) => await ReloadLibraryAsync(ct, SelectedGame?.Entry.Id);
    private async Task ReloadLibraryAsync(CancellationToken ct, Guid? select)
    {
        _entries = await _services.LoadLibraryAsync(ct);
        ApplyFilter(select);
        Notify(nameof(LibraryCount));
    }

    private void ApplyFilter(Guid? select = null)
    {
        select ??= SelectedGame?.Entry.Id;
        string query = Search.Trim();
        Games.Clear();
        foreach (var entry in _entries.OrderByDescending(g => g.AddedUtc).Where(g => query.Length == 0 || g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || g.SourcePath.Contains(query, StringComparison.OrdinalIgnoreCase))) Games.Add(new GameItemViewModel(entry));
        SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == select) ?? Games.FirstOrDefault();
        Notify(nameof(FilterCount));
    }

    private string EffectiveArchitecture => SelectedTarget.Platform == TargetPlatform.Windows ? "x64" : SelectedArchitecture.Key;
    private void SetDefaultOutput()
    {
        if (string.IsNullOrWhiteSpace(GameName)) return;
        string safeName = string.Concat(GameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        OutputPath = Path.Combine(_services.OutputDirectory, safeName + "-" + (IsMacTarget ? "macos" : "windows") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
    }
    private void ResetPlan()
    {
        _plan = null;
        foreach (string property in new[] { nameof(HasPlan), nameof(PlanTitle), nameof(PlanDetail), nameof(PlanFacts), nameof(PlanWarnings), nameof(HasWarnings) }) Notify(property);
        UpdateActions();
    }
    private async Task PerformAsync(string status, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        IsBusy = true; Error = ""; Status = status;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "Операция отменена."; }
        catch (Exception ex) { Error = ex.Message; Status = "Операция не завершена."; AppendLog(ex.Message); }
        finally { _operation = null; IsBusy = false; }
    }
    private void AppendLog(string message)
    {
        _log.Add(message);
        while (_log.Count > 80) _log.RemoveAt(0);
        Notify(nameof(ProgressLog));
        Status = message;
    }
    private void UpdateActions()
    {
        foreach (string property in new[] { nameof(NotBusy), nameof(CanAnalyze), nameof(CanConvert), nameof(CanLaunch), nameof(CanChooseArchitecture) }) Notify(property);
        foreach (var command in new[] { LibraryCommand, ExCommand, SettingsCommand, StoreCommand, LaunchCommand, RevealGameCommand, RemoveGameCommand, ConvertGameCommand, AnalyzeCommand, ConvertCommand, CancelCommand, RevealOutputCommand, RevealDataCommand, RevealCacheCommand, RefreshCommand })
            if (command is ICommandNotifications notifications) notifications.RaiseCanExecuteChanged();
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(property); return true;
    }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record TargetChoice(string Label, TargetPlatform Platform);
public sealed record ArchitectureChoice(string Label, string Key);
public sealed class GameItemViewModel
{
    public GameItemViewModel(GameEntry entry) => Entry = entry;
    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string SourcePath => Entry.SourcePath;
    public bool CanLaunch => Entry.CanLaunchOnMac;
    public string Status => CanLaunch ? "Готова к запуску на Mac" : !Entry.SourceExists ? "Исходный файл не найден" : Entry.Kind + " · перенос через eX";
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "D" : Name[..1].ToUpperInvariant();
}

internal interface ICommandNotifications { void RaiseCanExecuteChanged(); }
internal sealed class RelayCommand : ICommand, ICommandNotifications
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute ?? (() => true); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) _execute(); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
internal sealed class AsyncCommand : ICommand, ICommandNotifications
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _running;
    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute ?? (() => true); }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && _canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; RaiseCanExecuteChanged();
        try { await _execute(); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
