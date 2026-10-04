import SwiftUI
import UniformTypeIdentifiers

/// DUSTORE LAUNCHER V palette: plum-black, pink for selection, solid yellow for the main action. Flat.
enum Theme {
    static let background = Color(red: 0x12 / 255, green: 0x08 / 255, blue: 0x11 / 255)
    static let card = Color(red: 0x24 / 255, green: 0x10 / 255, blue: 0x1F / 255)
    static let line = Color(red: 0x4A / 255, green: 0x25 / 255, blue: 0x41 / 255)
    static let pink = Color(red: 1, green: 0x62 / 255, blue: 0xAB / 255)
    static let yellow = Color(red: 1, green: 0xD7 / 255, blue: 0x3D / 255)
    static let ink = Color(red: 0x2B / 255, green: 0x10 / 255, blue: 0x20 / 255)
    static let text = Color(red: 1, green: 0xF4 / 255, blue: 0xF1 / 255)
    static let muted = Color(red: 0xE6 / 255, green: 0xC6 / 255, blue: 0xD9 / 255)
    static let dim = Color(red: 0xA7 / 255, green: 0x84 / 255, blue: 0x9B / 255)
    static let good = Color(red: 0x86 / 255, green: 0xE6 / 255, blue: 0xA8 / 255)
    static let warn = Color(red: 0xE8 / 255, green: 0xC4 / 255, blue: 0x7A / 255)
}

struct DisplayTitle: View {
    let text: String
    var size: CGFloat = 30
    var body: some View {
        Text(text.uppercased()).font(.system(size: size, weight: .heavy)).fontWidth(.condensed).foregroundColor(Theme.text)
    }
}

struct Chip: View {
    let text: String
    var color: Color = Theme.pink
    var body: some View {
        Text("•  " + text).font(.system(size: 12, weight: .semibold)).foregroundColor(color)
            .padding(.horizontal, 10).padding(.vertical, 5)
            .background(Capsule().fill(color.opacity(0.12))).overlay(Capsule().stroke(color.opacity(0.3), lineWidth: 1))
    }
}

struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 10) { content }
            .padding(16).frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14).fill(Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.line.opacity(0.7), lineWidth: 1))
    }
}

struct YellowButton: View {
    let title: String
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text(title).font(.system(size: 16, weight: .bold)).foregroundColor(Theme.ink)
                .frame(maxWidth: .infinity).padding(.vertical, 13)
                .background(Capsule().fill(Theme.yellow))
        }.buttonStyle(.plain)
    }
}

struct EditionMark: View {
    @State private var asking = false
    @State private var answer: String?
    var body: some View {
        if Edition.isPrimeBuild && !Edition.isPrime {
            Button { Task { asking = true; answer = PrimeLicense.explain(await PrimeLicense.activate()); asking = false } } label: {
                Chip(text: asking ? "Проверяю покупку…" : "Prime · активировать", color: Theme.pink)
            }
            .buttonStyle(.plain)
            .alert("DustoreX Prime", isPresented: Binding(get: { answer != nil }, set: { if !$0 { answer = nil } })) {
                Button("Понятно", role: .cancel) {}
            } message: { Text(answer ?? "") }
        } else {
            Chip(text: Edition.name, color: Edition.isPrime ? Theme.pink : Theme.dim)
        }
    }
}

struct RootView: View {
    @StateObject private var library = Library.shared
    @State private var tab = 1

    var body: some View {
        TabView(selection: $tab) {
            StoreScreen().tabItem { Label("Магазин", systemImage: "bag") }.tag(0)
            LibraryScreen(openStore: { tab = 0 }).tabItem { Label("Библиотека", systemImage: "cube") }.tag(1)
            ExScreen().tabItem { Label("eX", systemImage: "arrow.left.arrow.right") }.tag(2)
        }
        .tint(Theme.pink)
        .environmentObject(library)
        .preferredColorScheme(.dark)
        .overlay { if let busy = library.busy { BusyOverlay(text: busy) } }
        .alert("DustoreX", isPresented: Binding(get: { library.message != nil }, set: { if !$0 { library.message = nil } })) {
            Button("Понятно", role: .cancel) {}
        } message: { Text(library.message ?? "") }
        .fullScreenCover(item: $library.playing) { game in PlayerScreen(game: game) }
    }
}

struct BusyOverlay: View {
    let text: String
    var body: some View {
        ZStack {
            Color.black.opacity(0.55).ignoresSafeArea()
            Card {
                HStack(spacing: 12) {
                    ProgressView().tint(Theme.pink)
                    Text(text).font(.system(size: 15, weight: .medium)).foregroundColor(Theme.text)
                }
            }.padding(32)
        }
    }
}

struct StoreScreen: View {
    @EnvironmentObject var library: Library
    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                Text("DUSTORE").font(.system(size: 18, weight: .heavy)).fontWidth(.condensed).foregroundColor(Theme.text)
                Text("X").font(.system(size: 18, weight: .heavy)).fontWidth(.condensed).foregroundColor(Theme.yellow)
                Spacer()
                EditionMark()
            }.padding(.horizontal, 16).padding(.vertical, 10).background(Theme.background)
            StoreWebView(library: library).ignoresSafeArea(edges: .bottom)
        }.background(Theme.background)
    }
}

