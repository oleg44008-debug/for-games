using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Store downloads. A response WebKit cannot show (a game ZIP redirected from the store to S3)
/// becomes a WKDownload that the launcher saves into its own Downloads folder, instead of the
/// blank page WebKit leaves when such a navigation is simply dropped.
/// </summary>
internal static unsafe partial class WebKitBridge
{
    private const nint PolicyCancel = 0, PolicyAllow = 1, PolicyDownload = 2;
    /// <summary>Set while the launcher asks the store whether Prime is bought.</summary>
    public static bool PrimeProbeRunning;
    public static bool PrimeProbeOwned;
    private static readonly string[] GameExtensions = { ".zip", ".dmg", ".pck", ".love", ".exe", ".apk", ".7z", ".rar", ".gz", ".tgz", ".pkg" };

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long SendLong(IntPtr target, IntPtr selector);

    private static IntPtr _download;
    private static int _downloadId;
    private static string _downloadName = "", _downloadPath = "", _downloadError = "", _pendingTitle = "";
    private static string _pendingPage = "", _downloadPage = "";
    private static DownloadStatus _downloadStatus = DownloadStatus.None;

    /// <summary>Folder for finished store downloads; set by the launcher before any page loads.</summary>
    public static string? DownloadDirectory { get; set; }
    /// <summary>On macOS older than 11.3 (no WKDownload) the file URL is handed to the system browser.</summary>
    public static string? FallbackDownloadUrl { get; private set; }
    public static bool SupportsDownloads => OperatingSystem.IsMacOS() && Class("WKDownload") != IntPtr.Zero;

    public static DownloadSnapshot? CurrentDownload
    {
        get
        {
            if (_downloadStatus == DownloadStatus.None) return null;
            double fraction = 0; long received = 0, total = 0;
            if (_download != IntPtr.Zero)
            {
                IntPtr progress = Send(_download, Sel("progress"));
                if (progress != IntPtr.Zero)
                {
                    fraction = SendDouble(progress, Sel("fractionCompleted"));
                    received = SendLong(progress, Sel("completedUnitCount"));
                    total = SendLong(progress, Sel("totalUnitCount"));
                }
            }
            if (_downloadStatus == DownloadStatus.Finished) fraction = 1;
            return new DownloadSnapshot(_downloadId, _downloadName, _downloadPath, double.IsFinite(fraction) ? fraction : 0,
                received, total, _downloadStatus, _downloadError, _downloadPage);
        }
    }

    public static string? TakeFallbackDownloadUrl() { string? url = FallbackDownloadUrl; FallbackDownloadUrl = null; return url; }

    public static void CancelDownload()
    {
        if (_download != IntPtr.Zero && _downloadStatus == DownloadStatus.Running)
            SendVoid(_download, Sel("cancel:"), IntPtr.Zero);
    }

    private static void RegisterDownloadMethods(IntPtr cls)
    {
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> decide = &DecideResponsePolicy;
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> became = &BecameDownload;
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> destination = &DecideDestination;
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> finished = &DownloadFinished;
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> failed = &DownloadFailed;
        class_addMethod(cls, Sel("webView:decidePolicyForNavigationResponse:decisionHandler:"), (IntPtr)decide, "v@:@@@?");
        class_addMethod(cls, Sel("webView:navigationResponse:didBecomeDownload:"), (IntPtr)became, "v@:@@@");
        class_addMethod(cls, Sel("webView:navigationAction:didBecomeDownload:"), (IntPtr)became, "v@:@@@");
        class_addMethod(cls, Sel("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:"), (IntPtr)destination, "v@:@@@@?");
        class_addMethod(cls, Sel("downloadDidFinish:"), (IntPtr)finished, "v@:@");
        class_addMethod(cls, Sel("download:didFailWithError:resumeData:"), (IntPtr)failed, "v@:@@@");
        IntPtr protocol = objc_getProtocol("WKDownloadDelegate");
        if (protocol != IntPtr.Zero) class_addProtocol(cls, protocol);
    }

