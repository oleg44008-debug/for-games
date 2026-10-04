import Foundation
import Security
import UIKit
import WebKit

/// Prime is unlocked by a purchase in the Dustore store, not by the file.
///
/// The app asks dustore.ru for the Prime product as the buyer signed in to the in-app store: the
/// store answers with the download (bought) or with its payment / sign-in page (not bought).
/// Nothing leaves the phone but that request to dustore.ru. The confirmation is kept in this
/// device's Keychain (never synced or restored to another device) and re-checked monthly.
enum PrimeLicense {
    /// The Prime product in the Dustore store (game_id). 0 until the product is published.
    static let productId = 0
    static let downloadURL = URL(string: "https://dustore.ru/swad/controllers/download_game.php?game_id=\(productId)")!
    private static let account = "prime-license"
    private static let recheck: TimeInterval = 30 * 24 * 3600

    enum Ownership { case owned, notOwned, needsLogin, notPublished, offline }

    private static var cached: Bool?

    private static func keychain(_ query: [String: Any]) -> [String: Any] {
        var q = query
        q[kSecClass as String] = kSecClassGenericPassword
        q[kSecAttrService as String] = "ru.dustore.launcher.ios"
        q[kSecAttrAccount as String] = account
        return q
    }

    private static var checkedAt: Date? {
        var item: CFTypeRef?
        guard SecItemCopyMatching(keychain([kSecReturnData as String: true]) as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data, let text = String(data: data, encoding: .utf8), let seconds = Double(text) else { return nil }
        return Date(timeIntervalSince1970: seconds)
    }

    private static func store(_ date: Date?) {
        SecItemDelete(keychain([:]) as CFDictionary)
        cached = date != nil
        guard let date else { return }
        var add = keychain([:])
        add[kSecValueData as String] = Data(String(date.timeIntervalSince1970).utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(add as CFDictionary, nil)
    }

    static var isActive: Bool {
        if let cached { return cached }
        cached = checkedAt != nil
        return cached!
    }

    static var needsRecheck: Bool { checkedAt.map { Date().timeIntervalSince($0) > recheck } ?? false }

    /// Stops at the first redirect so the answer (file or payment page) can be read, not followed.
    private final class NoRedirect: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    }

    @MainActor
    static func askStore() async -> Ownership {
        guard productId > 0 else { return .notPublished }
        let cookies = await withCheckedContinuation { (done: CheckedContinuation<[HTTPCookie], Never>) in
            WKWebsiteDataStore.default().httpCookieStore.getAllCookies { done.resume(returning: $0) }
        }.filter { $0.domain.hasSuffix("dustore.ru") }
        var request = URLRequest(url: downloadURL, timeoutInterval: 20)
        request.httpMethod = "HEAD"
        if !cookies.isEmpty { request.setValue(cookies.map { "\($0.name)=\($0.value)" }.joined(separator: "; "), forHTTPHeaderField: "Cookie") }
        let session = URLSession(configuration: .ephemeral, delegate: NoRedirect(), delegateQueue: nil)
        defer { session.finishTasksAndInvalidate() }
        guard let (_, response) = try? await session.data(for: request), let http = response as? HTTPURLResponse else { return .offline }
        let target = (http.value(forHTTPHeaderField: "Location") ?? "").lowercased()
        if target.contains("s3.regru.cloud") && target.contains("/game-\(productId)/") { return .owned }
        if target.contains("login") || cookies.isEmpty { return .needsLogin }
        return .notOwned
    }

    @MainActor
    static func activate() async -> Ownership {
        let answer = await askStore()
        if answer == .owned { store(Date()) }
        return answer
    }

    /// Monthly: a refunded purchase turns Prime off; no internet keeps it on.
    @MainActor
    static func recheckIfDue() async {
        guard Edition.isPrimeBuild, isActive, needsRecheck else { return }
        switch await askStore() {
        case .owned: store(Date())
        case .notOwned: store(nil)
        default: break
        }
    }

    static func explain(_ answer: Ownership) -> String {
        switch answer {
        case .owned: return "Prime активирован на этом устройстве. Перезапустите приложение, чтобы включились все возможности."
        case .needsLogin: return "Войдите в свой аккаунт Dustore во вкладке «Магазин» и проверьте покупку снова."
        case .notOwned: return "В этом аккаунте Dustore Prime не куплен. Купите Prime в магазине Dustore и повторите."
        case .notPublished: return "Prime ещё не выставлен в магазине Dustore."
        case .offline: return "Нет связи с магазином Dustore. Проверьте интернет и повторите."
        }
    }
}
