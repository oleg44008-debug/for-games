import Foundation
import SwiftUI

/// The games on this iPhone, saved in Documents/library.json. Every eX transfer passes the Free
/// gate here: three a day by server time, a short queue and 2 MB/s. Prime has none of it.
@MainActor
final class Library: ObservableObject {
    static let shared = Library()

    @Published private(set) var games: [Game] = []
    @Published var busy: String?
    @Published var message: String?
    @Published var playing: Game?
    @Published var quotaLine = ""

    private var file: URL { FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("library.json") }

    private init() {
        if let data = try? Data(contentsOf: file), let saved = try? JSONDecoder().decode([Game].self, from: data) {
            games = saved.filter { !$0.playable || FileManager.default.fileExists(atPath: $0.webRoot.path) }
        }
        refreshQuota()
        Task { await TrustedClock.sync(); refreshQuota() }
        Task { await PrimeLicense.recheckIfDue() }
    }

    func refreshQuota() {
        quotaLine = Edition.isPrime ? "Prime: eX без лимита и очереди" : "Free: сегодня осталось \(ExQuota.leftToday) из \(Edition.freePerDay) переносов eX · 2 МБ/с · очередь \(Edition.freeQueueSeconds) с"
    }

    private func save() {
        if let data = try? JSONEncoder().encode(games) { try? data.write(to: file, options: .atomic) }
    }

    func remove(_ game: Game) {
        try? FileManager.default.removeItem(at: game.folder)
        games.removeAll { $0.id == game.id }
        save()
    }

    /// Imports a downloaded or picked file: unpacks, recognises the engine and, for Godot, moves it onto the web engine.
    @discardableResult
    func add(from source: URL, title: String?) async -> Game? {
        guard busy == nil else { message = "eX уже переносит другую игру — дождитесь конца."; return nil }
        if !Edition.isPrime {
            busy = "Сверяю время с сервером…"
            await TrustedClock.sync()
            if let refusal = ExQuota.refusal { busy = nil; message = refusal; refreshQuota(); return nil }
            for left in stride(from: Edition.freeQueueSeconds, to: 0, by: -1) {
                busy = "Очередь Free: перенос начнётся через \(left) с. В Prime — сразу."
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }
        let started = Date()
        let bytes = Double((try? FileManager.default.attributesOfItem(atPath: source.path)[.size] as? NSNumber)?.int64Value ?? 0)
        do {
            let game = try await Porter.importGame(from: source, title: title) { text in
                Task { @MainActor in self.busy = text }
            }
            if game.kind == .godot && !Edition.isPrime {
                let target = bytes / Edition.freeBytesPerSecond
                while Date().timeIntervalSince(started) < target {
                    let done = Date().timeIntervalSince(started) / target
                    busy = "Free: eX не быстрее 2 МБ/с — \(Int(done * 100))%. В Prime без ограничений."
                    try? await Task.sleep(nanoseconds: 250_000_000)
                }
                ExQuota.recordSuccess()
            }
            games.insert(game, at: 0)
            save()
            busy = nil
            refreshQuota()
            if let note = game.note { message = note }
            return game
        } catch {
            busy = nil
            message = "Не получилось: " + error.localizedDescription
            return nil
        }
    }
}
