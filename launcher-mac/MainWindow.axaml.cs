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
using DustoreLauncherV.Mac.ViewModels;

namespace DustoreLauncherV.Mac;

public partial class MainWindow : Window
{
    private Task? _initializeTask;
    private bool _closingPrompt;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
    }

    public MainViewModel ViewModel { get; }

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

    private void GamesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel.LaunchCommand.CanExecute(null)) ViewModel.LaunchCommand.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.FindControl<ListBox>("GamesList")?.IsKeyboardFocusWithin == true && ViewModel.LaunchCommand.CanExecute(null))
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
                Background = Avalonia.Media.Brush.Parse("#111D2E")
            };
            var content = new StackPanel { Margin = new Thickness(24), Spacing = 18 };
            content.Children.Add(new TextBlock
            {
                Text = "eX ещё работает. Можно запросить отмену и дождаться завершения текущего шага. Исходные файлы сохраняются.",
                Foreground = Avalonia.Media.Brush.Parse("#E7EFF9"), TextWrapping = Avalonia.Media.TextWrapping.Wrap
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
