import CryptoKit
import Foundation
import Security

/// Free or Prime — fixed at build time (SWIFT_ACTIVE_COMPILATION_CONDITIONS=PRIME).
enum Edition {
#if PRIME
    static let isPrimeBuild = true
#else
    static let isPrimeBuild = false
#endif
    /// A Prime build whose purchase the Dustore store confirmed on this device.
    static var isPrime: Bool { isPrimeBuild && PrimeLicense.isActive }
    static var name: String { isPrime ? "Prime" : "Free" }
    static let freePerDay = 3
    static let freeQueueSeconds = 15
    static let freeBytesPerSecond: Double = 2 * 1024 * 1024
}

/// Server time, never the phone clock: the Date header of dustore.ru (or google.com), carried
/// forward by the system uptime. Without the internet time stands still on the last trusted moment.
enum TrustedClock {
    private static var server: Date?
    private static var uptimeAtSync: TimeInterval = 0
    /// Tests only.
    static var override: Date?

    static func sync() async {
        if let override { server = override; uptimeAtSync = ProcessInfo.processInfo.systemUptime; ExQuota.noteTrusted(override); return }
        for source in ["https://dustore.ru/", "https://www.google.com/generate_204"] {
            guard let url = URL(string: source) else { continue }
            var request = URLRequest(url: url, timeoutInterval: 6)
            request.httpMethod = "HEAD"
            if let (_, response) = try? await URLSession.shared.data(for: request),
               let header = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Date"),
               let date = parse(header) {
                server = date
                uptimeAtSync = ProcessInfo.processInfo.systemUptime
                ExQuota.noteTrusted(date)
                return
            }
        }
    }

    static var now: Date? { server.map { $0.addingTimeInterval(ProcessInfo.processInfo.systemUptime - uptimeAtSync) } }

    static func day(of date: Date) -> String {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = TimeZone(secondsFromGMT: 3 * 3600)
        format.dateFormat = "yyyy-MM-dd"
        return format.string(from: date)
    }

    private static func parse(_ header: String) -> Date? {
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return format.date(from: header)
    }
}

/// Free: three eX transfers per Moscow day by server time. The counter is signed and kept in the
/// Keychain as well — it survives deleting and reinstalling the app; editing it reads as «used up».
enum ExQuota {
    private struct Record: Codable { var day: String; var used: Int; var trusted: Double }
    private static let key = SymmetricKey(data: Data("dustore-ex-quota/ios/v1".utf8))
    private static let account = "ex-quota"

    private static func sign(_ body: Data) -> String {
        HMAC<SHA256>.authenticationCode(for: body, using: key).map { String(format: "%02x", $0) }.joined()
    }

    private static func encode(_ record: Record) -> String {
        let body = (try? JSONEncoder().encode(record)) ?? Data()
        return body.base64EncodedString() + "." + sign(body)
    }

    private static func decode(_ text: String?) -> (Record?, tampered: Bool) {
        guard let text, !text.isEmpty else { return (nil, false) }
        let parts = text.split(separator: ".")
        guard parts.count == 2, let body = Data(base64Encoded: String(parts[0])), sign(body) == parts[1],
              let record = try? JSONDecoder().decode(Record.self, from: body) else { return (nil, true) }
        return (record, false)
    }

    private static func keychainRead() -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "ru.dustore.launcher.ios",
                                    kSecAttrAccount as String: account, kSecReturnData as String: true]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private static func keychainWrite(_ text: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "ru.dustore.launcher.ios", kSecAttrAccount as String: account]
        SecItemDelete(query as CFDictionary)
        var add = query
        add[kSecValueData as String] = Data(text.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(add as CFDictionary, nil)
    }

    private static func read() -> (Record, tampered: Bool) {
        var merged = Record(day: "", used: 0, trusted: 0), tampered = false
        for text in [UserDefaults.standard.string(forKey: account), keychainRead()] {
            let (record, bad) = decode(text)
            tampered = tampered || bad
            guard let record else { continue }
            merged.trusted = max(merged.trusted, record.trusted)
            if record.day == merged.day { merged.used = max(merged.used, record.used) }
            else if later(record.day, merged.day) { merged.day = record.day; merged.used = record.used }
        }
        return (merged, tampered)
    }

    private static func write(_ record: Record) {
        let text = encode(record)
        UserDefaults.standard.set(text, forKey: account)
        keychainWrite(text)
    }

    private static func later(_ a: String, _ b: String) -> Bool {
        let aDate = a.count == 10 && a.first?.isNumber == true, bDate = b.count == 10 && b.first?.isNumber == true
        return aDate && (!bDate || a > b)
    }

    private static func currentDay(_ record: Record) -> String {
        let day = TrustedClock.now.map(TrustedClock.day(of:))
            ?? (record.trusted > 0 ? TrustedClock.day(of: Date(timeIntervalSince1970: record.trusted)) : record.day.isEmpty ? "first-run" : record.day)
        return later(record.day, day) ? record.day : day
    }

    static func noteTrusted(_ date: Date) {
        let (record, tampered) = read()
        guard !tampered, date.timeIntervalSince1970 > record.trusted else { return }
        let day = TrustedClock.day(of: date)
        let keep = record.day == day || later(record.day, day)
        write(Record(day: keep ? record.day : day, used: keep ? record.used : 0, trusted: date.timeIntervalSince1970))
    }

    static var usedToday: Int {
        let (record, tampered) = read()
        if tampered { return Edition.freePerDay }
        return record.day == currentDay(record) ? record.used : 0
    }

    static var leftToday: Int { Edition.isPrime ? Int.max : max(0, Edition.freePerDay - usedToday) }

    static var refusal: String? {
        leftToday > 0 ? nil
            : "Сегодня использованы все \(Edition.freePerDay) переноса eX версии Free. Новые появятся после полуночи по Москве (время берётся с сервера, нужен интернет). В Prime — без лимита."
    }

    static func recordSuccess() {
        guard !Edition.isPrime else { return }
        let (record, tampered) = read()
        let day = currentDay(record)
        let used = tampered ? Edition.freePerDay : record.day == day ? record.used : 0
        write(Record(day: day, used: used + 1, trusted: record.trusted))
    }

    static func clearForTests() {
        UserDefaults.standard.removeObject(forKey: account)
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "ru.dustore.launcher.ios", kSecAttrAccount as String: account] as CFDictionary)
    }
}
