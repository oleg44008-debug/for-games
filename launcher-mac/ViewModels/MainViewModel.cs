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
using Avalonia.Media.Imaging;
using DustoreLauncherV.Mac.Controls;
using DustoreLauncherV.Mac.Services;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    // The interface is Russian, so dates follow it rather than the system locale.
    private static readonly System.Globalization.CultureInfo Russian = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
    private readonly LauncherServices _services;
    private IReadOnlyList<GameEntry> _entries = Array.Empty<GameEntry>();
    private readonly List<string> _log = new();
    private readonly Dictionary<Guid, Bitmap> _covers = new();
    private readonly HashSet<Guid> _coverAttempts = new();
    private CancellationTokenSource? _operation;
    private GameItemViewModel? _selectedGame;
    private string _search = "", _source = "", _gameName = "", _output = "";
    private string _status = "Добавьте игру в библиотеку или выберите сборку в eX.";
    private string _error = "", _result = "", _runtimeVersion = "", _runtimePath = "";
    private string _webTitle = "", _webAddress = "";
    private WebLoadError? _webError;
    private DownloadSnapshot? _download;
    private int _importedDownloadId;
    private Guid? _downloadedGameId;
    private string _downloadNote = "";
    private double _webProgress;
    private bool _webLoading, _webCanGoBack, _webCanGoForward;
    private bool _busy;
    private string _section = "library";
    private string _shelf = "all";
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
        StoreCommand = new RelayCommand(() => Section = "store");
        HomeCommand = new RelayCommand(() => Section = "home");
        JamsCommand = new RelayCommand(() => Section = "jams");
        AssetsCommand = new RelayCommand(() => Section = "assets");
        OpenInBrowserCommand = new AsyncCommand(() => PerformAsync("Открываю страницу в браузере…",
            ct => _services.OpenUrlAsync(HasWebError ? WebRetryUrl : string.IsNullOrWhiteSpace(WebAddress) ? WebStartUrl : WebAddress, ct)), () => !IsBusy);
        ShelfAllCommand = new RelayCommand(() => Shelf = "all");
        ShelfReadyCommand = new RelayCommand(() => Shelf = "ready");
        ShelfExCommand = new RelayCommand(() => Shelf = "ex");
        SelectGameCommand = new RelayCommand<GameItemViewModel>(game => SelectedGame = game);
        TargetMacCommand = new RelayCommand(() => SelectedTarget = Targets[0], () => !IsBusy);
        TargetWindowsCommand = new RelayCommand(() => SelectedTarget = Targets[1], () => !IsBusy);
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
    public ObservableCollection<object> ShelfItems { get; } = new();
    public IReadOnlyList<TargetChoice> Targets { get; }
    public IReadOnlyList<ArchitectureChoice> Architectures { get; }
    public ICommand LibraryCommand { get; }
    public ICommand ExCommand { get; }
    public ICommand StoreCommand { get; }
    public ICommand HomeCommand { get; }
    public ICommand JamsCommand { get; }
    public ICommand AssetsCommand { get; }
    public ICommand OpenInBrowserCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ShelfAllCommand { get; }
    public ICommand ShelfReadyCommand { get; }
    public ICommand ShelfExCommand { get; }
    public ICommand SelectGameCommand { get; }
    public ICommand TargetMacCommand { get; }
    public ICommand TargetWindowsCommand { get; }
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
    public bool CanAnalyze => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath);
    public bool CanConvert => !IsBusy && _plan?.CanConvert == true && !string.IsNullOrWhiteSpace(OutputPath) && !string.IsNullOrWhiteSpace(GameName);
    public bool CanLaunch => !IsBusy && SelectedGame?.CanLaunch == true;
    public string LibraryCount => _entries.Count + " " + Plural(_entries.Count, "игра", "игры", "игр");
    public string FilterCount => Games.Count == 0 ? (_entries.Count == 0 ? "Библиотека пока пуста" : "Ничего не найдено") : $"Показано: {Games.Count}";
    public string DataDirectory => _services.DataDirectory;
    public string CacheDirectory => RuntimeCatalog.CacheDirectory;
    public string PlatformLabel => "macOS · " + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Apple Silicon" : "Intel / x64");
    public static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
    public string RailVersion => "LAUNCHER " + AppVersion + " · MAC";

    // Sections. Web sections share one in-app WKWebView.
    public string Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            foreach (string property in new[] { nameof(IsLibrary), nameof(IsEx), nameof(IsSettings), nameof(IsStore), nameof(IsHome), nameof(IsJams), nameof(IsAssets),
                nameof(IsWeb), nameof(WebStartUrl), nameof(HeaderTitle), nameof(HeaderSubtitle), nameof(ShowSearch) })
                Notify(property);
        }
    }
    public bool IsLibrary => Section == "library";
    public bool IsEx => Section == "ex";
    public bool IsSettings => Section == "settings";
    public bool IsStore => Section == "store";
    public bool IsHome => Section == "home";
    public bool IsJams => Section == "jams";
    public bool IsAssets => Section == "assets";
    public bool IsWeb => WebStartUrlFor(Section) is not null;
    public bool ShowSearch => IsLibrary && HasGames;
    public string HeaderTitle => Section switch
    {
        "ex" => "eX · перенос игр", "settings" => "Настройки", "store" => "Магазин", "home" => "Главная Dustore",
        "jams" => "Спринты", "assets" => "Ассеты", _ => "Библиотека"
    };
    public string HeaderSubtitle => Section switch
    {
        "library" => LibraryCount,
        "ex" => "Windows · macOS",
        "settings" => PlatformLabel,
        _ => string.IsNullOrWhiteSpace(WebTitle) ? "dustore.ru" : WebTitle
    };

    public bool IsWebSupported => NativeWebView.IsSupported;
    public bool IsWebUnsupported => !IsWebSupported;
    public string WebStartUrl => WebStartUrlFor(Section) ?? "https://dustore.ru/";
    public static string? WebStartUrlFor(string section) => section switch
    {
        "store" => "https://dustore.ru/explore",
        "home" => "https://dustore.ru/",
        "jams" => "https://dustore.ru/jams",
        "assets" => "https://dustore.ru/assetstore",
        _ => null
    };
    public string WebTitle { get => _webTitle; private set => Set(ref _webTitle, value); }
    public string WebAddress { get => _webAddress; private set => Set(ref _webAddress, value); }
    public double WebProgress { get => _webProgress; private set => Set(ref _webProgress, value); }
    public bool WebLoading { get => _webLoading; private set => Set(ref _webLoading, value); }
    public bool WebCanGoBack { get => _webCanGoBack; private set => Set(ref _webCanGoBack, value); }
    public bool WebCanGoForward { get => _webCanGoForward; private set => Set(ref _webCanGoForward, value); }

    public WebLoadError? WebError { get => _webError; private set { if (Set(ref _webError, value)) foreach (string p in new[] { nameof(HasWebError), nameof(WebViewVisible), nameof(WebErrorMessage), nameof(WebErrorHint) }) Notify(p); } }
    public bool HasWebError => WebError is not null;
    public bool WebViewVisible => IsWebSupported && !HasWebError;
    public string WebErrorMessage => WebError?.Message ?? "";
    public string WebErrorHint => WebError switch
    {
        null => "",
        { IsCertificateProblem: true } => "Защищённое соединение не установилось. Чаще всего так бывает, когда на Mac неверные дата и время: "
            + "сертификат сайта для macOS «ещё не начал действовать». Сейчас на этом Mac: " + DateTime.Now.ToString("d MMMM yyyy, HH:mm", Russian)
            + ". Включите «Устанавливать время и дату автоматически» в Системных настройках → Основные → Дата и время.",
        { IsOffline: true } => "Нет связи с dustore.ru. Проверьте подключение к интернету.",
        _ => "Попробуйте ещё раз или откройте страницу в браузере."
    };
    public string WebRetryUrl => WebError?.FailingUrl is { Length: > 0 } failed ? failed : WebStartUrl;
    public void ClearWebError() => WebError = null;

    // Store downloads: progress under the in-app site, then the game joins the library.
    public bool HasDownload => _download is not null;
    public bool DownloadRunning => _download?.Status == DownloadStatus.Running;
    public bool DownloadReady => _downloadedGameId is not null && _download?.Status == DownloadStatus.Finished;
    public Guid? DownloadedGameId => _downloadedGameId;
    public DownloadSnapshot? LastDownload => _download;
    public string DownloadName => _download?.Name is { Length: > 0 } name ? name : "Игра";
    public double DownloadPercent => (_download?.Fraction ?? 0) * 100;
    public string DownloadText => _download switch
    {
        null => "",
        { Status: DownloadStatus.Running } d => d.TotalBytes > 0
            ? $"{Megabytes(d.ReceivedBytes)} из {Megabytes(d.TotalBytes)} МБ · {d.Fraction * 100:0}%" : $"{Megabytes(d.ReceivedBytes)} МБ",
        { Status: DownloadStatus.Finished } => _downloadNote.Length > 0 ? _downloadNote : "Скачано. Добавляю в библиотеку…",
        { Status: DownloadStatus.Cancelled } => "Загрузка отменена.",
        { Status: DownloadStatus.Failed } d => "Не удалось скачать: " + d.Error,
        _ => ""
    };
    public ICommand OpenDownloadedGameCommand => new RelayCommand(() =>
    {
        if (_downloadedGameId is { } id) { Shelf = "all"; Section = "library"; SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == id) ?? SelectedGame; }
    });
    public ICommand DismissDownloadCommand => new RelayCommand(() => { _download = null; NotifyDownload(); });
    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.0" : "0", Russian);

    public void UpdateDownload(DownloadSnapshot download)
    {
        bool finishedNow = download.Status == DownloadStatus.Finished && download.Id != _importedDownloadId;
        if (_download?.Id != download.Id) { _downloadNote = ""; _downloadedGameId = null; }
        _download = download;
        NotifyDownload();
        if (finishedNow)
        {
            _importedDownloadId = download.Id;
            _ = ImportDownloadAsync(download);
        }
    }

    /// <summary>
    /// Sites whose downloads count as store downloads: dustore.ru and its subdomains. An entry
    /// with a port ("host:port") matches that exact address only (used by the Mac CI store mock).
    /// </summary>
    public static HashSet<string> TrustedStoreHosts { get; } = new(StringComparer.OrdinalIgnoreCase) { "dustore.ru" };
    public static bool IsTrustedStorePage(string page) =>
        Uri.TryCreate(page, UriKind.Absolute, out var url)
        && TrustedStoreHosts.Any(host => host.Contains(':')
            ? url.Authority.Equals(host, StringComparison.OrdinalIgnoreCase)
            : url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    public void OpenDownloadInBrowser(string url) => _ = PerformAsync("Открываю загрузку в браузере…", ct => _services.OpenUrlAsync(url, ct));

    private async Task ImportDownloadAsync(DownloadSnapshot download)
    {
        // A running operation finishes first; downloads never interrupt eX.
        while (IsBusy) await Task.Delay(300);
        await PerformAsync("Добавляю скачанную игру в библиотеку…", async ct =>
        {
            // Like Steam: games the launcher fetched from the Dustore store open without the
            // Gatekeeper prompt. Anything offered by another site keeps macOS's usual checks.
            if (IsTrustedStorePage(download.SourcePage)) MacQuarantine.RemoveFromStoreDownload(download.Path);
            var entry = await _services.AddGameAsync(download.Path, ct);
            await ReloadLibraryAsync(ct, SelectedGame?.Entry.Id ?? entry.Id);
            _downloadedGameId = entry.Id;
            _downloadNote = entry.CanLaunchOnMac ? "Готово: игра в библиотеке и готова к запуску." : "Готово: игра в библиотеке. Для Mac перенесите её через eX.";
            Status = _downloadNote;
        });
        if (_downloadedGameId is null && HasError) _downloadNote = "Скачано, но не добавлено: " + Error;
        NotifyDownload();
    }

    private void NotifyDownload()
    {
        foreach (string property in new[] { nameof(HasDownload), nameof(DownloadRunning), nameof(DownloadReady), nameof(DownloadName), nameof(DownloadPercent), nameof(DownloadText) })
            Notify(property);
    }

    public void UpdateWebState(string url, string title, bool loading, double progress, bool canGoBack, bool canGoForward, WebLoadError? error = null)
    {
        WebError = error;
        WebAddress = url;
        WebTitle = title;
        WebLoading = loading;
        WebProgress = loading ? Math.Clamp(progress, 0.08, 1) * 100 : 0;
        WebCanGoBack = canGoBack;
        WebCanGoForward = canGoForward;
        Notify(nameof(HeaderSubtitle));
    }

    // Library shelf and hero.
    public bool HasGames => _entries.Count > 0;
    public bool IsLibraryEmpty => _entries.Count == 0;
    public string Shelf { get => _shelf; set { if (Set(ref _shelf, value)) { Notify(nameof(IsShelfAll)); Notify(nameof(IsShelfReady)); Notify(nameof(IsShelfEx)); ApplyFilter(); } } }
    public bool IsShelfAll => Shelf == "all";
    public bool IsShelfReady => Shelf == "ready";
    public bool IsShelfEx => Shelf == "ex";
    public int CountAll => _entries.Count;
    public int CountReady => _entries.Count(e => e.CanLaunchOnMac);
    public int CountEx => _entries.Count(e => !e.CanLaunchOnMac);
    public bool ShelfEmpty => Games.Count == 0 && HasGames;
    public string SelectedTitle => SelectedGame?.Name ?? "Ваша библиотека";
    public string SelectedSource => SelectedGame?.SourcePath ?? "";
    public string SelectedStatus => SelectedGame?.Status ?? "";
    public string SelectedAdded => SelectedGame is null ? "" : SelectedGame.Entry.AddedUtc.ToLocalTime().ToString("d MMM yyyy", Russian);
    public string SelectedLastPlayed => SelectedGame?.Entry.LastPlayedUtc is { } when ? when.ToLocalTime().ToString("d MMM, HH:mm", Russian) : "Не запускали";
    public string SelectedKind => SelectedGame?.KindChip ?? "";
    public Bitmap? SelectedCover => SelectedGame?.Cover;
    public bool SelectedHasCover => SelectedGame?.Cover is not null;
    public bool SelectedNoCover => SelectedGame is not null && SelectedGame.Cover is null;
    public string SelectedMonogram => SelectedGame?.Monogram ?? "D";
    public bool SelectedReady => SelectedGame?.CanLaunch == true;
    public bool SelectedNeedsEx => SelectedGame is not null && !SelectedGame.CanLaunch;
    public string SelectedStateLabel => SelectedGame is null ? "" : SelectedGame.CanLaunch ? "ГОТОВО К ЗАПУСКУ"
        : !SelectedGame.Entry.SourceExists ? "ФАЙЛ НЕ НАЙДЕН" : "НУЖЕН ПЕРЕНОС ЧЕРЕЗ eX";
    public string LaunchLabel => "ИГРАТЬ";
    public string LaunchHint => SelectedGame is null ? "" : SelectedGame.CanLaunch ? "Приложение откроется обычным способом macOS."
        : "Для Windows-сборки сначала создайте macOS-пакет через eX. Для неизвестного формата нужен готовый Mac-порт.";

    // eX.
    public string PlanTitle => _plan?.Title ?? "Выберите игру — eX проверит её";
    public string PlanDetail => _plan?.Detail ?? "eX определит движок и доступный способ переноса. Исходные файлы останутся на месте.";
    public string PlanFacts => _plan is null ? "Godot · LÖVE · Ren’Py · NW.js" : $"{_plan.Engine}  ·  {_plan.SourcePlatform} → {_plan.Target}";
    public string PlanWarnings => _plan is null ? "" : string.Join("\n\n", _plan.Warnings);
    public bool HasWarnings => _plan?.Warnings.Count > 0;
    public string PlanChip => _plan is null ? "Ожидает анализа" : _plan.CanConvert ? "Доступен перенос" : "Перенос недоступен";
    public bool PlanReady => _plan?.CanConvert == true;
    public bool PlanBlocked => _plan is not null && !_plan.CanConvert;
    public bool PlanPending => _plan is null;
    public string ProgressLog => string.Join("\n", _log);
    public string SourceDisplayName => string.IsNullOrWhiteSpace(SourcePath) ? "Игра не выбрана" : Path.GetFileName(SourcePath.TrimEnd('/', '\\'));
    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);
    public bool IsMacTarget => SelectedTarget.Platform == TargetPlatform.MacOS;
    public bool IsTargetWindows => !IsMacTarget;
    public bool CanChooseArchitecture => IsMacTarget && !IsBusy;
    public string ConvertLabel => IsMacTarget ? "Создать пакет macOS" : "Создать пакет Windows";

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) ApplyFilter(); } }
    public string SourcePath { get => _source; set { if (Set(ref _source, value)) { ResultPath = ""; Notify(nameof(SourceDisplayName)); Notify(nameof(HasSource)); ResetPlan(); } } }
    public string GameName { get => _gameName; set { if (Set(ref _gameName, value)) UpdateActions(); } }
    public string OutputPath { get => _output; set { if (Set(ref _output, value)) UpdateActions(); } }
    public string RuntimeVersion { get => _runtimeVersion; set => Set(ref _runtimeVersion, value); }
    public string RuntimePath { get => _runtimePath; set => Set(ref _runtimePath, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) Notify(nameof(HasError)); } }
    public string ResultPath { get => _result; private set { if (Set(ref _result, value)) { Notify(nameof(HasResult)); UpdateActions(); } } }
    public TargetChoice SelectedTarget
    {
        get => _target;
        set
        {
            if (value is null || !Set(ref _target, value)) return;
            ResetPlan();
            SetDefaultOutput();
            foreach (string property in new[] { nameof(IsMacTarget), nameof(IsTargetWindows), nameof(ConvertLabel), nameof(CanChooseArchitecture) }) Notify(property);
        }
    }
    public ArchitectureChoice SelectedArchitecture { get => _architecture; set { if (value is not null && Set(ref _architecture, value)) ResetPlan(); } }
    public GameItemViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            var previous = _selectedGame;
            if (!Set(ref _selectedGame, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            NotifySelection();
            UpdateActions();
        }
    }

    public Task InitializeAsync() => PerformAsync("Загружаю библиотеку…", async cancellation =>
    {
        await ReloadLibraryAsync(cancellation);
        Status = _entries.Count == 0
            ? "Библиотека готова. Добавьте игру или выберите исходную сборку в eX."
            : "Библиотека готова.";
    });

    public async Task ImportGameAsync(string path)
    {
        await PerformAsync("Добавляю игру…", async ct =>
        {
            var entry = await _services.AddGameAsync(path, ct);
            Shelf = "all";
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
    public void DismissError() => Error = "";

    public void ReportError(Exception error)
    {
        Error = error.Message;
        Status = "Операция не завершена.";
    }

    public Task AnalyzeAsync() => PerformAsync("Анализирую сборку…", async ct =>
    {
        _plan = await _services.InspectAsync(SourcePath, SelectedTarget.Platform, EffectiveArchitecture, ct);
        RuntimeVersion = _plan.RuntimeVersion ?? "";
        NotifyPlan();
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
        foreach (string property in new[] { nameof(LibraryCount), nameof(HasGames), nameof(IsLibraryEmpty), nameof(CountAll), nameof(CountReady), nameof(CountEx), nameof(HeaderSubtitle), nameof(ShowSearch), nameof(ShelfEmpty) })
            Notify(property);
        _ = LoadCoversAsync();
    }

    private async Task LoadCoversAsync()
    {
        foreach (var entry in _entries.ToArray())
        {
            if (_covers.ContainsKey(entry.Id) || !_coverAttempts.Add(entry.Id)) continue;
            string? path;
            try { path = await CoverExtractor.GetCoverAsync(entry, _services.DataDirectory, CancellationToken.None); }
            catch (Exception) { continue; }
            if (path is null) continue;
            try { _covers[entry.Id] = new Bitmap(path); }
            catch (Exception) { continue; }
            foreach (var game in Games.Where(g => g.Entry.Id == entry.Id)) game.Cover = _covers[entry.Id];
            if (SelectedGame?.Entry.Id == entry.Id) NotifySelection();
        }
    }

    private void ApplyFilter(Guid? select = null)
    {
        select ??= SelectedGame?.Entry.Id;
        string query = Search.Trim();
        var visible = _entries.OrderByDescending(g => g.AddedUtc)
            .Where(g => query.Length == 0 || g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || g.SourcePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(g => Shelf switch { "ready" => g.CanLaunchOnMac, "ex" => !g.CanLaunchOnMac, _ => true })
            .ToArray();
        Games.Clear();
        ShelfItems.Clear();
        foreach (var entry in visible)
        {
            var item = new GameItemViewModel(entry) { Cover = _covers.GetValueOrDefault(entry.Id) };
            Games.Add(item);
            ShelfItems.Add(item);
        }
        ShelfItems.Add(AddGameTile.Instance);
        _selectedGame = null;
        SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == select) ?? Games.FirstOrDefault();
        if (SelectedGame is null) { NotifySelection(); UpdateActions(); }
        Notify(nameof(FilterCount));
        Notify(nameof(ShelfEmpty));
    }

    private void NotifySelection()
    {
        foreach (string property in new[] { nameof(HasSelection), nameof(NoSelection), nameof(SelectedTitle), nameof(SelectedSource), nameof(SelectedStatus),
            nameof(SelectedAdded), nameof(SelectedLastPlayed), nameof(LaunchHint), nameof(SelectedCover), nameof(SelectedHasCover), nameof(SelectedNoCover),
            nameof(SelectedMonogram), nameof(SelectedKind), nameof(SelectedReady), nameof(SelectedNeedsEx), nameof(SelectedStateLabel) })
            Notify(property);
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
        NotifyPlan();
        UpdateActions();
    }
    private void NotifyPlan()
    {
        foreach (string property in new[] { nameof(HasPlan), nameof(PlanTitle), nameof(PlanDetail), nameof(PlanFacts), nameof(PlanWarnings), nameof(HasWarnings),
            nameof(PlanChip), nameof(PlanReady), nameof(PlanBlocked), nameof(PlanPending) })
            Notify(property);
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
        foreach (var command in new[] { LibraryCommand, ExCommand, SettingsCommand, StoreCommand, OpenInBrowserCommand, TargetMacCommand, TargetWindowsCommand, LaunchCommand,
            RevealGameCommand, RemoveGameCommand, ConvertGameCommand, AnalyzeCommand, ConvertCommand, CancelCommand, RevealOutputCommand, RevealDataCommand, RevealCacheCommand, RefreshCommand })
            if (command is ICommandNotifications notifications) notifications.RaiseCanExecuteChanged();
    }
    private static string Plural(int count, string one, string few, string many)
    {
        int mod100 = count % 100, mod10 = count % 10;
        return mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
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

/// <summary>The dashed "add a game" tile that closes the library shelf.</summary>
public sealed class AddGameTile
{
    public static readonly AddGameTile Instance = new();
    private AddGameTile() { }
}

public sealed class GameItemViewModel : INotifyPropertyChanged
{
    private bool _selected;
    private Bitmap? _cover;
    public GameItemViewModel(GameEntry entry) => Entry = entry;
    public event PropertyChangedEventHandler? PropertyChanged;
    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string SourcePath => Entry.SourcePath;
    public bool CanLaunch => Entry.CanLaunchOnMac;
    public string Status => CanLaunch ? "Готова к запуску на Mac" : !Entry.SourceExists ? "Исходный файл не найден" : Entry.Kind + " · перенос через eX";
    public string ShortStatus => CanLaunch ? "ГОТОВА" : !Entry.SourceExists ? "НЕ НАЙДЕНА" : "НУЖЕН eX";
    public string KindChip => CanLaunch ? "MACOS" : Entry.Kind.ToUpperInvariant();
    public string Monogram => string.IsNullOrWhiteSpace(Name) ? "D" : Name[..1].ToUpperInvariant();
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(nameof(IsSelected)); } }
    public Bitmap? Cover { get => _cover; set { if (ReferenceEquals(_cover, value)) return; _cover = value; Raise(nameof(Cover)); Raise(nameof(HasCover)); Raise(nameof(NoCover)); } }
    public bool HasCover => Cover is not null;
    public bool NoCover => Cover is null;
    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
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
internal sealed class RelayCommand<T> : ICommand where T : class
{
    private readonly Action<T> _execute;
    public RelayCommand(Action<T> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T;
    public void Execute(object? parameter) { if (parameter is T value) _execute(value); }
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
