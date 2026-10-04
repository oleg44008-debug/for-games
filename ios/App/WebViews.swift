import SwiftUI
import WebKit

/// The Dustore store inside the app. «Скачать» saves the build and hands it to eX.
struct StoreWebView: UIViewRepresentable {
    let library: Library

    func makeCoordinator() -> Coordinator { Coordinator(library: library) }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        view.allowsBackForwardNavigationGestures = true
        view.isOpaque = false
        view.backgroundColor = UIColor(Theme.background)
        view.customUserAgent = nil
        if let url = URL(string: "https://dustore.ru/") { view.load(URLRequest(url: url)) }
        return view
    }

    func updateUIView(_ view: WKWebView, context: Context) {}

    final class Coordinator: NSObject, WKNavigationDelegate, WKDownloadDelegate {
        let library: Library
        private var targets: [ObjectIdentifier: (URL, String?)] = [:]
        private weak var lastView: WKWebView?
        init(library: Library) { self.library = library }

        func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse, decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
            lastView = webView
            let http = response.response as? HTTPURLResponse
            let disposition = http?.value(forHTTPHeaderField: "Content-Disposition")?.lowercased() ?? ""
            let type = response.response.mimeType?.lowercased() ?? ""
            let download = disposition.contains("attachment") || !response.canShowMIMEType
                || ["application/zip", "application/octet-stream", "application/x-zip-compressed", "application/vnd.android.package-archive"].contains(type)
            decisionHandler(download ? .download : .allow)
        }

        func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
            download.delegate = self
        }

        func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
            let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Downloads", isDirectory: true)
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent(UUID().uuidString + "-" + suggestedFilename)
            // The store page title names the game better than a generated file name does.
            var title = lastView?.title?.replacingOccurrences(of: "Dustore — ", with: "").replacingOccurrences(of: " — Dustore", with: "")
            if title?.isEmpty != false { title = (suggestedFilename as NSString).deletingPathExtension }
            targets[ObjectIdentifier(download)] = (file, title)
            Task { @MainActor in library.busy = "Скачиваю «\(title ?? suggestedFilename)»…" }
            completionHandler(file)
        }

        func downloadDidFinish(_ download: WKDownload) {
            guard let (file, title) = targets.removeValue(forKey: ObjectIdentifier(download)) else { return }
            Task { @MainActor in
                library.busy = nil
                await library.add(from: file, title: title)
                try? FileManager.default.removeItem(at: file)
            }
        }

        func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
            targets.removeValue(forKey: ObjectIdentifier(download))
            Task { @MainActor in library.busy = nil; library.message = "Загрузка не завершилась: " + error.localizedDescription }
        }
    }
}

/// A game, full screen. Its files come from the game folder through GameServer.
struct GameWebView: UIViewRepresentable {
    let game: Game
    var onReport: (String) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(onReport: onReport) }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.setURLSchemeHandler(GameServer(root: game.webRoot), forURLScheme: GameServer.scheme)
        configuration.allowsInlineMediaPlayback = true
        configuration.mediaTypesRequiringUserActionForPlayback = []
        configuration.userContentController.add(context.coordinator, name: "ex")
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.scrollView.isScrollEnabled = false
        view.scrollView.contentInsetAdjustmentBehavior = .never
        view.isOpaque = true
        view.backgroundColor = .black
        if #available(iOS 16.4, *) { view.isInspectable = true }
        let query = ProcessInfo.processInfo.arguments.contains("-dustoreSelfTest") ? "?selftest" : ""
        if let url = URL(string: "\(GameServer.scheme)://game/index.html" + query) { view.load(URLRequest(url: url)) }
        return view
    }

    func updateUIView(_ view: WKWebView, context: Context) {}

    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) {
        view.configuration.userContentController.removeScriptMessageHandler(forName: "ex")
        view.loadHTMLString("", baseURL: nil)
    }

    final class Coordinator: NSObject, WKScriptMessageHandler {
        let onReport: (String) -> Void
        init(onReport: @escaping (String) -> Void) { self.onReport = onReport }
        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            onReport(String(describing: message.body))
        }
    }
}
