import AppKit
import CoreGraphics
import Foundation

// An independent observer for our one CI-created app. It never clicks UI,
// launches games, relaxes Gatekeeper, or terminates apps by a shared name.
@main
@MainActor
struct LauncherObserver {
    static func canonical(_ url: URL) -> String {
        url.standardizedFileURL.resolvingSymlinksInPath().path
    }

    static func snapshot(_ path: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: path),
              let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return value
    }

    static func pump(_ seconds: Double) {
        RunLoop.current.run(until: Date().addingTimeInterval(seconds))
    }

    static func windows(_ pid: pid_t) -> [[String: Any]] {
        guard let values = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return [] }
        return values.compactMap { value in
            guard (value[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == pid,
                  (value[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  let bounds = value[kCGWindowBounds as String] as? [String: Any],
                  let width = (bounds["Width"] as? NSNumber)?.doubleValue,
                  let height = (bounds["Height"] as? NSNumber)?.doubleValue,
                  width >= 800, height >= 500 else { return nil }
            return ["windowId": (value[kCGWindowNumber as String] as? NSNumber)?.uint32Value ?? 0,
                    "width": width, "height": height]
        }
    }

    static func main() {
        guard CommandLine.arguments.count == 5 else {
            fputs("usage: observer APP_BUNDLE REPORT STARTUP_REPORT EXPECTED_VERSION\n", stderr)
            exit(2)
        }
        let bundle = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let destination = URL(fileURLWithPath: CommandLine.arguments[2])
        let startupPath = URL(fileURLWithPath: CommandLine.arguments[3])
        let expectedVersion = CommandLine.arguments[4]
        let bundlePath = canonical(bundle)
        let basePath = canonical(bundle.appendingPathComponent("Contents/MacOS", isDirectory: true))
        let identifier = "io.dustore.launcher.v"
        let previous = snapshot(startupPath)?["launchId"] as? String
        let previousPids = Set(NSWorkspace.shared.runningApplications.map { $0.processIdentifier })
        let start = Date()
        let open = Process()
        open.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        // Match a normal Finder launch: no --args, smoke flags, stdout override,
        // profile override, or application-specific environment variables.
        open.arguments = ["-n", "-W", bundle.path]
        var environment = ProcessInfo.processInfo.environment
        for key in Array(environment.keys) where key.hasPrefix("DUSTOREV_") || key == "DOTNET_ROOT" || key.hasPrefix("DOTNET_ROOT_") {
            environment.removeValue(forKey: key)
        }
        open.environment = environment
        var owned: NSRunningApplication?
        var latest: [String: Any]?
        var samples: [[String: Any]] = []
        var stableSince: Date?
        var firstLaunchId: String?
        var passed = false
        var failure = "The app never produced a ready normal startup with a persistent onscreen window."
        do {
            try open.run()
            while Date().timeIntervalSince(start) < 60 {
                pump(0.5)
                if owned == nil {
                    owned = NSWorkspace.shared.runningApplications.first { application in
                        guard !previousPids.contains(application.processIdentifier),
                              application.bundleIdentifier == identifier,
                              let url = application.bundleURL else { return false }
                        return canonical(url) == bundlePath
                    }
                }
                latest = snapshot(startupPath)
                if let application = owned {
                    let pid = application.processIdentifier
                    if application.isTerminated {
                        failure = "The exact LaunchServices app process exited before persistent-window verification completed."
                        break
                    }
                    let visible = windows(pid)
                    let ready = latest?["status"] as? String == "ready"
                    let reportPid = (latest?["processId"] as? NSNumber)?.int32Value
                    let launchId = latest?["launchId"] as? String
                    let reportBase = latest?["applicationBaseDirectory"] as? String
                    let version = latest?["version"] as? String ?? ""
                    let profile = latest?["profileDirectory"] as? String
                    let expectedProfile = canonical(FileManager.default.homeDirectoryForCurrentUser
                        .appendingPathComponent("Library/Application Support/DUSTORE Launcher V", isDirectory: true))
                    let reportMatches = reportPid == pid && launchId != nil && launchId != previous
                        && reportBase.map { canonical(URL(fileURLWithPath: $0)) == basePath } == true
                    if reportMatches && latest?["status"] as? String == "failed" {
                        failure = "Normal app startup reported failure: " + String(describing: latest?["error"] ?? "unknown")
                        break
                    }
                    if ready && reportMatches && (version == expectedVersion || version.hasPrefix(expectedVersion + "."))
                        && latest?["mode"] as? String == "normal"
                        && latest?["libraryInitializationSucceeded"] as? Bool == true
                        && latest?["windowVisible"] as? Bool == true
                        && profile.map({ canonical(URL(fileURLWithPath: $0)) == expectedProfile }) == true
                        && ((latest?["clientWidth"] as? NSNumber)?.doubleValue ?? 0) >= 800
                        && ((latest?["clientHeight"] as? NSNumber)?.doubleValue ?? 0) >= 500
                        && !visible.isEmpty {
                        if stableSince == nil { stableSince = Date(); firstLaunchId = launchId }
                        if launchId != firstLaunchId {
                            failure = "The process restarted during normal startup observation."
                            break
                        }
                        samples.append(["elapsedSeconds": Date().timeIntervalSince(start), "processId": pid,
                                        "launchId": launchId!, "windows": visible])
                        if Date().timeIntervalSince(stableSince!) >= 10 && samples.count >= 3 {
                            passed = true
                            break
                        }
                    } else {
                        stableSince = nil
                        samples.removeAll()
                    }
                } else if !open.isRunning && open.terminationStatus != 0 {
                    failure = "LaunchServices open failed with exit code \(open.terminationStatus)."
                    break
                }
            }
        } catch {
            failure = "Could not start LaunchServices: \(error)"
        }
        var result: [String: Any] = ["status": passed ? "passed" : "failed", "route": "LaunchServices open -n -W",
            "bundlePath": bundlePath, "bundleIdentifier": identifier, "defaultArguments": true,
            "defaultProfile": true, "smokeBranch": false, "stdoutOverridden": false,
            "startedAtUtc": ISO8601DateFormatter().string(from: start),
            "elapsedSeconds": Date().timeIntervalSince(start), "persistentWindowSecondsRequired": 10,
            "samples": samples, "startupReportPath": startupPath.path,
            "scope": "Native process and real onscreen-window observation. No interactive clicks or games are executed."]
        if let application = owned { result["processId"] = application.processIdentifier }
        if let latest = latest { result["startupReport"] = latest }
        if !passed { result["error"] = failure }
        // Only terminate the exact new app instance we identified by bundle URL.
        if let application = owned, !application.isTerminated,
           application.bundleURL.map({ canonical($0) == bundlePath }) == true {
            result["normalTerminationRequested"] = application.terminate()
            let until = Date().addingTimeInterval(8)
            while !application.isTerminated && Date() < until { pump(0.2) }
            if !application.isTerminated { result["ownedForceTerminationRequested"] = application.forceTerminate() }
            result["ownedAppTerminated"] = application.isTerminated
        }
        let waitUntil = Date().addingTimeInterval(5)
        while open.isRunning && Date() < waitUntil { pump(0.2) }
        if open.isRunning { open.terminate() }
        if !open.isRunning { result["openExitCode"] = open.terminationStatus }
        do {
            let data = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: destination, options: .atomic)
            print(String(data: data, encoding: .utf8) ?? "")
        } catch {
            fputs("Could not write LaunchServices observation: \(error)\n", stderr)
            exit(1)
        }
        exit(passed ? 0 : 1)
    }
}