struct LibraryScreen: View {
    @EnvironmentObject var library: Library
    let openStore: () -> Void
    @State private var importing = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 2) {
                        DisplayTitle(text: "Библиотека")
                        Text(library.games.isEmpty ? "пока без игр" : "\(library.games.count) в библиотеке").font(.system(size: 13)).foregroundColor(Theme.dim)
                    }
                    Spacer()
                    EditionMark()
                }
                if library.games.isEmpty {
                    Card {
                        DisplayTitle(text: "Пока здесь пусто", size: 22).frame(maxWidth: .infinity)
                        Text("Скачайте игру в магазине Dustore или добавьте архив из «Файлов». eX перенесёт Godot-игру на iPhone, веб-игры запускаются как есть.")
                            .font(.system(size: 15)).foregroundColor(Theme.muted).multilineTextAlignment(.center).frame(maxWidth: .infinity)
                        YellowButton(title: "Открыть магазин", action: openStore)
                    }
                }
                ForEach(library.games) { game in GameCard(game: game) }
                Button { importing = true } label: {
                    Label("Добавить игру из «Файлов»", systemImage: "folder").font(.system(size: 15, weight: .semibold)).foregroundColor(Theme.pink)
                        .frame(maxWidth: .infinity).padding(.vertical, 12)
                        .background(Capsule().stroke(Theme.line, lineWidth: 1))
                }.buttonStyle(.plain)
                Text(library.quotaLine).font(.system(size: 12)).foregroundColor(Theme.dim)
            }.padding(16)
        }
        .background(Theme.background.ignoresSafeArea())
        .fileImporter(isPresented: $importing, allowedContentTypes: [.zip, .folder, .item], allowsMultipleSelection: false) { result in
            guard case .success(let urls) = result, let url = urls.first else { return }
            Task {
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }
                let local = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + "-" + url.lastPathComponent)
                do { try FileManager.default.copyItem(at: url, to: local) } catch { library.message = error.localizedDescription; return }
                await library.add(from: local, title: url.deletingPathExtension().lastPathComponent)
                try? FileManager.default.removeItem(at: local)
            }
        }
    }
}

struct GameCard: View {
    @EnvironmentObject var library: Library
    let game: Game

    private var state: (String, Color) {
        switch game.kind {
        case .godot: return ("\(game.engine ?? "Godot") · перенесена eX", Theme.good)
        case .web: return ("Веб-игра · готова", Theme.good)
        case .unity: return ("Unity · на iPhone не запускается", Theme.warn)
        case .unsupported: return ("\(game.engine ?? "Движок не узнан") · не поддерживается", Theme.warn)
        }
    }

    var body: some View {
        Card {
            HStack(spacing: 12) {
                RoundedRectangle(cornerRadius: 12).fill(Color(hue: Double(abs(game.title.hashValue) % 360) / 360, saturation: 0.5, brightness: 0.42))
                    .frame(width: 56, height: 56)
                    .overlay(Text(String(game.title.prefix(1)).uppercased()).font(.system(size: 26, weight: .heavy)).fontWidth(.condensed).foregroundColor(.white.opacity(0.85)))
                VStack(alignment: .leading, spacing: 6) {
                    DisplayTitle(text: game.title, size: 18).lineLimit(2)
                    Chip(text: state.0, color: state.1)
                }
                Spacer(minLength: 0)
            }
            if let note = game.note {
                Text(note).font(.system(size: 13)).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
            }
            if game.playable { YellowButton(title: "Играть") { library.playing = game } }
            HStack {
                Spacer()
                Button("Убрать") { library.remove(game) }.font(.system(size: 13)).foregroundColor(Theme.dim)
            }
        }
    }
}

struct ExScreen: View {
    @EnvironmentObject var library: Library
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    DisplayTitle(text: "eX для iPhone")
                    Spacer()
                    EditionMark()
                }
                Card {
                    Text("Что eX переносит на iPhone").font(.system(size: 17, weight: .semibold)).foregroundColor(Theme.text)
                    Label("Godot 4.3+ и 3.3+ — сборки для Windows, Mac и Linux. eX берёт пакет игры и запускает его на официальном веб-движке Godot той же версии.", systemImage: "checkmark.circle")
                        .foregroundColor(Theme.muted)
                    Label("Веб-игры из магазина Dustore — запускаются как есть, без интернета.", systemImage: "checkmark.circle").foregroundColor(Theme.muted)
                }.font(.system(size: 14))
                Card {
                    Text("Unity на iPhone не запускается").font(.system(size: 17, weight: .semibold)).foregroundColor(Theme.warn)
                    Text(Porter.unityMessage).font(.system(size: 14)).foregroundColor(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    Text("То же относится к Windows-играм на других движках и к Godot-играм на C#.").font(.system(size: 13)).foregroundColor(Theme.dim)
                }
                Card {
                    Text("Управление").font(.system(size: 17, weight: .semibold)).foregroundColor(Theme.text)
                    Text("Касания работают как мышь. Игры, которым нужна клавиатура, удобнее с Bluetooth-клавиатурой или геймпадом.").font(.system(size: 14)).foregroundColor(Theme.muted)
                }
                Text(library.quotaLine).font(.system(size: 12)).foregroundColor(Theme.dim)
            }.padding(16)
        }.background(Theme.background.ignoresSafeArea())
    }
}

struct PlayerScreen: View {
    @EnvironmentObject var library: Library
    let game: Game
    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.black.ignoresSafeArea()
            GameWebView(game: game) { report in SelfTest.gameReported(report) }.ignoresSafeArea()
            Button { library.playing = nil } label: {
                Image(systemName: "xmark").font(.system(size: 15, weight: .bold)).foregroundColor(.white)
                    .frame(width: 36, height: 36).background(Circle().fill(Color.black.opacity(0.45)))
            }.padding(.leading, 14).padding(.top, 10)
        }
        .statusBarHidden()
        .persistentSystemOverlays(.hidden)
    }
}
