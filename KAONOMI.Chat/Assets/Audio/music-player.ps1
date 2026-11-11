# Audio player of Kaonomi Chat (/music) on Windows, driven through stdin / stdout like music-player.swift:
# commands load <path> | play | pause | stop | seek <seconds> | volume <0-1> | info <path> | quit;
# events ready | loaded <duration> | pos <current> <duration> <playing 0/1> <level> | ended | info <json> | error <message>.
$ErrorActionPreference = 'Stop'
$player = New-Object -ComObject WMPlayer.OCX
$player.settings.autoStart = $false
$player.settings.volume = 80
$reader = [Console]::In
$pending = $reader.ReadLineAsync()
$loaded = $false
$wasPlaying = $false
[Console]::Out.WriteLine('ready'); [Console]::Out.Flush()

function Write-Event([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }

while ($true) {
    if ($pending.IsCompleted) {
        $line = $pending.Result
        if ($null -eq $line) { break }
        $pending = $reader.ReadLineAsync()
        $command, $argument = $line -split ' ', 2
        try {
            switch ($command) {
                'load' {
                    $player.controls.stop()
                    $player.URL = $argument
                    $player.controls.play(); $player.controls.pause()
                    for ($i = 0; $i -lt 40 -and $player.currentMedia.duration -le 0; $i++) { Start-Sleep -Milliseconds 50 }
                    $loaded = $true
                    Write-Event ("loaded " + [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $player.currentMedia.duration))
                }
                'play' { $player.controls.play() }
                'pause' { $player.controls.pause() }
                'stop' { $player.controls.stop() }
                'seek' { $player.controls.currentPosition = [double]::Parse($argument, [Globalization.CultureInfo]::InvariantCulture) }
                'volume' { $player.settings.volume = [int]([double]::Parse($argument, [Globalization.CultureInfo]::InvariantCulture) * 100) }
                'info' {
                    $media = $player.newMedia($argument)
                    $info = @{ path = $argument; title = $media.getItemInfo('Title'); artist = $media.getItemInfo('Author');
                               albumName = $media.getItemInfo('WM/AlbumTitle'); duration = $media.duration }
                    Write-Event ('info ' + ($info | ConvertTo-Json -Compress))
                }
                'quit' { exit 0 }
            }
        } catch { Write-Event ('error ' + $_.Exception.Message) }
    }
    if ($loaded) {
        $playing = $player.playState -eq 3
        if ($wasPlaying -and $player.playState -eq 8) { Write-Event 'ended' }   # 8 = media ended
        $wasPlaying = $playing
        $culture = [Globalization.CultureInfo]::InvariantCulture
        Write-Event ([string]::Format($culture, 'pos {0} {1} {2} 0', $player.controls.currentPosition, $player.currentMedia.duration, [int]$playing))
    }
    Start-Sleep -Milliseconds 250
}
