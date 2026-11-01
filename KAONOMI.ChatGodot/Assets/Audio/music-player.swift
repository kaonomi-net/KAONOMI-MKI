// Audio player of Kaonomi Chat (/music), driven through stdin / stdout, one command or event per line.
import AVFoundation
import Foundation

setvbuf(stdout, nil, _IOLBF, 0)

final class Events: NSObject, AVAudioPlayerDelegate {
    func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) { print("ended") }
    func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) { print("error \(error?.localizedDescription ?? "decode error")") }
}

let events = Events()
var player: AVAudioPlayer?
var volume: Float = 0.8

func json(_ value: Any) -> String {
    guard let data = try? JSONSerialization.data(withJSONObject: value), let text = String(data: data, encoding: .utf8) else { return "{}" }
    return text
}

// Title, artist, album and cover (written to a PNG / JPEG file) of a track.
func info(_ path: String) {
    let asset = AVURLAsset(url: URL(fileURLWithPath: path))
    var result: [String: Any] = ["path": path]
    for item in asset.commonMetadata {
        guard let key = item.commonKey?.rawValue else { continue }
        if key == "artwork", let data = item.dataValue {
            let file = NSTemporaryDirectory() + "kaonomi-cover-" + String(abs(path.hashValue)) + ".img"
            if (try? data.write(to: URL(fileURLWithPath: file))) != nil { result["artwork"] = file }
        } else if let text = item.stringValue {
            result[key] = text
        }
    }
    result["duration"] = CMTimeGetSeconds(asset.duration)
    print("info " + json(result))
}

func handle(_ line: String) {
    let parts = line.split(separator: " ", maxSplits: 1).map(String.init)
    guard let command = parts.first else { return }
    let argument = parts.count > 1 ? parts[1] : ""
    switch command {
    case "load":
        player?.stop()
        do {
            let next = try AVAudioPlayer(contentsOf: URL(fileURLWithPath: argument))
            next.delegate = events
            next.isMeteringEnabled = true
            next.volume = volume
            next.prepareToPlay()
            player = next
            print("loaded \(next.duration)")
        } catch {
            player = nil
            print("error \(error.localizedDescription)")
        }
    case "play": player?.play()
    case "pause": player?.pause()
    case "stop": player?.stop(); player?.currentTime = 0
    case "seek": if let time = Double(argument) { player?.currentTime = max(0, time) }
    case "volume": if let value = Float(argument) { volume = max(0, min(1, value)); player?.volume = volume }
    case "info": info(argument)
    case "quit": exit(0)
    default: print("error unknown command \(command)")
    }
}

DispatchQueue.global().async {
    while let line = readLine() {
        DispatchQueue.main.async { handle(line) }
    }
    DispatchQueue.main.async { exit(0) }   // the chat has closed
}

// Position, duration, state and loudness (0 to 1) four times a second.
Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { _ in
    guard let current = player else { return }
    var level = 0.0
    if current.isPlaying {
        current.updateMeters()
        let channels = max(1, current.numberOfChannels)
        let power = (0..<channels).map { Double(current.averagePower(forChannel: $0)) }.reduce(0, +) / Double(channels)
        level = max(0, min(1, pow(10, power / 20) * 1.6))
    }
    print("pos \(current.currentTime) \(current.duration) \(current.isPlaying ? 1 : 0) \(level)")
}
print("ready")
RunLoop.main.run()
