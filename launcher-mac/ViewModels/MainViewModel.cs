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
using Avalonia.Media;
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
    private Task<bool>? _wineInstall;
    private UiPreferences _ui = new();
    private Guid? _launchingId;
    private bool _launchIsWine;
    private string _wineStatus = "";
    private double _wineProgress;
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
    public bool CanLaunch => !IsBusy && SelectedGame?.CanLaunch == true && !SelectedLaunching;
    // A first start takes a while (macOS checks a new app; Wine prepares its prefix). The Play
    // button says so instead of looking idle, which read as "only the second click works".
    public bool SelectedLaunching => SelectedGame is not null && _launchingId == SelectedGame.Entry.Id;
    public string PlayLabel => SelectedLaunching ? "Запуск…" : "Играть";
    public string LaunchNote => !SelectedLaunching ? ""
        : _launchIsWine ? "Windows-игра запускается через Wine (первый запуск — до пары минут). Выйти из игры: ⌘Q, или ⌘Tab → «Закрыть игру»."
        : "macOS открывает игру. Первый запуск новой игры занимает несколько секунд.";
    public bool HasLaunchNote => LaunchNote.Length > 0;

    // Window settings per game: Unity and Godot read them from the command line.
    public IReadOnlyList<WindowChoice> WindowModes => AllWindowModes;
    private static readonly IReadOnlyList<WindowChoice> AllWindowModes = new[]
    {
        new WindowChoice("В окне", GameLaunchOptions.Windowed), new WindowChoice("Весь экран", GameLaunchOptions.Fullscreen),
        new WindowChoice("Как в игре", GameLaunchOptions.GameDefault)
    };
    public IReadOnlyList<ResolutionChoice> Resolutions => AllResolutions;
    private static readonly IReadOnlyList<ResolutionChoice> AllResolutions = new[]
    {
        new ResolutionChoice(1024, 576), new ResolutionChoice(1280, 720), new ResolutionChoice(1440, 810), new ResolutionChoice(1600, 900), new ResolutionChoice(1920, 1080)
    };
    public bool SelectedHasWindowOptions => SelectedGame is not null && SelectedGame.CanLaunch
        && GameLaunchOptions.DetectEngine(SelectedGame.Entry.PreparedMacAppPath ?? SelectedGame.Entry.SourcePath) != GameEngineKind.Other;
    public bool SelectedCanStop => SelectedGame?.CanLaunch == true;
    public WindowChoice? SelectedWindowMode
    {
        get => SelectedGame is null ? null : AllWindowModes.FirstOrDefault(m => m.Key == (SelectedGame.Entry.WindowMode ?? GameLaunchOptions.Fullscreen));
        set { if (value is not null) _ = SaveWindowOptionsAsync(value.Key, SelectedResolution); }
    }
    public ResolutionChoice? SelectedResolution
    {
        get => SelectedGame is null ? null : AllResolutions.FirstOrDefault(r => r.Width == (SelectedGame.Entry.WindowWidth ?? GameLaunchOptions.DefaultWidth)
            && r.Height == (SelectedGame.Entry.WindowHeight ?? GameLaunchOptions.DefaultHeight)) ?? AllResolutions[1];
        set { if (value is not null) _ = SaveWindowOptionsAsync(SelectedWindowMode?.Key ?? GameLaunchOptions.Fullscreen, value); }
    }
    public bool SelectedResolutionMatters => SelectedWindowMode?.Key != GameLaunchOptions.GameDefault;
    // ---- Prime per-game performance ----
    public bool SelectedIsWineGame => SelectedGame?.CanLaunch == true && WineRuntime.IsWineWrapper(SelectedGame.Entry.PreparedMacAppPath);
    public bool SelectedShowsPerformance => SelectedGame?.CanLaunch == true;
    public IReadOnlyList<WindowChoice> GraphicsModes => UltraMode.AppleSilicon ? AllGraphicsModes : AllGraphicsModes.Take(1).ToArray();
    private static readonly IReadOnlyList<WindowChoice> AllGraphicsModes = new[]
    {
        new WindowChoice("Стандарт · DXVK", "standard"), new WindowChoice("ULTRA · Metal", "metal")
    };
    public IReadOnlyList<WindowChoice> FpsLimits => AllFpsLimits;
    private static readonly IReadOnlyList<WindowChoice> AllFpsLimits = new[]
    {
        new WindowChoice("Без ограничения FPS", "0"), new WindowChoice("Не больше 30 FPS", "30"), new WindowChoice("Не больше 60 FPS", "60")
    };
    public WindowChoice? SelectedGraphicsMode
    {
        get => SelectedGame is null ? null : AllGraphicsModes.FirstOrDefault(m => m.Key == (SelectedGame.Entry.GraphicsMode ?? "standard"));
        set { if (value is not null) _ = SavePrimeOptionsAsync(value.Key, null, null, null); }
    }
    public WindowChoice? SelectedFpsLimit
    {
        get => SelectedGame is null ? null : AllFpsLimits.FirstOrDefault(m => m.Key == (SelectedGame.Entry.FpsLimit ?? 0).ToString());
        set { if (value is not null) _ = SavePrimeOptionsAsync(null, null, int.Parse(value.Key), null); }
    }
    public bool SelectedMetalFx { get => SelectedGame?.Entry.MetalFxUpscale == true; set => _ = SavePrimeOptionsAsync(null, value, null, null); }
    public bool SelectedShowFps { get => SelectedGame?.Entry.ShowFps == true; set => _ = SavePrimeOptionsAsync(null, null, null, value); }
    public bool SelectedMetalMode => SelectedGame?.Entry.GraphicsMode == "metal";
    public bool SelectedUltra
    {
        get => Edition.IsPrime && SelectedGame?.Entry.Ultra == true;
        set => _ = SaveUltraAsync(value);
    }
    /// <summary>Raised when a game starts in ULTRA: the window goes to the Dock and stops drawing.</summary>
    public event EventHandler? YieldToGameRequested;

    private async Task SaveUltraAsync(bool ultra)
    {
        if (SelectedGame is null || !Edition.IsPrime) return;
        try
        {
            if (ultra && UltraMode.UsesMetal && !PrimeGraphics.IsInstalled)
            {
                Status = "Устанавливаю ULTRA: Wine на основе CrossOver и Direct3D→Metal (один раз, около 260 МБ)…";
                await PrimeGraphics.InstallAsync(new Progress<WineProgress>(p => Status = p.Stage + (p.Fraction < 1 ? $" {p.Fraction * 100:0}%" : "")));
                Status = "ULTRA установлен.";
            }
            await ReplaceEntryAsync(await _services.SetUltraAsync(SelectedGame.Entry.Id, ultra));
        }
        catch (Exception error) { ReportError(error); }
        foreach (string property in new[] { nameof(SelectedUltra), nameof(PerformanceNote) }) Notify(property);
    }
    public string PerformanceNote => !Edition.IsPrime
        ? "ULTRA — больше кадров без понижения графики, счётчик и ограничение FPS — в Prime."
        : SelectedUltra ? (UltraMode.UsesMetal
            ? "ULTRA: Direct3D идёт прямо в Metal, без ограничителя кадров, шейдеры кэшируются навсегда; разрешение и качество — как в игре. Лаунчер уходит в Dock."
            : "ULTRA: самый быстрый путь графики на Intel (DXVK), без ограничителя кадров, постоянный кэш шейдеров на всех ядрах; разрешение и качество — как в игре. Лаунчер уходит в Dock.")
        : "Включите ULTRA, чтобы отдать игре всё: без ограничителя кадров, с кэшем шейдеров и самым коротким путём до видеокарты.";
    public ICommand PickCoverCommand => new RelayCommand(() => CoverPickRequested?.Invoke(this, EventArgs.Empty), () => Edition.IsPrime);
    public event EventHandler? CoverPickRequested;

    public async Task SetCustomCoverAsync(string path)
    {
        if (SelectedGame is null || !Edition.IsPrime) return;
        var id = SelectedGame.Entry.Id;
        string covers = Path.Combine(_services.DataDirectory, "Covers");
        Directory.CreateDirectory(covers);
        string copy = Path.Combine(covers, id.ToString("N") + "-custom" + Path.GetExtension(path));
        File.Copy(path, copy, overwrite: true);
        await _services.SetCustomCoverAsync(id, copy);
        _covers.Remove(id); _coverAttempts.Remove(id);
        await PerformAsync("Меняю обложку…", ct => ReloadLibraryAsync(ct, id));
    }

    private async Task SavePrimeOptionsAsync(string? graphics, bool? metalFx, int? fpsLimit, bool? showFps)
    {
        if (SelectedGame is null || !Edition.IsPrime) return;
        var e = SelectedGame.Entry;
        string mode = graphics ?? e.GraphicsMode ?? "standard";
        try
        {
            if (mode == "metal" && !PrimeGraphics.IsInstalled)
            {
                Status = "Устанавливаю ULTRA: Wine на основе CrossOver и Direct3D→Metal (один раз, около 260 МБ)…";
                await PrimeGraphics.InstallAsync(new Progress<WineProgress>(p => Status = p.Stage + (p.Fraction < 1 ? $" {p.Fraction * 100:0}%" : "")));
                Status = "ULTRA установлен.";
            }
            var updated = await _services.SetPrimeOptionsAsync(e.Id, mode, metalFx ?? e.MetalFxUpscale, fpsLimit ?? e.FpsLimit, showFps ?? e.ShowFps);
            await ReplaceEntryAsync(updated);
        }
        catch (Exception error) { ReportError(error); }
        foreach (string property in new[] { nameof(SelectedGraphicsMode), nameof(SelectedFpsLimit), nameof(SelectedMetalFx), nameof(SelectedShowFps), nameof(SelectedMetalMode), nameof(SelectedUltra), nameof(PerformanceNote) })
            Notify(property);
    }

    private async Task ReplaceEntryAsync(GameEntry updated)
    {
        _entries = await _services.LoadLibraryAsync();
        foreach (var game in Games.Where(g => g.Entry.Id == updated.Id).ToArray())
        {
            int index = Games.IndexOf(game);
            var replacement = new GameItemViewModel(updated) { Cover = game.Cover, IsSelected = game.IsSelected };
            Games[index] = replacement;
            int shelf = ShelfItems.IndexOf(game);
            if (shelf >= 0) ShelfItems[shelf] = replacement;
            if (ReferenceEquals(_selectedGame, game)) _selectedGame = replacement;
        }
    }

    public ICommand StopGameCommand => new AsyncCommand(StopSelectedAsync, () => SelectedGame?.CanLaunch == true);

    private async Task SaveWindowOptionsAsync(string mode, ResolutionChoice? resolution)
    {
        if (SelectedGame is null) return;
        var id = SelectedGame.Entry.Id;
        try
        {
            var updated = await _services.SetWindowOptionsAsync(id, mode, resolution?.Width, resolution?.Height);
            _entries = await _services.LoadLibraryAsync();
            foreach (var game in Games.Where(g => g.Entry.Id == id).ToArray())
            {
                int index = Games.IndexOf(game);
                var replacement = new GameItemViewModel(updated) { Cover = game.Cover, IsSelected = game.IsSelected };
                Games[index] = replacement;
                int shelf = ShelfItems.IndexOf(game);
                if (shelf >= 0) ShelfItems[shelf] = replacement;
                if (ReferenceEquals(_selectedGame, game)) _selectedGame = replacement;
            }
            foreach (string property in new[] { nameof(SelectedWindowMode), nameof(SelectedResolution), nameof(SelectedResolutionMatters) }) Notify(property);
        }
        catch (Exception error) { ReportError(error); }
    }

    private async Task StopSelectedAsync()
    {
        if (SelectedGame is null) return;
        var entry = SelectedGame.Entry;
        try
        {
            await GameLaunchOptions.StopAsync(entry.PreparedMacAppPath ?? entry.SourcePath, CancellationToken.None);
            if (entry.GraphicsMode == "metal") await PrimeGraphics.StopAsync(entry, CancellationToken.None);
            if (_launchingId == entry.Id) { _launchingId = null; NotifyLaunch(); }
            Status = "Игра закрыта.";
        }
        catch (Exception error) { ReportError(error); }
    }
    public string LibraryCount => _entries.Count + " " + Plural(_entries.Count, "игра", "игры", "игр");
    public string FilterCount => Games.Count == 0 ? (_entries.Count == 0 ? "Библиотека пока пуста" : "Ничего не найдено") : $"Показано: {Games.Count}";
    public string DataDirectory => _services.DataDirectory;
    public string CacheDirectory => RuntimeCatalog.CacheDirectory;
    public string PlatformLabel => "macOS · " + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Apple Silicon" : "Intel / x64");
    public static string AppVersion => typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
    public string RailVersion => "DUSTORE LAUNCHER V " + AppVersion + " · " + Edition.Name;
    public string FreeQuotaLine => Edition.IsPrime ? "" : $"Осталось сегодня: {Math.Max(0, ExDailyQuota.FreePerDay - ExDailyQuota.UsedToday(DataDirectory))} из {ExDailyQuota.FreePerDay} переносов eX · 2 МБ/с · очередь 15 с";

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

    // ---- Edition and appearance (themes, accents and sections are Prime) ----
    public bool IsPrime => Edition.IsPrime;
    public bool IsFree => !Edition.IsPrimeBuild;
    /// <summary>A Prime build whose purchase is not confirmed on this Mac yet.</summary>
    public bool ShowPrimeActivation => Edition.IsPrimeBuild && !Edition.IsPrime;
    public string PrimeActivationText { get; private set; } = "Prime работает, если он куплен в вашем аккаунте Dustore. Войдите в магазин и нажмите «Активировать».";
    public ICommand ActivatePrimeCommand => new RelayCommand(() =>
    {
        PrimeActivationText = "Спрашиваю магазин Dustore о покупке…";
        Notify(nameof(PrimeActivationText));
        PrimeActivateRequested?.Invoke(this, EventArgs.Empty);
    }, () => Edition.IsPrimeBuild);
    /// <summary>The window runs the check in its store view and reports back.</summary>
    public event EventHandler? PrimeActivateRequested;

    public void PrimeActivationDone(PrimeLicense.Ownership answer)
    {
        PrimeLicense.Accept(answer);
        PrimeActivationText = PrimeLicense.Explain(answer);
        Status = PrimeActivationText;
        foreach (string property in new[] { nameof(PrimeActivationText), nameof(ShowPrimeActivation), nameof(IsPrime) }) Notify(property);
    }
    public string EditionLabel => Edition.IsPrime ? "Prime" : "Free";
    public IReadOnlyList<ThemeChoice> ThemeChoices => UiPreferences.Themes;
    public IReadOnlyList<AccentChoice> AccentChoices => UiPreferences.Accents;
    public ThemeChoice SelectedTheme
    {
        get => UiPreferences.Themes.FirstOrDefault(t => t.Key == _ui.Theme) ?? UiPreferences.Themes[0];
        set { if (value is not null && Edition.IsPrime) { _ui.Theme = value.Key; SaveAppearance(); } }
    }
    public AccentChoice SelectedAccent
    {
        get => UiPreferences.Accents.FirstOrDefault(a => a.Key == _ui.Accent) ?? UiPreferences.Accents[0];
        set { if (value is not null && Edition.IsPrime) { _ui.Accent = value.Key; SaveAppearance(); } }
    }
    public bool ShowExSection { get => !IsPrime || _ui.ShowEx; set { _ui.ShowEx = value; SaveAppearance(); } }
    public bool ShowStoreSection { get => !IsPrime || _ui.ShowStore; set { _ui.ShowStore = value; SaveAppearance(); } }
    public bool ShowHomeSection { get => !IsPrime || _ui.ShowHome; set { _ui.ShowHome = value; SaveAppearance(); } }
    public bool ShowJamsSection { get => !IsPrime || _ui.ShowJams; set { _ui.ShowJams = value; SaveAppearance(); } }
    public bool ShowAssetsSection { get => !IsPrime || _ui.ShowAssets; set { _ui.ShowAssets = value; SaveAppearance(); } }
    public bool ShowStoreGroup => ShowStoreSection || ShowHomeSection || ShowJamsSection || ShowAssetsSection;
    public bool ShowHeroCard { get => !IsPrime || _ui.ShowHero; set { _ui.ShowHero = value; SaveAppearance(); } }
    public bool CompactShelf { get => IsPrime && _ui.CompactShelf; set { _ui.CompactShelf = value; SaveAppearance(); } }
    public double TileSize => CompactShelf ? 132 : 172;
    public double TileArt => CompactShelf ? 92 : 120;
    public bool IntroAnimation { get => IsPrime && _ui.IntroAnimation; set { _ui.IntroAnimation = value; SaveAppearance(); } }
    public bool HasSelectionAndHero => HasSelection && ShowHeroCard;

    /// <summary>Free converts at most 4 MB/s of game data; Prime finishes as fast as the Mac allows.</summary>
    private async Task HoldFreeExPaceAsync(string input, System.Diagnostics.Stopwatch started, CancellationToken ct)
    {
        if (Edition.IsPrime) return;
        long bytes = 0;
        try
        {
            bytes = File.Exists(input) ? new FileInfo(input).Length
                : Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception) { return; }
        var target = TimeSpan.FromSeconds(bytes / (double)Edition.FreeExBytesPerSecond);
        while (started.Elapsed < target)
        {
            double done = started.Elapsed.TotalSeconds / target.TotalSeconds;
            Status = $"Free: eX переносит не быстрее 2 МБ/с — {bytes * done / 1048576:0} из {bytes / 1048576.0:0} МБ ({done * 100:0}%). В Prime без ограничений.";
            await Task.Delay(400, ct);
        }
    }

    public void LoadAppearance()
    {
        _ui = UiPreferences.Load(_services.DataDirectory);
        _ui.Apply();
        NotifyAppearance();
    }

    public static bool PrimeIntroWanted(string dataDirectory) => Edition.IsPrime && UiPreferences.Load(dataDirectory).IntroAnimation;

    private void SaveAppearance()
    {
        if (!Edition.IsPrime) return;
        _ui.Save(_services.DataDirectory);
        _ui.Apply();
        NotifyAppearance();
    }

    private void NotifyAppearance()
    {
        foreach (string property in new[] { nameof(SelectedTheme), nameof(SelectedAccent), nameof(ShowExSection), nameof(ShowStoreSection), nameof(ShowHomeSection),
            nameof(ShowJamsSection), nameof(ShowAssetsSection), nameof(ShowStoreGroup), nameof(ShowHeroCard), nameof(CompactShelf), nameof(TileSize), nameof(TileArt),
            nameof(IntroAnimation), nameof(HasSelectionAndHero) })
            Notify(property);
    }

    // Wine for Windows games packaged by eX: installed by the launcher itself.
    public bool WineInstalled => WineRuntime.IsInstalled;
    public bool WineInstalling => _wineInstall is { IsCompleted: false };
    public bool WineCanInstall => !WineInstalled && !WineInstalling;
    public bool WineCanRemove => WineInstalled && !WineInstalling;
    public double WineProgress { get => _wineProgress; private set => Set(ref _wineProgress, value); }
    public string WineStatus => _wineStatus.Length > 0 ? _wineStatus
        : WineInstalled ? WineRuntime.DisplayVersion + " и DXVK установлены. Перенесённые Windows-игры запускаются через них."
        : "Не установлен. Лаунчер поставит его сам при первом запуске Windows-игры (" + WineRuntime.ArchiveBytes / 1048576 + " МБ).";
    public string PlanWineNote => _plan?.Method != "wine" ? ""
        : WineInstalled ? "Wine уже установлен лаунчером — игра запустится сразу после переноса."
        : "Wine лаунчер поставит сам после создания пакета (" + WineRuntime.ArchiveBytes / 1048576 + " МБ, один раз).";
    public bool HasPlanWineNote => PlanWineNote.Length > 0;
    public ICommand InstallWineCommand => new RelayCommand(() => _ = EnsureWineAsync());
    public ICommand RemoveWineCommand => new AsyncCommand(() => PerformAsync("Удаляю Wine…", async ct =>
    {
        await WineRuntime.RemoveAsync(ct);
        _wineStatus = "";
        NotifyWine();
        Status = "Wine удалён. Игры и их сохранения не тронуты.";
    }), () => !IsBusy);

    /// <summary>Installs Wine once; concurrent callers share the same installation.</summary>
    public Task<bool> EnsureWineAsync()
    {
        if (WineRuntime.IsInstalled) return Task.FromResult(true);
        if (_wineInstall is { IsCompleted: false } running) return running;
        _wineInstall = InstallWineAsync();
        NotifyWine();
        return _wineInstall;
    }

    private async Task<bool> InstallWineAsync()
    {
        var progress = new Progress<WineProgress>(p =>
        {
            WineProgress = p.Fraction * 100;
            _wineStatus = p.TotalBytes > 0 && p.Fraction < 1
                ? $"{p.Stage} {p.ReceivedBytes / 1048576} из {p.TotalBytes / 1048576} МБ · {p.Fraction * 100:0}%" : p.Stage;
            Notify(nameof(WineStatus));
        });
        try
        {
            await Task.Yield();
            await WineRuntime.InstallAsync(progress);
            _wineStatus = (await WineRuntime.InstalledVersionTextAsync()) + " и DXVK установлены. Перенесённые Windows-игры запускаются через них.";
            return true;
        }
        catch (Exception error)
        {
            _wineStatus = "Wine не установлен: " + error.Message;
            Error = _wineStatus;
            return false;
        }
        finally { WineProgress = 0; NotifyWine(); }
    }

    private void NotifyWine()
    {
        foreach (string property in new[] { nameof(WineInstalled), nameof(WineInstalling), nameof(WineCanInstall), nameof(WineCanRemove), nameof(WineStatus), nameof(PlanWineNote), nameof(HasPlanWineNote) })
            Notify(property);
    }

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
        LoadAppearance();
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
        if (!Edition.IsPrime) await TrustedClock.SyncAsync(DataDirectory);
        if (ExDailyQuota.Refusal(DataDirectory) is { } quota) { Status = quota; return; }
        await PerformAsync("Создаю пакет…", async ct =>
        {
            for (int left = Edition.FreeQueueSeconds; !Edition.IsPrime && left > 0; left--)
            {
                Status = $"Очередь Free: перенос начнётся через {left} с. В Prime — сразу и без очереди.";
                await Task.Delay(1000, ct);
            }
            var request = new ConversionRequest(SourcePath, SelectedTarget.Platform, GameName.Trim(), OutputPath,
                string.IsNullOrWhiteSpace(RuntimeVersion) ? null : RuntimeVersion.Trim(), EffectiveArchitecture,
                string.IsNullOrWhiteSpace(RuntimePath) ? null : RuntimePath.Trim(), _plan.Method);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = await _services.ConvertAsync(request, new Progress<string>(AppendLog), ct);
            await HoldFreeExPaceAsync(request.InputPath, started, ct);
            ExDailyQuota.RecordSuccess(DataDirectory); Notify(nameof(FreeQuotaLine));
            ResultPath = result.OutputPath;
            foreach (string warning in result.Warnings) AppendLog(warning);
            if (request.Target == TargetPlatform.MacOS)
            {
                var original = _entries.FirstOrDefault(g => string.Equals(g.SourcePath, request.InputPath, StringComparison.Ordinal));
                original ??= await _services.AddGameAsync(request.InputPath, CancellationToken.None);
                var ready = await _services.PrepareConvertedMacAsync(original.Id, result, CancellationToken.None);
                await ReloadLibraryAsync(CancellationToken.None, ready.Id);
                Status = "macOS-пакет готов. Игра добавлена в библиотеку для запуска." + ExDailyQuota.Remaining(DataDirectory);
                if (_plan.Method == "wine" && !WineRuntime.IsInstalled) _ = EnsureWineAsync();
            }
            else Status = "Windows-пакет готов. Откройте ZIP на Windows для проверки запуска." + ExDailyQuota.Remaining(DataDirectory);
        });
    }

    private async Task LaunchSelectedAsync()
    {
        if (SelectedGame is null) return;
        if (WineRuntime.IsWineWrapper(SelectedGame.Entry.PreparedMacAppPath) && !WineRuntime.IsInstalled)
        {
            Status = "Этой игре нужен Wine. Устанавливаю его — один раз…";
            var entry = SelectedGame.Entry;
            if (!await EnsureWineAsync()) return;
            SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == entry.Id) ?? SelectedGame;
        }
        await LaunchNowAsync();
    }

    private async Task LaunchNowAsync()
    {
        if (SelectedGame is null) return;
        var entry = SelectedGame.Entry;
        bool launched = false;
        await PerformAsync("Запускаю игру…", async ct =>
        {
            await _services.LaunchAsync(entry, ct);
            if (Edition.IsPrime && entry.Ultra) YieldToGameRequested?.Invoke(this, EventArgs.Empty);
            launched = true;
            await ReloadLibraryAsync(ct, entry.Id);
            Status = "Игра передана macOS для запуска.";
        });
        if (!launched) return;
        // macOS only brings an already running app forward on a repeated open, so this is feedback, not a lock.
        _launchingId = entry.Id;
        _launchIsWine = WineRuntime.IsWineWrapper(entry.PreparedMacAppPath);
        NotifyLaunch();
        await Task.Delay(TimeSpan.FromSeconds(_launchIsWine ? 45 : 6));
        if (_launchingId == entry.Id) _launchingId = null;
        NotifyLaunch();
    }

    private void NotifyLaunch()
    {
        foreach (string property in new[] { nameof(SelectedLaunching), nameof(PlayLabel), nameof(LaunchNote), nameof(HasLaunchNote), nameof(CanLaunch) })
            Notify(property);
        if (LaunchCommand is ICommandNotifications notifications) notifications.RaiseCanExecuteChanged();
    }

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
            try
            {
                await using var stream = File.OpenRead(path);
                _covers[entry.Id] = await Task.Run(() => Bitmap.DecodeToWidth(stream, 400, BitmapInterpolationMode.MediumQuality));
            }
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
        foreach (string property in new[] { nameof(HasSelection), nameof(HasSelectionAndHero), nameof(NoSelection), nameof(SelectedTitle), nameof(SelectedSource), nameof(SelectedStatus),
            nameof(SelectedAdded), nameof(SelectedLastPlayed), nameof(LaunchHint), nameof(SelectedCover), nameof(SelectedHasCover), nameof(SelectedNoCover),
            nameof(SelectedMonogram), nameof(SelectedKind), nameof(SelectedReady), nameof(SelectedNeedsEx), nameof(SelectedStateLabel),
            nameof(SelectedLaunching), nameof(PlayLabel), nameof(LaunchNote), nameof(HasLaunchNote),
            nameof(SelectedHasWindowOptions), nameof(SelectedCanStop), nameof(SelectedWindowMode), nameof(SelectedResolution), nameof(SelectedResolutionMatters),
            nameof(SelectedIsWineGame), nameof(SelectedShowsPerformance), nameof(SelectedGraphicsMode), nameof(SelectedFpsLimit), nameof(SelectedMetalFx),
            nameof(SelectedShowFps), nameof(SelectedMetalMode), nameof(SelectedUltra), nameof(PerformanceNote) })
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
            nameof(PlanChip), nameof(PlanReady), nameof(PlanBlocked), nameof(PlanPending), nameof(PlanWineNote), nameof(HasPlanWineNote) })
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
public sealed record WindowChoice(string Label, string Key);
public sealed record ResolutionChoice(int Width, int Height) { public string Label => Width + " × " + Height; }
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
    public string ShortStatus => CanLaunch ? "Готова" : !Entry.SourceExists ? "Не найдена" : "Нужен eX";
    public string KindChip => CanLaunch ? "macOS" : Entry.Kind;
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
