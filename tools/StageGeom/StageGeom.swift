// StageGeom — dump per-window geometry + window-server transforms on macOS.
//
// Purpose: measure the EXACT transform macOS Stage Manager applies to tray
// cards, so StageManagerForWindows can copy the numbers 1:1 instead of
// eyeballing screenshots.
//
// Build (on the Mac):
//   swiftc StageGeom.swift -o stagegeom
// Run:
//   ./stagegeom            # one-shot dump of all windows
//   ./stagegeom --json     # machine-readable dump
//   ./stagegeom --watch    # redump every 2s (rearrange scenes between dumps)
//
// Notes:
// - Uses private SkyLight (CGS) API SLSGetWindowTransform. Read-only; no SIP
//   change needed. If the symbol lookup fails on a newer macOS, the tool still
//   prints window bounds (axis-aligned) from the public CGWindowList API.
// - Grant Terminal "Screen Recording" permission if window TITLES show empty —
//   geometry works without it.

import CoreGraphics
import Foundation

// MARK: - SkyLight private API (resolved at runtime)

typealias MainConnectionFn = @convention(c) () -> Int32
typealias GetTransformFn = @convention(c) (Int32, UInt32, UnsafeMutablePointer<CGAffineTransform>) -> Int32

let skyLight = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW)

func sym<T>(_ name: String, _ type: T.Type) -> T? {
    guard let handle = skyLight, let ptr = dlsym(handle, name) else { return nil }
    return unsafeBitCast(ptr, to: T.self)
}

let mainConnection = sym("SLSMainConnectionID", MainConnectionFn.self)
    ?? sym("CGSMainConnectionID", MainConnectionFn.self)
let getTransform = sym("SLSGetWindowTransform", GetTransformFn.self)
    ?? sym("CGSGetWindowTransform", GetTransformFn.self)

// MARK: - Decomposition

struct EdgeInfo {
    let topDeg: Double      // + = right end rises (screen y grows down, hence sign flip)
    let leftDeg: Double     // + = bottom end drifts right
    let scaleX: Double
    let scaleY: Double
}

/// Affine maps unit-x to (a,b) and unit-y to (c,d); edges of the mapped rect
/// stay parallel (affine invariant), so one angle per axis fully describes it.
func decompose(_ t: CGAffineTransform) -> EdgeInfo {
    let topDeg = -atan2(Double(t.b), Double(t.a)) * 180.0 / .pi
    let leftDeg = atan2(Double(t.c), Double(t.d)) * 180.0 / .pi
    let sx = hypot(Double(t.a), Double(t.b))
    let det = Double(t.a * t.d - t.b * t.c)
    let sy = sx == 0 ? 0 : det / sx
    return EdgeInfo(topDeg: topDeg, leftDeg: leftDeg, scaleX: sx, scaleY: sy)
}

func isIdentity(_ t: CGAffineTransform, eps: CGFloat = 1e-6) -> Bool {
    abs(t.a - 1) < eps && abs(t.b) < eps && abs(t.c) < eps
        && abs(t.d - 1) < eps && abs(t.tx) < eps && abs(t.ty) < eps
}

// MARK: - Dump

struct WindowRecord: Codable {
    let wid: UInt32
    let owner: String
    let title: String
    let layer: Int
    let bounds: [Double] // x, y, w, h
    let onScreen: Bool
    let transform: [Double]? // a b c d tx ty (nil if unavailable)
    let topEdgeDeg: Double?
    let leftEdgeDeg: Double?
    let scaleX: Double?
    let scaleY: Double?
}

func snapshot() -> [WindowRecord] {
    let cid = mainConnection?() ?? 0
    let options: CGWindowListOption = [.optionAll]
    guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
        return []
    }

    return list.compactMap { info in
        guard let wid = info[kCGWindowNumber as String] as? UInt32 else { return nil }
        let owner = info[kCGWindowOwnerName as String] as? String ?? "?"
        let title = info[kCGWindowName as String] as? String ?? ""
        let layer = info[kCGWindowLayer as String] as? Int ?? 0
        let onScreen = (info[kCGWindowIsOnscreen as String] as? Bool) ?? false
        var rect = [0.0, 0.0, 0.0, 0.0]
        if let b = info[kCGWindowBounds as String] as? [String: Double] {
            rect = [b["X"] ?? 0, b["Y"] ?? 0, b["Width"] ?? 0, b["Height"] ?? 0]
        }

        var matrix: [Double]? = nil
        var edges: EdgeInfo? = nil
        if let getTransform, cid != 0 {
            var t = CGAffineTransform.identity
            if getTransform(cid, wid, &t) == 0, !isIdentity(t) {
                matrix = [t.a, t.b, t.c, t.d, t.tx, t.ty].map(Double.init)
                edges = decompose(t)
            }
        }

        return WindowRecord(
            wid: wid, owner: owner, title: title, layer: layer,
            bounds: rect, onScreen: onScreen,
            transform: matrix,
            topEdgeDeg: edges?.topDeg, leftEdgeDeg: edges?.leftDeg,
            scaleX: edges?.scaleX, scaleY: edges?.scaleY)
    }
}

func printHuman(_ records: [WindowRecord]) {
    let interesting = records.filter { $0.transform != nil }
    let wm = records.filter { $0.owner.contains("WindowManager") || $0.owner.contains("Dock") }

    print("=== Windows with non-identity server transform (\(interesting.count)) ===")
    for r in interesting {
        let e = String(
            format: "top %+.3f°  left %+.3f°  scale %.4f x %.4f",
            r.topEdgeDeg ?? 0, r.leftEdgeDeg ?? 0, r.scaleX ?? 0, r.scaleY ?? 0)
        let m = (r.transform ?? []).map { String(format: "%.5f", $0) }.joined(separator: " ")
        print("wid=\(r.wid) [\(r.owner)] '\(r.title)'")
        print("    \(e)")
        print("    matrix [a b c d tx ty] = \(m)")
        print("    bounds = \(r.bounds)")
    }

    print("\n=== WindowManager/Dock-owned windows (\(wm.count)) — possible proxy tiles ===")
    for r in wm where r.onScreen {
        print(String(
            format: "wid=%d layer=%d bounds=(%.1f, %.1f, %.1f x %.1f) '%@'",
            r.wid, r.layer, r.bounds[0], r.bounds[1], r.bounds[2], r.bounds[3], r.title))
    }

    if getTransform == nil {
        print("\n!! SLSGetWindowTransform not found — transforms unavailable, bounds only.")
    }
}

// MARK: - Main

let args = CommandLine.arguments
let json = args.contains("--json")
let watch = args.contains("--watch")

repeat {
    let records = snapshot()
    if json {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? enc.encode(records) {
            print(String(data: data, encoding: .utf8) ?? "")
        }
    } else {
        print("\n──── \(Date()) ────")
        printHuman(records)
    }
    if watch { Thread.sleep(forTimeInterval: 2.0) }
} while watch
