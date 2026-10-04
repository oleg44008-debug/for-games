import Foundation
import UniformTypeIdentifiers
import WebKit

/// Serves a game's folder to its web view under dxgame://game/… — with the right MIME types
/// (WebAssembly needs application/wasm) and streamed in pieces, so a large pack never sits in memory.
final class GameServer: NSObject, WKURLSchemeHandler {
    static let scheme = "dxgame"
    private let root: URL
    private var stopped = Set<ObjectIdentifier>()
    private let lock = NSLock()
    private let queue = DispatchQueue(label: "dxgame.files", qos: .userInitiated)

    init(root: URL) { self.root = root.standardizedFileURL }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        let id = ObjectIdentifier(task)
        queue.async { [self] in
            let path = task.request.url?.path.removingPercentEncoding ?? "/"
            let file = root.appendingPathComponent(path == "/" ? "index.html" : String(path.dropFirst())).standardizedFileURL
            guard file.path.hasPrefix(root.path), let handle = try? FileHandle(forReadingFrom: file),
                  let length = try? handle.seekToEnd(), (try? handle.seek(toOffset: 0)) != nil else {
                finish(id) { task.didFailWithError(URLError(.fileDoesNotExist)) }
                return
            }
            defer { try? handle.close() }
            let headers = ["Content-Type": mime(file), "Content-Length": String(length), "Access-Control-Allow-Origin": "*",
                           "Cross-Origin-Opener-Policy": "same-origin", "Cross-Origin-Embedder-Policy": "require-corp", "Cache-Control": "no-cache"]
            let response = HTTPURLResponse(url: task.request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: headers)!
            guard deliver(id, { task.didReceive(response) }) else { return }
            while true {
                guard let chunk = try? handle.read(upToCount: 4 << 20), !chunk.isEmpty else { break }
                guard deliver(id, { task.didReceive(chunk) }) else { return }
            }
            finish(id) { task.didFinish() }
        }
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        lock.lock(); stopped.insert(ObjectIdentifier(task)); lock.unlock()
    }

    private func isStopped(_ id: ObjectIdentifier) -> Bool { lock.lock(); defer { lock.unlock() }; return stopped.contains(id) }

    /// WebKit wants these calls on the main thread and never after the task was stopped.
    private func deliver(_ id: ObjectIdentifier, _ call: @escaping () -> Void) -> Bool {
        var alive = true
        DispatchQueue.main.sync { if isStopped(id) { alive = false } else { call() } }
        return alive
    }

    private func finish(_ id: ObjectIdentifier, _ call: @escaping () -> Void) {
        _ = deliver(id, call)
        lock.lock(); stopped.remove(id); lock.unlock()
    }

    private func mime(_ file: URL) -> String {
        switch file.pathExtension.lowercased() {
        case "wasm": return "application/wasm"
        case "js", "mjs": return "text/javascript"
        case "html", "htm": return "text/html; charset=utf-8"
        case "pck", "data", "bin": return "application/octet-stream"
        default: return UTType(filenameExtension: file.pathExtension)?.preferredMIMEType ?? "application/octet-stream"
        }
    }
}
