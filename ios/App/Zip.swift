import Compression
import Foundation

/// One file inside a ZIP archive, as its central directory describes it.
struct ZipEntry {
    let name: String
    let method: UInt16          // 0 stored, 8 deflate
    let compressedSize: UInt64
    let size: UInt64
    let localHeaderOffset: UInt64
    var isDirectory: Bool { name.hasSuffix("/") }
}

enum ZipError: LocalizedError {
    case notZip, unsupported(String), truncated
    var errorDescription: String? {
        switch self {
        case .notZip: return "Файл не похож на ZIP-архив."
        case .unsupported(let what): return "Архив использует неподдерживаемый формат: \(what)."
        case .truncated: return "Архив повреждён или скачан не полностью."
        }
    }
}

/// Anything a ZIP can be read from: a local file or a remote file read with HTTP ranges.
protocol ZipSource {
    var length: UInt64 { get }
    func read(at offset: UInt64, count: Int) throws -> Data
}

final class FileZipSource: ZipSource {
    private let handle: FileHandle
    let length: UInt64
    init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
        length = try handle.seekToEnd()
    }
    deinit { try? handle.close() }
    func read(at offset: UInt64, count: Int) throws -> Data {
        try handle.seek(toOffset: offset)
        return try handle.read(upToCount: count) ?? Data()
    }
}

final class DataZipSource: ZipSource {
    private let data: Data
    init(_ data: Data) { self.data = data }
    var length: UInt64 { UInt64(data.count) }
    func read(at offset: UInt64, count: Int) throws -> Data {
        let start = Int(offset), end = min(data.count, start + count)
        guard start <= end else { throw ZipError.truncated }
        return data.subdata(in: start..<end)
    }
}

/// A file on a web server that answers HTTP Range requests (GitHub release assets do).
final class RemoteZipSource: ZipSource {
    let url: URL
    let length: UInt64
    init(url: URL) async throws {
        self.url = url
        var head = URLRequest(url: url)
        head.httpMethod = "HEAD"
        let (_, response) = try await URLSession.shared.data(for: head)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200, http.expectedContentLength > 0 else {
            throw URLError(.fileDoesNotExist)
        }
        length = UInt64(http.expectedContentLength)
    }
    func read(at offset: UInt64, count: Int) throws -> Data {
        var request = URLRequest(url: url)
        request.setValue("bytes=\(offset)-\(offset + UInt64(count) - 1)", forHTTPHeaderField: "Range")
        let semaphore = DispatchSemaphore(value: 0)
        var result: Result<Data, Error> = .failure(ZipError.truncated)
        URLSession.shared.dataTask(with: request) { data, response, error in
            if let error { result = .failure(error) }
            else if let data, let http = response as? HTTPURLResponse, http.statusCode == 206 || http.statusCode == 200 { result = .success(data) }
            semaphore.signal()
        }.resume()
        semaphore.wait()
        return try result.get()
    }
}

private extension Data {
    func u16(_ at: Int) -> UInt16 { UInt16(self[startIndex + at]) | UInt16(self[startIndex + at + 1]) << 8 }
    func u32(_ at: Int) -> UInt32 { UInt32(u16(at)) | UInt32(u16(at + 2)) << 16 }
    func u64(_ at: Int) -> UInt64 { UInt64(u32(at)) | UInt64(u32(at + 4)) << 32 }
    func lastRange(of bytes: [UInt8]) -> Int? {
        guard count >= bytes.count else { return nil }
        var i = count - bytes.count
        while i >= 0 {
            if self[startIndex + i] == bytes[0] && self[startIndex + i + 1] == bytes[1] && self[startIndex + i + 2] == bytes[2] && self[startIndex + i + 3] == bytes[3] { return i }
            i -= 1
        }
        return nil
    }
}

/// Reads the central directory and extracts entries — local or remote, deflate or stored, ZIP64 aware.
final class ZipArchive {
    let source: ZipSource
    private(set) var entries: [ZipEntry] = []

