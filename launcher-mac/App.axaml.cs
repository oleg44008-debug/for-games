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

        // A load that cannot connect must be explained on screen, not left as an empty page.
        // Port 59999 is unused and, unlike ports such as 9, not on WebKit's restricted list.
        web.Navigate("https://127.0.0.1:59999/");
        var failureStarted = DateTime.UtcNow;
        while (!window.ViewModel.HasWebError && DateTime.UtcNow - failureStarted < TimeSpan.FromSeconds(20))
            await Task.Delay(250);
        if (!window.ViewModel.HasWebError || window.ViewModel.WebViewVisible)
            throw new InvalidOperationException("A failed page load did not show the launcher's error screen. "
                + $"started={Services.WebKitBridge.StartedCount} failed={Services.WebKitBridge.FailedCount} raw='{Services.WebKitBridge.LastRawFailure}' "
                + $"url='{web.State.Url}' loading={web.State.IsLoading} bridgeError={(Services.WebKitBridge.LastError is null ? "none" : "set")}");
        var failure = window.ViewModel.WebError!;
        window.ViewModel.ClearWebError();
        object? download = Program.SmokeInputPath is { } game && game.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? await VerifyStoreDownloadAsync(window, web, game) : null;
        return new
        {
            storeDownload = download,
            failedLoadShowsError = true, failedLoadCode = failure.Code, failedLoadDomain = failure.Domain,
            navigationCallbacks = new { started = Services.WebKitBridge.StartedCount, failed = Services.WebKitBridge.FailedCount },
            webViewCreated = true, url = final.Url, title = final.Title, finishedLoading = !final.IsLoading,
            secondsToLoad = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            screenCapture = File.Exists(capture) ? capture : null, insideLauncherWindow = true
        };
    }

    // Mirrors the store: a game page whose download link answers 302 to an S3-style
    // application/zip without Content-Disposition. The game must land in the library.
    private static async Task<object> VerifyStoreDownloadAsync(MainWindow window, Controls.NativeWebView web, string gameZip)
    {
        int port = System.Net.Sockets.TcpListener.Create(0) is var probe ? StartAndStop(probe) : 0;
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                try
                {
                    string path = context.Request.Url?.AbsolutePath ?? "/";
                    if (path == "/g/1")
                    {
                        byte[] html = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>Dustore — PODIEZD</title>"
                            + "<meta http-equiv=\"refresh\" content=\"1;url=/download_game.php?game_id=1\"></head><body>Скачать</body></html>");
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(html);
                    }
                    else if (path == "/download_game.php")
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = "/builds/game-1/build-8df380fa84ff.zip";
                    }
                    else if (path.EndsWith(".zip", StringComparison.Ordinal))
                    {
                        context.Response.ContentType = "application/zip";
                        await using var file = File.OpenRead(gameZip);
                        context.Response.ContentLength64 = file.Length;
                        await file.CopyToAsync(context.Response.OutputStream);
                    }
                    else context.Response.StatusCode = 404;
                }
                catch { }
                finally { try { context.Response.Close(); } catch { } }
            }
        });

        int before = window.ViewModel.Games.Count;
        web.Navigate($"http://127.0.0.1:{port}/g/1");
        var started = DateTime.UtcNow;
        while (window.ViewModel.DownloadedGameId is null && DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
        {
            await Task.Delay(500);
            if (window.ViewModel.LastDownload is { Status: Services.DownloadStatus.Failed or Services.DownloadStatus.Cancelled } broken)
                throw new InvalidOperationException("The store download failed: " + broken.Error);
            if (window.ViewModel.HasWebError)
                throw new InvalidOperationException("The store download page failed: " + window.ViewModel.WebErrorMessage);
        }
        listener.Stop();
        var downloaded = window.ViewModel.LastDownload;
        if (window.ViewModel.DownloadedGameId is not { } id || downloaded is null)
            throw new InvalidOperationException($"The store download did not reach the library (state {downloaded?.Status}, page '{web.State.Url}').");
        var entry = window.ViewModel.Games.FirstOrDefault(g => g.Entry.Id == id)?.Entry;
        if (entry is null || !entry.CanLaunchOnMac)
            throw new InvalidOperationException("The downloaded game was not prepared for launch on Mac.");
        if (web.State.Url.EndsWith(".zip", StringComparison.Ordinal))
            throw new InvalidOperationException("The view navigated to the file instead of keeping the store page.");
        return new
        {
            addedToLibrary = true, readyToLaunch = true, libraryBefore = before, libraryAfter = window.ViewModel.Games.Count,
            name = downloaded.Name, file = Path.GetFileName(downloaded.Path), bytes = new FileInfo(downloaded.Path).Length,
            pageAfterDownload = web.State.Url,
            downloadQuarantined = Services.MacQuarantine.Read(downloaded.Path) is not null,
            preparedAppQuarantined = entry.PreparedMacAppPath is { } app && Services.MacQuarantine.Read(app) is not null
        };
    }

    private static int StartAndStop(System.Net.Sockets.TcpListener probe)
    {
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
