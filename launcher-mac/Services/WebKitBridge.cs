using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Minimal Objective-C bridge to the system WKWebView. The store is shown with the
/// WebKit that ships with macOS, so the launcher does not bundle a browser engine.
/// Every call must happen on the AppKit main thread (Avalonia's UI thread).
/// </summary>
internal static unsafe class WebKitBridge
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string WebKitFramework = "/System/Library/Frameworks/WebKit.framework/WebKit";
    // WKWebView reports a bare WebKit user agent; sites expecting Safari get the familiar token.
    private const string UserAgentSuffix = "Version/18.0 Safari/605.1.15 DustoreLauncherV/5.2.2";

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, Width, Height; }

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
    [DllImport(ObjC)] private static extern IntPtr objc_getProtocol(string name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(IntPtr cls, IntPtr protocol);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, CGRect frame, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr target, IntPtr selector, byte argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr target, IntPtr selector);

    private static IntPtr _delegate;

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    private static IntPtr NSString(string value)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? ManagedString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        IntPtr utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>Creates a WKWebView NSView, or returns zero when WebKit is unavailable.</summary>
    public static IntPtr CreateWebView()
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        if (!NativeLibrary.TryLoad(WebKitFramework, out _)) return IntPtr.Zero;
        IntPtr webViewClass = Class("WKWebView");
        IntPtr configurationClass = Class("WKWebViewConfiguration");
        if (webViewClass == IntPtr.Zero || configurationClass == IntPtr.Zero) return IntPtr.Zero;

        IntPtr configuration = Send(Send(configurationClass, Sel("alloc")), Sel("init"));
        SendVoid(configuration, Sel("setApplicationNameForUserAgent:"), NSString(UserAgentSuffix));
        IntPtr webView = Send(Send(webViewClass, Sel("alloc")), Sel("initWithFrame:configuration:"),
            new CGRect { Width = 800, Height = 600 }, configuration);
        if (webView == IntPtr.Zero) return IntPtr.Zero;
        SendVoid(webView, Sel("setAllowsBackForwardNavigationGestures:"), (byte)1);
        // WKWebView ignores target=_blank links unless a UI delegate answers them.
        SendVoid(webView, Sel("setUIDelegate:"), UiDelegate());
        return webView;
    }

    public static void Navigate(IntPtr webView, string url)
    {
        IntPtr nsUrl = Send(Class("NSURL"), Sel("URLWithString:"), NSString(url));
        if (nsUrl == IntPtr.Zero) return;
        SendVoid(webView, Sel("loadRequest:"), Send(Class("NSURLRequest"), Sel("requestWithURL:"), nsUrl));
    }

    public static void GoBack(IntPtr webView) => Send(webView, Sel("goBack"));
    public static void GoForward(IntPtr webView) => Send(webView, Sel("goForward"));
    public static void Reload(IntPtr webView) => Send(webView, Sel("reload"));

    public static WebPageState ReadState(IntPtr webView) => new(
        ManagedString(Send(Send(webView, Sel("URL")), Sel("absoluteString"))) ?? "",
        ManagedString(Send(webView, Sel("title"))) ?? "",
        SendBool(webView, Sel("canGoBack")) != 0,
        SendBool(webView, Sel("canGoForward")) != 0,
        SendBool(webView, Sel("isLoading")) != 0,
        SendDouble(webView, Sel("estimatedProgress")));

    private static IntPtr UiDelegate()
    {
        if (_delegate != IntPtr.Zero) return _delegate;
        IntPtr cls = Class("DustoreWebUIDelegate");
        if (cls == IntPtr.Zero)
        {
            cls = objc_allocateClassPair(Class("NSObject"), "DustoreWebUIDelegate", 0);
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> create = &CreateWebViewForNewWindow;
            class_addMethod(cls, Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"), (IntPtr)create, "@@:@@@@");
            IntPtr protocol = objc_getProtocol("WKUIDelegate");
            if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
        }
        _delegate = Send(Send(cls, Sel("alloc")), Sel("init"));
        return _delegate;
    }

    // A link that asks for a new window opens in the same in-app view instead.
    [UnmanagedCallersOnly]
    private static IntPtr CreateWebViewForNewWindow(IntPtr self, IntPtr selector, IntPtr webView, IntPtr configuration, IntPtr navigationAction, IntPtr windowFeatures)
    {
        try
        {
            IntPtr request = Send(navigationAction, Sel("request"));
            if (request != IntPtr.Zero) SendVoid(webView, Sel("loadRequest:"), request);
        }
        catch { /* An exception must never cross back into Objective-C. */ }
        return IntPtr.Zero;
    }
}

public readonly record struct WebPageState(string Url, string Title, bool CanGoBack, bool CanGoForward, bool IsLoading, double Progress);