    init(source: ZipSource) throws {
        self.source = source
        let tailLength = Int(min(source.length, 70_000))
        let tail = try source.read(at: source.length - UInt64(tailLength), count: tailLength)
        guard let end = tail.lastRange(of: [0x50, 0x4b, 0x05, 0x06]) else { throw ZipError.notZip }
        var directorySize = UInt64(tail.u32(end + 12)), directoryOffset = UInt64(tail.u32(end + 16))
        var total = Int(tail.u16(end + 10))
        if directoryOffset == 0xFFFF_FFFF || directorySize == 0xFFFF_FFFF || total == 0xFFFF,
           let end64 = tail.lastRange(of: [0x50, 0x4b, 0x06, 0x06]) {
            total = Int(tail.u64(end64 + 32))
            directorySize = tail.u64(end64 + 40)
            directoryOffset = tail.u64(end64 + 48)
        }
        let directory = try source.read(at: directoryOffset, count: Int(directorySize))
        var p = 0
        while p + 46 <= directory.count, directory.u32(p) == 0x0201_4b50, entries.count < max(total, 1) * 2 {
            let flags = directory.u16(p + 8), method = directory.u16(p + 10)
            var compressed = UInt64(directory.u32(p + 20)), size = UInt64(directory.u32(p + 24))
            let nameLength = Int(directory.u16(p + 28)), extraLength = Int(directory.u16(p + 30)), commentLength = Int(directory.u16(p + 32))
            var offset = UInt64(directory.u32(p + 42))
            let nameData = directory.subdata(in: (directory.startIndex + p + 46)..<(directory.startIndex + p + 46 + nameLength))
            // ZIP64 extra field: only the values that were 0xFFFFFFFF follow, in this order.
            var e = p + 46 + nameLength
            let extraEnd = e + extraLength
            while e + 4 <= extraEnd {
                let tag = directory.u16(e), length = Int(directory.u16(e + 2))
                if tag == 0x0001 {
                    var q = e + 4
                    if size == 0xFFFF_FFFF { size = directory.u64(q); q += 8 }
                    if compressed == 0xFFFF_FFFF { compressed = directory.u64(q); q += 8 }
                    if offset == 0xFFFF_FFFF { offset = directory.u64(q) }
                }
                e += 4 + length
            }
            entries.append(ZipEntry(name: Self.decodeName(nameData, utf8: flags & 0x0800 != 0), method: method,
                                    compressedSize: compressed, size: size, localHeaderOffset: offset))
            p += 46 + nameLength + extraLength + commentLength
        }
    }

    /// Names are UTF-8 when the flag says so; Russian Windows archives without it use CP866.
    private static func decodeName(_ data: Data, utf8: Bool) -> String {
        if utf8 || !data.contains(where: { $0 >= 0x80 }) { return String(decoding: data, as: UTF8.self) }
        if let text = String(data: data, encoding: .utf8) { return text }
        let cp866 = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.dosRussian.rawValue)))
        return String(data: data, encoding: cp866) ?? String(decoding: data, as: UTF8.self)
    }

    private func dataStart(of entry: ZipEntry) throws -> UInt64 {
        let header = try source.read(at: entry.localHeaderOffset, count: 30)
        guard header.count == 30, header.u32(0) == 0x0403_4b50 else { throw ZipError.truncated }
        return entry.localHeaderOffset + 30 + UInt64(header.u16(26)) + UInt64(header.u16(28))
    }

    /// Extracts one entry into memory (for small files such as an engine template).
    func data(of entry: ZipEntry) throws -> Data {
        var output = Data()
        try stream(entry) { output.append($0) }
        return output
    }

    /// Extracts one entry to a file, a megabyte at a time.
    func extract(_ entry: ZipEntry, to destination: URL) throws {
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        let out = try FileHandle(forWritingTo: destination)
        defer { try? out.close() }
        try stream(entry) { try out.write(contentsOf: $0) }
    }

    private func stream(_ entry: ZipEntry, _ sink: @escaping (Data) throws -> Void) throws {
        let start = try dataStart(of: entry)
        let chunk = 1 << 20
        var read: UInt64 = 0
        switch entry.method {
        case 0:
            while read < entry.compressedSize {
                let piece = try source.read(at: start + read, count: Int(min(UInt64(chunk), entry.compressedSize - read)))
                guard !piece.isEmpty else { throw ZipError.truncated }
                try sink(piece); read += UInt64(piece.count)
            }
        case 8:
            var sinkError: Error?
            let filter = try OutputFilter(.decompress, using: .zlib) { data in
                if let data, sinkError == nil { do { try sink(data) } catch { sinkError = error } }
            }
            while read < entry.compressedSize {
                let piece = try source.read(at: start + read, count: Int(min(UInt64(chunk), entry.compressedSize - read)))
                guard !piece.isEmpty else { throw ZipError.truncated }
                try filter.write(piece); read += UInt64(piece.count)
                if let sinkError { throw sinkError }
            }
            try filter.finalize()
            if let sinkError { throw sinkError }
        default:
            throw ZipError.unsupported("метод сжатия \(entry.method)")
        }
    }

    /// Extracts the whole archive into a folder, refusing paths that would escape it.
    func extractAll(to folder: URL, progress: ((Double) -> Void)? = nil) throws {
        let root = folder.standardizedFileURL.path
        let total = max(1, entries.reduce(0) { $0 + $1.size })
        var done: UInt64 = 0
        for entry in entries where !entry.isDirectory && !entry.name.hasPrefix("__MACOSX/") {
            let target = folder.appendingPathComponent(entry.name).standardizedFileURL
            guard target.path.hasPrefix(root + "/") else { continue }
            try extract(entry, to: target)
            done += entry.size
            progress?(Double(done) / Double(total))
        }
    }
}