    // Objective-C blocks keep their function pointer after isa, flags and reserved fields.
    private static void InvokeBlockWithPolicy(IntPtr block, nint value) =>
        ((delegate* unmanaged<IntPtr, nint, void>)Marshal.ReadIntPtr(block, 16))(block, value);
    private static void InvokeBlock(IntPtr block, IntPtr value) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void>)Marshal.ReadIntPtr(block, 16))(block, value);

    [UnmanagedCallersOnly]
    private static void DecideResponsePolicy(IntPtr self, IntPtr selector, IntPtr webView, IntPtr navigationResponse, IntPtr decisionHandler)
    {
        nint policy = PolicyAllow;
        try
        {
            IntPtr response = Send(navigationResponse, Sel("response"));
            bool canShow = SendBool(navigationResponse, Sel("canShowMIMEType")) != 0;
            string fileName = ManagedString(Send(response, Sel("suggestedFilename"))) ?? "";
            string url = ManagedString(Send(Send(response, Sel("URL")), Sel("absoluteString"))) ?? "";
            string disposition = "";
            if (SendBool(response, Sel("isKindOfClass:"), Class("NSHTTPURLResponse")) != 0)
                disposition = ManagedString(Send(response, Sel("valueForHTTPHeaderField:"), NSString("Content-Disposition"))) ?? "";
            bool gameFile = GameExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (PrimeProbeRunning && PrimeLicense.IsPrimeFile(url))
            {
                PrimeProbeOwned = true;
                policy = PolicyCancel;
            }
            else if (!canShow || disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) || gameFile)
            {
                // The store page title names the game; the S3 file name is only a build hash.
                _pendingTitle = ManagedString(Send(webView, Sel("title"))) ?? "";
                // The page that offered the file decides whether it counts as a store download.
                _pendingPage = ManagedString(Send(Send(webView, Sel("URL")), Sel("absoluteString"))) ?? "";
                if (SupportsDownloads) policy = PolicyDownload;
                else { FallbackDownloadUrl = url; policy = PolicyCancel; }
            }
        }
        catch { policy = PolicyAllow; }
        InvokeBlockWithPolicy(decisionHandler, policy);
    }

    [UnmanagedCallersOnly]
    private static void BecameDownload(IntPtr self, IntPtr selector, IntPtr webView, IntPtr navigation, IntPtr download)
    {
        try
        {
            if (_download != IntPtr.Zero) Send(_download, Sel("release"));
            _download = Send(download, Sel("retain"));
            SendVoid(download, Sel("setDelegate:"), self);
            _downloadId++;
            _downloadStatus = DownloadStatus.Running;
            _downloadName = _downloadPath = _downloadError = "";
            _downloadPage = _pendingPage;
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void DecideDestination(IntPtr self, IntPtr selector, IntPtr download, IntPtr response, IntPtr suggestedFilename, IntPtr completion)
    {
        IntPtr destination = IntPtr.Zero;
        try
        {
            if (DownloadDirectory is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                string suggested = ManagedString(suggestedFilename) ?? "game.zip";
                _downloadName = GameNameFromTitle(_pendingTitle) ?? Path.GetFileNameWithoutExtension(suggested);
                string extension = Path.GetExtension(suggested);
                string path = UniquePath(directory, SafeFileName(_downloadName) + extension);
                _downloadPath = path;
                destination = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
            }
            else
            {
                _downloadStatus = DownloadStatus.Failed;
                _downloadError = "Папка загрузок лаунчера недоступна.";
            }
        }
        catch (Exception error) { _downloadStatus = DownloadStatus.Failed; _downloadError = error.Message; destination = IntPtr.Zero; }
        // A nil destination cancels the download instead of leaving WebKit waiting.
        InvokeBlock(completion, destination);
    }

    [UnmanagedCallersOnly]
    private static void DownloadFinished(IntPtr self, IntPtr selector, IntPtr download) => _downloadStatus = DownloadStatus.Finished;

    [UnmanagedCallersOnly]
    private static void DownloadFailed(IntPtr self, IntPtr selector, IntPtr download, IntPtr error, IntPtr resumeData)
    {
        try
        {
            nint code = SendNint(error, Sel("code"));
            _downloadStatus = code == -999 ? DownloadStatus.Cancelled : DownloadStatus.Failed;
            _downloadError = ManagedString(Send(error, Sel("localizedDescription"))) ?? "";
        }
        catch { _downloadStatus = DownloadStatus.Failed; }
    }

    internal static string? GameNameFromTitle(string title)
    {
        // Store pages are titled "Dustore — <game>".
        string name = title.Trim();
        int dash = name.IndexOf(" — ", StringComparison.Ordinal);
        if (name.StartsWith("Dustore", StringComparison.OrdinalIgnoreCase) && dash >= 0) name = name[(dash + 3)..].Trim();
        return name.Length == 0 || name.Equals("Dustore", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Каталог", StringComparison.Ordinal) ? null : name;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', ':' }).ToHashSet();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        return safe.Length == 0 ? "game" : safe.Length > 120 ? safe[..120] : safe;
    }

    private static string UniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        for (int index = 2; File.Exists(path) || Directory.Exists(path); index++)
            path = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + " (" + index + ")" + Path.GetExtension(fileName));
        return path;
    }
}

public enum DownloadStatus { None, Running, Finished, Failed, Cancelled }

public sealed record DownloadSnapshot(int Id, string Name, string Path, double Fraction, long ReceivedBytes, long TotalBytes, DownloadStatus Status, string Error, string SourcePage);
