# logo.svg → png/logo-N.png, ALEdit.ico (2026-11-02)
# Chrome headless로 512px을 그리고, 작은 크기는 System.Drawing으로 줄인다 (Chrome은 작은 창을 못 찍는다)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$chrome = if ($env:CHROME) { $env:CHROME } else { 'C:\Program Files\Google\Chrome\Application\chrome.exe' }
New-Item -ItemType Directory -Force png | Out-Null
$big = Join-Path $PWD 'png\logo-512.png'
$url = 'file:///' + (Join-Path $PWD 'logo.svg').Replace([char]92, '/')
$ErrorActionPreference = 'Continue'  # Chrome이 stderr에 쓰는 경고로 멈추지 않게
& $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --force-device-scale-factor=1 --window-size=512,512 "--screenshot=$big" $url 2>$null | Out-Null
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile($big)
foreach ($n in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.SmoothingMode = 'HighQuality'
    $g.DrawImage($src, 0, 0, $n, $n)
    $g.Dispose(); $bmp.Save((Join-Path $PWD "png\logo-$n.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$src.Dispose()
python -I make_ico.py png ALEdit.ico 16 24 32 48 64 128 256
