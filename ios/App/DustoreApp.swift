import SwiftUI

@main
struct DustoreApp: App {
    init() { SelfTest.startIfRequested() }
    var body: some Scene {
        WindowGroup { RootView() }
    }
}

/// CI only (launch argument -dustoreSelfTest): imports Documents/selftest.zip through eX, opens the
/// game and writes what happened to Documents/selftest.json; the workflow then takes a screenshot.
@MainActor
enum SelfTest {
    private static var report: [String: Any] = [:]
    private static var url: URL { FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("selftest.json") }

    nonisolated static func startIfRequested() {
        guard ProcessInfo.processInfo.arguments.contains("-dustoreSelfTest") else { return }
        Task { @MainActor in await run() }
    }

    private static func write() {
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) { try? data.write(to: url) }
    }

    private static func run() async {
        report["edition"] = Edition.name
        // Quota by server time: three, then refused; a server day later — open again.
        if !Edition.isPrime {
            ExQuota.clearForTests()
            TrustedClock.override = Date(timeIntervalSince1970: 1_791_108_000)
            await TrustedClock.sync()
            for _ in 0..<Edition.freePerDay { ExQuota.recordSuccess() }
            let refused = ExQuota.refusal != nil
            TrustedClock.override = Date(timeIntervalSince1970: 1_791_108_000 + 13 * 3600)
            await TrustedClock.sync()
            report["quotaFourthRefused"] = refused
            report["quotaNewServerDayResets"] = ExQuota.leftToday == Edition.freePerDay
            ExQuota.clearForTests()
            TrustedClock.override = nil
        }
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let zip = documents.appendingPathComponent("selftest.zip")
        report["inputExists"] = FileManager.default.fileExists(atPath: zip.path)
        write()
        let started = Date()
        guard let game = await Library.shared.add(from: zip, title: "PODIEZD") else {
            report["error"] = Library.shared.message ?? "import failed"
            write(); return
        }
        report["importSeconds"] = Date().timeIntervalSince(started)
        report["kind"] = game.kind.rawValue
        report["engine"] = game.engine ?? ""
        report["note"] = game.note ?? ""
        report["webFiles"] = (try? FileManager.default.contentsOfDirectory(atPath: game.webRoot.path))?.sorted() ?? []
        report["unityMessage"] = Porter.unityMessage
        write()
        Library.shared.message = nil
        if game.playable { Library.shared.playing = game }
    }

    nonisolated static func gameReported(_ text: String) {
        guard ProcessInfo.processInfo.arguments.contains("-dustoreSelfTest") else { return }
        Task { @MainActor in
            if text.hasPrefix("frame: ") {
                let parts = text.dropFirst(7).split(separator: " ", maxSplits: 1)
                var frames = report["frames"] as? [String] ?? []
                frames.append(String(parts.first ?? ""))
                report["frames"] = frames
                if parts.count == 2, let comma = parts[1].firstIndex(of: ","), let data = Data(base64Encoded: String(parts[1][parts[1].index(after: comma)...])) {
                    try? data.write(to: url.deletingLastPathComponent().appendingPathComponent("frame-\(frames.count).jpg"))
                }
            } else if text.hasPrefix("log: ") {
                var log = report["engineLog"] as? [String] ?? []
                log.append(String(text.dropFirst(5)))
                report["engineLog"] = log
            } else {
                report["game"] = text
            }
            write()
        }
    }
}
