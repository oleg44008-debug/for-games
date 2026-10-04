import Foundation

enum GameKind: String, Codable { case web, godot, unity, unsupported }

struct Game: Codable, Identifiable, Equatable {
    var id: UUID
    var title: String
    var kind: GameKind
    var engine: String?
    var note: String?
    var added: Date
    var playable: Bool { kind == .web || kind == .godot }

    static var gamesFolder: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Games", isDirectory: true)
    }
    var folder: URL { Self.gamesFolder.appendingPathComponent(id.uuidString, isDirectory: true) }
    /// What the player serves: index.html and everything next to it.
    var webRoot: URL { folder.appendingPathComponent("web", isDirectory: true) }
}

struct PortError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// eX for iPhone. Godot games (Windows, Mac, Linux builds) are moved onto Godot's official web
/// engine of the same version and run inside the app; web games run as they are. Unity builds are
/// compiled x86 code for Windows — iOS forbids the JIT that would be needed to translate them,
/// so they cannot run on an iPhone.
enum Porter {
    static let unityMessage = "Игры на Unity на iPhone запустить нельзя: сборка Unity — это готовый машинный код для Windows, а iOS запрещает приложениям перевод такого кода на лету (JIT). Исходного проекта, из которого можно было бы собрать версию для iPhone, у eX нет."

    static func importGame(from source: URL, title: String?, progress: @escaping (String) -> Void) async throws -> Game {
        let fm = FileManager.default
        var game = Game(id: UUID(), title: title ?? source.deletingPathExtension().lastPathComponent, kind: .unsupported, engine: nil, note: nil, added: Date())
        let src = game.folder.appendingPathComponent("src", isDirectory: true)
        try fm.createDirectory(at: src, withIntermediateDirectories: true)

        // 1. Unpack.
        var isFolder: ObjCBool = false
        fm.fileExists(atPath: source.path, isDirectory: &isFolder)
        if isFolder.boolValue {
            try fm.copyItem(at: source, to: src.appendingPathComponent(source.lastPathComponent))
        } else if isZip(source) {
            progress("Распаковываю…")
            let archive = try ZipArchive(source: try FileZipSource(url: source))
            try archive.extractAll(to: src) { fraction in progress("Распаковываю… \(Int(fraction * 100))%") }
            // An APK or a ZIP inside the ZIP (the store sometimes wraps builds twice).
            let files = allFiles(in: src)
            if files.count == 1, let inner = files.first, isZip(inner), inner.pathExtension.lowercased() != "apk" {
                let nested = try ZipArchive(source: try FileZipSource(url: inner))
                try nested.extractAll(to: src.appendingPathComponent("inner"))
                try? fm.removeItem(at: inner)
            }
        } else {
            try fm.copyItem(at: source, to: src.appendingPathComponent(source.lastPathComponent))
        }

        // 2. Recognise.
        progress("eX разбирает игру…")
        let files = allFiles(in: src)
        let names = Set(files.map { $0.lastPathComponent.lowercased() })
        if names.contains("unityplayer.dll") || names.contains("globalgamemanagers") || names.contains("data.unity3d") || names.contains("libunity.so") || names.contains("unityframework") {
            game.kind = .unity; game.engine = "Unity"; game.note = unityMessage
            try? fm.removeItem(at: src)
            return game
        }
        if let page = files.first(where: { $0.lastPathComponent.lowercased() == "index.html" }),
           (try? fm.contentsOfDirectory(atPath: page.deletingLastPathComponent().path))?.contains(where: { $0.hasSuffix(".js") || $0.hasSuffix(".wasm") || $0.hasSuffix(".pck") }) == true {
            try fm.moveItem(at: page.deletingLastPathComponent(), to: game.webRoot)
            try? fm.removeItem(at: src)
            game.kind = .web; game.engine = "Веб-игра"
            return game
        }
        if names.contains("godotsharp.dll") || files.contains(where: { $0.deletingLastPathComponent().lastPathComponent.hasPrefix("data_") && $0.pathExtension.lowercased() == "dll" }) {
            game.kind = .unsupported; game.engine = "Godot · C#"
            game.note = "Эта Godot-игра написана на C# (.NET): веб-движок Godot такие игры не запускает, поэтому на iPhone её перенести нельзя."
            try? fm.removeItem(at: src)
            return game
        }
        guard let pack = findGodotPack(files) else {
            try? fm.removeItem(at: src)
            game.note = names.contains(where: { $0.hasSuffix(".exe") })
                ? "eX не нашёл в сборке игру на Godot. На iPhone переносятся Godot-игры и веб-игры; Windows-игры на других движках запустить на iPhone нельзя."
                : "eX не узнал движок этой игры. На iPhone переносятся Godot-игры (Windows, Mac, Linux) и веб-игры."
            return game
        }

        // 3. Godot: the official web engine of the same version.
        let version = pack.version
        game.engine = "Godot \(version.text)"
        guard version.supported else {
            try? fm.removeItem(at: src)
            game.note = "Godot \(version.text): " + (version.major == 4
                ? "версии 4.0–4.2 есть только в многопоточном веб-движке, а он на iPhone не работает. Поддерживаются Godot 3.3+ и 4.3+."
                : "поддерживаются Godot 3.3 и новее.")
            return game
        }
        let engine = try await GodotWebEngine.files(for: version, progress: progress)
        progress("Собираю игру для iPhone…")
        try fm.createDirectory(at: game.webRoot, withIntermediateDirectories: true)
        for (name, file) in engine {
            try fm.copyItem(at: file, to: game.webRoot.appendingPathComponent(name))
        }
        // The whole file is used as the pack: Godot finds a pack embedded at the end of an .exe itself.
        try fm.moveItem(at: pack.file, to: game.webRoot.appendingPathComponent("index.pck"))
        try page(major: version.major).write(to: game.webRoot.appendingPathComponent("index.html"), atomically: true, encoding: .utf8)
        try? fm.removeItem(at: src)
        game.kind = .godot
        return game
    }

