param([string]$Repository = 'x3sh/razret', [string]$Dotnet = 'dotnet', [string]$EnginePath = '')
$ErrorActionPreference = 'Stop'
if ($Repository -and $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Use owner/repository.' }
[xml]$project = Get-Content "$PSScriptRoot/src/ZapretApp/ZapretApp.csproj"
$version = $project.Project.PropertyGroup.Version
$stage = Join-Path $PSScriptRoot "build/release-$version"
$portable = Join-Path $PSScriptRoot "dist/Razret-$version-win-x64"
if (Test-Path -LiteralPath $portable) { throw "Output already exists: $portable. Move it aside before rebuilding a release." }
if (!$EnginePath) {
    $archive = Join-Path $PSScriptRoot 'downloads/zapret-discord-youtube-1.10.3.zip'
    New-Item -ItemType Directory -Path (Split-Path $archive) -Force | Out-Null
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest 'https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip' -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne '244314AE1C24538A0D751601DA8E0C925C843371EEC4456EB15F14C4FD6B7058') { throw 'Upstream archive checksum mismatch.' }
    $extract = Join-Path $PSScriptRoot ('build/engine-' + [guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $roots = @(Get-ChildItem -LiteralPath $extract -Filter service.bat -File -Recurse)
    if ($roots.Count -ne 1) { throw 'Unexpected upstream archive layout.' }
    $EnginePath = $roots[0].DirectoryName
    Set-Content -LiteralPath (Join-Path $EnginePath 'version.txt') -Value '1.10.3' -Encoding utf8
}
& $Dotnet publish "$PSScriptRoot/src/ZapretApp/ZapretApp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:UpdateRepository=$Repository" -o $stage
if ($LASTEXITCODE) { throw 'Publish failed.' }
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -LiteralPath "$stage/Razret.exe" -Destination $portable -Force
Copy-Item -LiteralPath "$stage/Assets" -Destination $portable -Recurse -Force
if (!(Test-Path -LiteralPath "$portable/engine")) { Copy-Item -LiteralPath $EnginePath -Destination "$portable/engine" -Recurse }
Copy-Item -LiteralPath "$PSScriptRoot/THIRD-PARTY" -Destination $portable -Recurse -Force
@'
Razret

1. Распакуйте папку целиком и откройте Razret.exe.
2. Разрешите запуск и нажмите «Подобрать» рядом с заголовком «Стратегии».
3. В следующий раз приложение включит сохранённую стратегию.

Закрытие окна оставляет приложение в трее. Полный выход — через значок рядом с часами.
Новые версии: значок обновлений внизу окна. Настройки и списки сохраняются.
'@ | Set-Content -LiteralPath "$portable/Начать здесь.txt" -Encoding utf8
# Validate against a clean engine without writing test reports into the portable package.
$check = Join-Path $PSScriptRoot 'build/release-check'
& $Dotnet build "$PSScriptRoot/src/ZapretApp/ZapretApp.csproj" -c Release -o $check
if ($LASTEXITCODE) { throw 'Verification build failed.' }
if (!(Test-Path -LiteralPath "$check/engine")) { Copy-Item -LiteralPath $EnginePath -Destination "$check/engine" -Recurse }
# Exercise the published self-contained executable, including bundle decompression.
Copy-Item -LiteralPath "$stage/Razret.exe" -Destination "$check/Razret.exe" -Force
foreach ($verificationMode in @('--self-test', '--ui-test', '--statistics-test')) {
    $verificationProcess = Start-Process -FilePath "$check/Razret.exe" -ArgumentList $verificationMode -WorkingDirectory $check -WindowStyle Hidden -Wait -PassThru
    if ($verificationProcess.ExitCode -ne 0) { throw "Published executable verification failed: $verificationMode" }
}
Compress-Archive -LiteralPath "$stage/Razret.exe" -DestinationPath "$PSScriptRoot/dist/Razret-app-update-win-x64.zip" -Force
Compress-Archive -LiteralPath $portable -DestinationPath "$PSScriptRoot/dist/Razret-$version-win-x64.zip" -Force
Get-FileHash "$PSScriptRoot/dist/Razret-app-update-win-x64.zip", "$PSScriptRoot/dist/Razret-$version-win-x64.zip" | Format-Table
