using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DustoreLauncherV.Mac.Controls;
using DustoreLauncherV.Mac.ViewModels;

namespace DustoreLauncherV.Mac;

public partial class MainWindow : Window
{
    private Task? _initializeTask;
    private bool _closingPrompt;

    private readonly NativeWebView? _web;
    private string? _webSectionShown;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        if (OperatingSystem.IsMacOS())
        {
            // Content runs under the title bar; the rail leaves room for the window buttons.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = 34;
            // Sidebar material like Finder: blurred desktop behind a translucent tint when macOS grants it.
            TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None };
            Opened += (_, _) =>
            {
                if (ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur && ActualTransparencyLevel != WindowTransparencyLevel.Blur) return;
                Background = Avalonia.Media.Brushes.Transparent;
                if (this.FindControl<Border>("Sidebar") is { } sidebar && Application.Current?.FindResource("NavTranslucent") is Avalonia.Media.IBrush tint)
                    sidebar.Background = tint;
            };
        }
        if (NativeWebView.IsSupported && this.FindControl<Panel>("SiteHost") is { } siteHost)
        {
            // WKWebView exists only on macOS; other systems never create a native host.
            if (!string.IsNullOrWhiteSpace(ViewModel.DataDirectory))
                Services.WebKitBridge.DownloadDirectory = System.IO.Path.Combine(ViewModel.DataDirectory, "Downloads");
            _web = new NativeWebView();
            _web.StateChanged += (_, _) => PushWebState();
            _web.DownloadChanged += (_, download) => ViewModel.UpdateDownload(download);
            _web.FallbackDownload += (_, url) => ViewModel.OpenDownloadInBrowser(url);
            siteHost.Children.Add(_web);
        }
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Section)) ShowWebSection(); };
        if (Program.StartSection is "ex" or "settings" || MainViewModel.WebStartUrlFor(Program.StartSection ?? "") is not null)
        {
            ViewModel.Section = Program.StartSection!;
            ShowWebSection();
        }
        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
    }

    public MainViewModel ViewModel { get; }
    public NativeWebView? WebView => _web;

    private void ShowWebSection()
    {
        if (_web is null || !ViewModel.IsWeb) return;
        // Each site section opens its start page once; returning keeps the page the user left.
        if (_webSectionShown == ViewModel.Section) return;
        _webSectionShown = ViewModel.Section;
        _web.Navigate(ViewModel.WebStartUrl);
    }

    private void PushWebState()
    {
        if (_web is null) return;
        var state = _web.State;
        ViewModel.UpdateWebState(state.Url, state.Title, state.IsLoading, state.Progress, state.CanGoBack, state.CanGoForward, state.Error);
    }

    private void WebBack_Click(object? sender, RoutedEventArgs e) => _web?.GoBack();
    private void WebForward_Click(object? sender, RoutedEventArgs e) => _web?.GoForward();
    private void WebReload_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.HasWebError) WebRetry_Click(sender, e);
        else _web?.Reload();
    }

    private void WebRetry_Click(object? sender, RoutedEventArgs e)
    {
        string url = ViewModel.WebRetryUrl;
        ViewModel.ClearWebError();
        _web?.Navigate(url);
    }
    private void DismissError_Click(object? sender, RoutedEventArgs e) => ViewModel.DismissError();
    private void CancelDownload_Click(object? sender, RoutedEventArgs e) => _web?.CancelDownload();

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source.FindAncestorOfType<TextBox>(includeSelf: true) is null
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Tile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: GameItemViewModel game })
        {
            ViewModel.SelectedGame = game;
            if (ViewModel.LaunchCommand.CanExecute(null)) ViewModel.LaunchCommand.Execute(null);
        }
    }

    public Task InitializeAsync() => _initializeTask ??= ViewModel.InitializeAsync();

    private async void OnOpened(object? sender, EventArgs args)
    {
        try
        {
            await InitializeAsync();
            if (IsVisible) StartupDiagnostics.RecordReady(this);
        }
        catch (Exception error)
        {
            ViewModel.ReportError(error);
            StartupDiagnostics.RecordFailure(error);
        }
    }

    private async Task<string?> PickFileAsync(string title, bool gamesOnly = true)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanOpen) return null;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = gamesOnly ? new[]
                {
                    new FilePickerFileType("Игры и игровые данные") { Patterns = new[] { "*.exe", "*.zip", "*.pck", "*.love", "*.app" } },
                    FilePickerFileTypes.All
                } : new[] { FilePickerFileTypes.All }
            });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception error) { ViewModel.ReportError(error); return null; }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanPickFolder) return null;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception error) { ViewModel.ReportError(error); return null; }
    }

    private async void AddGameFile_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Добавить игру в библиотеку") is { } path) await ViewModel.ImportGameAsync(path);
    }

    private async void AddGameFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Добавить Mac-приложение .app или папку игры") is { } path) await ViewModel.ImportGameAsync(path);
    }

    private async void SelectSourceFile_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Выбрать исходную сборку для eX") is { } path) await ViewModel.SetSourceAsync(path);
    }

    private async void SelectSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Выбрать .app или папку исходной игры") is { } path) await ViewModel.SetSourceAsync(path);
    }

    private async void SelectRuntime_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFileAsync("Выбрать официальный пакет движка", false) is { } path) ViewModel.RuntimePath = path;
    }

    private async void SelectOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || !StorageProvider.CanSave) return;
        try
        {
            var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Создать новый ZIP игры",
                SuggestedFileName = string.IsNullOrWhiteSpace(ViewModel.OutputPath) ? "game-converted.zip" : Path.GetFileName(ViewModel.OutputPath),
                DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
            });
            if (result?.TryGetLocalPath() is { } path) ViewModel.OutputPath = path;
        }
        catch (Exception error) { ViewModel.ReportError(error); }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.F && ViewModel.IsLibrary && ViewModel.HasGames)
        {
            this.FindControl<TextBox>("SearchBox")?.Focus(); e.Handled = true;
        }
        else if (e.Key == Key.Enter && this.FindControl<ItemsControl>("Shelf")?.IsKeyboardFocusWithin == true && ViewModel.LaunchCommand.CanExecute(null))
        {
            ViewModel.LaunchCommand.Execute(null); e.Handled = true;
        }
        else if (e.Key == Key.Escape && ViewModel.IsBusy) { ViewModel.CancelOperation(); e.Handled = true; }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ViewModel.IsBusy) return;
        e.Cancel = true;
        if (_closingPrompt) return;
        _closingPrompt = true;
        try
        {
            var dialog = new Window
            {
                Title = "Операция выполняется", Width = 460, Height = 210, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brush.Parse("#1B0C1A")
            };
            var content = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
            content.Children.Add(new TextBlock
            {
                Text = "eX ещё работает. Можно запросить отмену и дождаться завершения текущего шага. Исходные файлы сохраняются.",
                Foreground = Avalonia.Media.Brush.Parse("#FFF4F1"), TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            var stay = new Button { Content = "Продолжить", Padding = new Thickness(15, 9) };
            var cancel = new Button { Content = "Отменить операцию", Padding = new Thickness(15, 9) };
            stay.Click += (_, _) => dialog.Close();
            cancel.Click += (_, _) => { ViewModel.CancelOperation(); dialog.Close(); };
            actions.Children.Add(stay); actions.Children.Add(cancel);
            content.Children.Add(actions); dialog.Content = content;
            await dialog.ShowDialog(this);
        }
        finally { _closingPrompt = false; }
    }
}
