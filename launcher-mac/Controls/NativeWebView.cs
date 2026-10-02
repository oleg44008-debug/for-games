using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DustoreLauncherV.Mac.Services;

namespace DustoreLauncherV.Mac.Controls;

/// <summary>
/// Hosts the system WKWebView inside the launcher window. One web view lives for the
/// whole session so sign-in, history and scroll position survive section switches.
/// </summary>
public sealed class NativeWebView : NativeControlHost
{
    private static IntPtr _webView;
    private readonly DispatcherTimer _poll;
    private string? _pending;
    private WebPageState _state;

    public NativeWebView()
    {
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => Poll());
    }

    public static bool IsSupported => OperatingSystem.IsMacOS();
    public bool IsCreated => _webView != IntPtr.Zero;
    public WebPageState State => _state;
    public event EventHandler? StateChanged;
    public event EventHandler<DownloadSnapshot>? DownloadChanged;
    public event EventHandler<string>? FallbackDownload;
    private DownloadSnapshot? _download;

    public void Navigate(string url)
    {
        if (_webView == IntPtr.Zero) { _pending = url; return; }
        WebKitBridge.Navigate(_webView, url);
        Poll();
    }

    public void GoBack() { if (_webView != IntPtr.Zero) WebKitBridge.GoBack(_webView); }
    public void GoForward() { if (_webView != IntPtr.Zero) WebKitBridge.GoForward(_webView); }
    public void Reload() { if (_webView != IntPtr.Zero) WebKitBridge.Reload(_webView); }
    public void CancelDownload() => WebKitBridge.CancelDownload();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (IsSupported)
        {
            if (_webView == IntPtr.Zero) _webView = WebKitBridge.CreateWebView();
            if (_webView != IntPtr.Zero)
            {
                if (_pending is { } url) { _pending = null; WebKitBridge.Navigate(_webView, url); }
                _poll.Start();
                return new PlatformHandle(_webView, "NSView");
            }
        }
        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _poll.Stop();
        // The WKWebView is kept for reuse; only foreign placeholder controls are destroyed.
        if (control.Handle != _webView) base.DestroyNativeControlCore(control);
    }

    private void Poll()
    {
        if (_webView == IntPtr.Zero) return;
        if (WebKitBridge.TakeFallbackDownloadUrl() is { } fallback) FallbackDownload?.Invoke(this, fallback);
        if (WebKitBridge.CurrentDownload is { } download && download != _download)
        {
            _download = download;
            DownloadChanged?.Invoke(this, download);
        }
        var next = WebKitBridge.ReadState(_webView);
        if (next == _state) return;
        _state = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