    struct GodotVersion {
        let major: Int, minor: Int, patch: Int
        var text: String { patch > 0 ? "\(major).\(minor).\(patch)" : "\(major).\(minor)" }
        var supported: Bool { (major == 4 && minor >= 3) || (major == 3 && minor >= 3) }
        var templateEntry: String { major == 4 ? "templates/web_nothreads_release.zip" : "templates/webassembly_release.zip" }
    }

    struct GodotPack { let file: URL; let version: GodotVersion }

    static func isZip(_ url: URL) -> Bool {
        guard let h = try? FileHandle(forReadingFrom: url), let head = try? h.read(upToCount: 4) else { return false }
        try? h.close()
        return head.count == 4 && head[0] == 0x50 && head[1] == 0x4b && head[2] == 0x03 && head[3] == 0x04
    }

    static func allFiles(in folder: URL) -> [URL] {
        guard let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey]) else { return [] }
        return walker.compactMap { $0 as? URL }.filter { (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true }
    }

    private static func header(at url: URL) -> GodotVersion? {
        guard let h = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? h.close() }
        func version(at offset: UInt64) -> GodotVersion? {
            guard (try? h.seek(toOffset: offset)) != nil, let d = try? h.read(upToCount: 20), d.count == 20,
                  d[0] == 0x47, d[1] == 0x44, d[2] == 0x50, d[3] == 0x43 else { return nil }
            func u32(_ i: Int) -> Int { Int(d[i]) | Int(d[i + 1]) << 8 | Int(d[i + 2]) << 16 | Int(d[i + 3]) << 24 }
            return GodotVersion(major: u32(8), minor: u32(12), patch: u32(16))
        }
        if let v = version(at: 0) { return v }
        // Embedded pack: …[pack][uint64 size]["GDPC"] at the very end of the executable.
        guard let length = try? h.seekToEnd(), length > 32, (try? h.seek(toOffset: length - 12)) != nil,
              let tail = try? h.read(upToCount: 12), tail.count == 12, tail[8] == 0x47, tail[9] == 0x44, tail[10] == 0x50, tail[11] == 0x43 else { return nil }
        var size: UInt64 = 0
        for i in (0..<8).reversed() { size = size << 8 | UInt64(tail[i]) }
        guard size > 0, size < length else { return nil }
        return version(at: length - 12 - size)
    }

    private static func findGodotPack(_ files: [URL]) -> GodotPack? {
        func size(_ u: URL) -> Int { (try? u.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0 }
        let packs = files.filter { $0.pathExtension.lowercased() == "pck" }.sorted { size($0) > size($1) }
        for pack in packs { if let v = header(at: pack) { return GodotPack(file: pack, version: v) } }
        let binaries = files.filter { ["exe", "x86_64", "arm64", ""].contains($0.pathExtension.lowercased()) && size($0) > 1_000_000 }
            .filter { !$0.lastPathComponent.lowercased().contains("console") }
        for binary in binaries { if let v = header(at: binary) { return GodotPack(file: binary, version: v) } }
        return nil
    }

    /// The page that starts the game. Same for Godot 3.3+ and 4.3+: both ship `Engine` with this config.
    static func page(major: Int) -> String {
        """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, user-scalable=no, initial-scale=1.0, viewport-fit=cover">
        <style>html,body,#canvas{margin:0;padding:0;border:0;width:100%;height:100%;background:#000;overflow:hidden;touch-action:none}#canvas{display:block}#canvas:focus{outline:none}
        #ex-status{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;text-align:center;padding:24px;color:#E6C6D9;font:600 15px -apple-system,sans-serif;background:#120811}</style></head>
        <body><canvas id="canvas"></canvas><div id="ex-status">eX запускает игру…</div>
        <script>
        if (location.search.indexOf('selftest') >= 0) {
          const get = HTMLCanvasElement.prototype.getContext;
          HTMLCanvasElement.prototype.getContext = function (type, attrs) { return get.call(this, type, Object.assign({}, attrs || {}, { preserveDrawingBuffer: true })); };
        }
        </script>
        <script src="index.js"></script>
        <script>
        function exReport(m){try{window.webkit.messageHandlers.ex.postMessage(m)}catch(e){}}
        (function(){let n=0;for(const k of ['log','warn','error']){const o=console[k].bind(console);console[k]=function(...a){if(n++<80)exReport('log: '+a.join(' '));o(...a)}}})();
        window.addEventListener('error',e=>exReport('log: page error '+e.message));
        if (location.search.indexOf('selftest') >= 0) setTimeout(function(){
          const c=document.getElementById('canvas'), r=c.getBoundingClientRect(), x=r.left+r.width/2, y=r.top+r.height/2;
          for (const t of ['pointerdown','mousedown','pointerup','mouseup','click']) c.dispatchEvent(new (t.startsWith('pointer')?PointerEvent:MouseEvent)(t,{bubbles:true,clientX:x,clientY:y,button:0,buttons:t.endsWith('down')?1:0,pointerType:'mouse'}));
          exReport('log: selftest tapped the canvas');
        }, 30000);
        if (location.search.indexOf('selftest') >= 0) {
          const probe=document.createElement('canvas'); probe.width=probe.height=48;
          probe.style.cssText='position:absolute;right:8px;top:60px;width:48px;height:48px;z-index:9';
          document.body.appendChild(probe);
          const g=probe.getContext('webgl2'); if(g){g.clearColor(1,0,0.6,1);g.clear(g.COLOR_BUFFER_BIT);}
          for (const at of [20000, 45000, 65000]) setTimeout(function(){
            try { const c=document.getElementById('canvas'); exReport('frame: '+c.width+'x'+c.height+' '+c.toDataURL('image/jpeg',0.6)); }
            catch(e){ exReport('log: frame failed '+e); }
          }, at);
        }
        const engine = new Engine({ executable: 'index', mainPack: 'index.pck', canvasResizePolicy: 2, args: [], focusCanvas: true, ensureCrossOriginIsolationHeaders: false, experimentalVK: false, gdextensionLibs: [] });
        engine.startGame({ onProgress: (cur, total) => { if (total > 0) document.getElementById('ex-status').textContent = 'eX загружает игру… ' + Math.round(cur * 100 / total) + '%'; } })
          .then(() => { const s = document.getElementById('ex-status'); if (s) s.remove(); exReport('started'); })
          .catch((e) => { document.getElementById('ex-status').textContent = 'Игра не запустилась: ' + e; exReport('error: ' + e); });
        </script></body></html>
        """
    }
}

