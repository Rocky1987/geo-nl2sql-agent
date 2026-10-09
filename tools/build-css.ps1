<#
.SYNOPSIS
  把 Web 專案的 Tailwind 樣式編譯成 wwwroot/css/app.css。
.DESCRIPTION
  使用 Tailwind 官方的獨立 CLI（不需要 Node）。第一次執行會把 CLI 下載到 tools/bin/（已被 .gitignore 忽略）。
  編譯結果 app.css 有入版控，所以只有修改 Views、wwwroot/js 或 Styles/app.css 的樣式時才需要重新編譯。
  用法：./tools/build-css.ps1
#>
$ErrorActionPreference = 'Stop'
$version = 'v4.3.3'
$bin = Join-Path $PSScriptRoot 'bin'
$exe = Join-Path $bin 'tailwindcss.exe'
if (-not (Test-Path $exe)) {
    New-Item -ItemType Directory -Force $bin | Out-Null
    Invoke-WebRequest "https://github.com/tailwindlabs/tailwindcss/releases/download/$version/tailwindcss-windows-x64.exe" -OutFile $exe
}
$web = Join-Path $PSScriptRoot '..\src\GeoNl2Sql.Web'
Push-Location $web
try { & $exe -i Styles/app.css -o wwwroot/css/app.css --minify } finally { Pop-Location }
