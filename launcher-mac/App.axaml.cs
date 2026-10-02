using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Text.Json;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, args) => StartupDiagnostics.RecordFailure(args.Exception);
        StartupDiagnostics.RecordFrameworkInitialized();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow window;
            try { window = new MainWindow(); }
            catch (Exception error)
            {
                StartupDiagnostics.RecordFailure(error);
                if (Program.UiSmoke) throw;
                desktop.MainWindow = CreateStartupFailureWindow(error);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            desktop.MainWindow = window;
            if (Program.UiSmoke)
                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await window.InitializeAsync();
                        await Task.Delay(2000);
                        if (window.ViewModel.HasError || window.ViewModel.IsBusy)
                            throw new InvalidOperationException("The launcher did not finish loading: " + window.ViewModel.Error);
                        if (!window.IsVisible || window.ClientSize.Width < 800 || window.ClientSize.Height < 500)
                            throw new InvalidOperationException("The native launcher window did not open at a usable size.");
                        window.UpdateLayout();
                        double scale = Math.Clamp(window.RenderScaling, 1, 2);
                        var pixels = new PixelSize((int)Math.Ceiling(window.ClientSize.Width * scale),
                            (int)Math.Ceiling(window.ClientSize.Height * scale));
                        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        bitmap.Render(window);
                        string imagePath = Program.SmokeScreenshotPath;
                        bitmap.Save(imagePath);
                        window.ViewModel.Section = "ex";
                        if (Program.SmokeInputPath is not null)
                        {
                            window.ViewModel.SelectedTarget = window.ViewModel.Targets.Single(t => t.Platform == TargetPlatform.Windows);
                            await window.ViewModel.SetSourceAsync(Program.SmokeInputPath);
                            if (window.ViewModel.HasError || !window.ViewModel.CanConvert)
                                throw new InvalidOperationException("The eX Windows route did not become ready: " + window.ViewModel.Error);
                        }
                        window.UpdateLayout();
                        using var exBitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        exBitmap.Render(window);
                        string exImagePath = Path.ChangeExtension(Program.SmokeReportPath, ".ex.png");
                        exBitmap.Save(exImagePath);
                        window.ViewModel.Section = "settings";
                        window.UpdateLayout();
                        using var settingsBitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
                        settingsBitmap.Render(window);
                        string settingsImagePath = Path.ChangeExtension(Program.SmokeReportPath, ".settings.png");
                        settingsBitmap.Save(settingsImagePath);
                        var web = await VerifyEmbeddedStoreAsync(window);
                        Program.WriteReport(new
                        {
                            status = "Pass", product = "DUSTORE LAUNCHER V", mode = "native-desktop-ui-startup",
                            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                            windowOpened = window.IsVisible, clientWidth = window.ClientSize.Width,
                            clientHeight = window.ClientSize.Height, renderScaling = window.RenderScaling,
                            renderedControlsImage = imagePath, interactiveClicksTested = false,
                            exControlsImage = exImagePath, settingsControlsImage = settingsImagePath,
                            exAnalysisReady = Program.SmokeInputPath is not null && window.ViewModel.CanConvert,
                            viewModelLoaded = true, libraryEntryCount = window.ViewModel.Games.Count,
                            originalLogoUnchanged = Program.OriginalLogoUnchanged(),
                            embeddedStore = web,
                            verifiedAtUtc = DateTimeOffset.UtcNow
                        });
                        desktop.Shutdown(0);
                    }
                    catch (Exception error)
                    {
                        Program.WriteReport(new { status = "Fail", mode = "native-desktop-ui-startup", error = error.ToString() });
                        desktop.Shutdown(1);
                    }
                };
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Opens the store section and waits for dustore.ru inside the window's own WKWebView.
    private static async Task<object> VerifyEmbeddedStoreAsync(MainWindow window)
    {
        window.ViewModel.Section = "store";
        if (window.WebView is not { } web)
            throw new InvalidOperationException("The in-app store web view was not created on macOS.");
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(500);
            var state = web.State;
            if (web.IsCreated && state.Url.StartsWith("https://", StringComparison.Ordinal) && !state.IsLoading && state.Title.Length > 0) break;
        }
        if (!web.IsCreated)
            throw new InvalidOperationException("WKWebView was not created inside the launcher window.");
        var final = web.State;
        if (!Uri.TryCreate(final.Url, UriKind.Absolute, out var url) || !url.Host.EndsWith("dustore.ru", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The in-app store did not navigate to dustore.ru: '" + final.Url + "'.");
        // A desktop capture shows the native web view, which RenderTargetBitmap cannot draw.
        string capture = Path.ChangeExtension(Program.SmokeReportPath, ".store.png");
        try
        {
            using var screen = System.Diagnostics.Process.Start("/usr/sbin/screencapture", new[] { "-x", capture });
            screen?.WaitForExit(15000);
        }
        catch (Exception) { capture = ""; }
        return new
        {
            webViewCreated = true, url = final.Url, title = final.Title, finishedLoading = !final.IsLoading,
            secondsToLoad = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            screenCapture = File.Exists(capture) ? capture : null, insideLauncherWindow = true
        };
    }

    private static Window CreateStartupFailureWindow(Exception error)
    {
        var window = new Window
        {
            Title = "Ошибка запуска DUSTORE LAUNCHER V", Width = 640, Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = "Не удалось загрузить лаунчер. Подробности сохранены в журнале.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 18
        });
        panel.Children.Add(new TextBox
        {
            Text = error.Message + "\n\nЖурнал: " + StartupDiagnostics.LogPath,
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 180
        });
        var close = new Button { Content = "Закрыть", Padding = new Thickness(16, 8) };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = panel;
        return window;
    }
}