/// Godot's official web engine for one version, taken out of the export-templates archive on
/// GitHub with HTTP ranges: about 10 MB is downloaded instead of the whole 1.3 GB archive.
enum GodotWebEngine {
    static func files(for version: Porter.GodotVersion, progress: @escaping (String) -> Void) async throws -> [(String, URL)] {
        let cache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("GodotWeb", isDirectory: true).appendingPathComponent(version.text, isDirectory: true)
        let wanted = ["godot.js", "godot.wasm", "godot.audio.worklet.js", "godot.audio.position.worklet.js"]
        if !FileManager.default.fileExists(atPath: cache.appendingPathComponent("index.wasm").path) {
            progress("Скачиваю веб-движок Godot \(version.text)…")
            guard let url = URL(string: "https://github.com/godotengine/godot/releases/download/\(version.text)-stable/Godot_v\(version.text)-stable_export_templates.tpz") else {
                throw PortError(message: "Неверная версия Godot.")
            }
            let remote = try await RemoteZipSource(url: url)
            let template: Data = try await Task.detached {
                let archive = try ZipArchive(source: remote)
                guard let entry = archive.entries.first(where: { $0.name == version.templateEntry }) else {
                    throw PortError(message: "В официальных шаблонах Godot \(version.text) нет веб-движка.")
                }
                return try archive.data(of: entry)
            }.value
            let inner = try ZipArchive(source: DataZipSource(template))
            try FileManager.default.createDirectory(at: cache, withIntermediateDirectories: true)
            for entry in inner.entries where wanted.contains(entry.name) {
                try inner.extract(entry, to: cache.appendingPathComponent(entry.name.replacingOccurrences(of: "godot.", with: "index.")))
            }
        }
        let names = (try? FileManager.default.contentsOfDirectory(atPath: cache.path)) ?? []
        guard names.contains("index.js"), names.contains("index.wasm") else { throw PortError(message: "Веб-движок Godot скачался не полностью — повторите.") }
        return names.map { ($0, cache.appendingPathComponent($0)) }
    }
}
