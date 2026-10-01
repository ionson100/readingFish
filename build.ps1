<#
.SYNOPSIS
    Сборка readingFish под Windows 64: exe для Windows и .deb для Linux.

.DESCRIPTION
    Собирает самодостаточные файлы, которым не нужен .NET на целевой машине,
    и пакует Linux-версию в .deb прямо здесь, на Windows.

    Для .deb не нужен dpkg-deb: архивы tar и контейнер ar пишутся вручную
    в этом же скрипте. Так сделано потому, что штатный bsdtar в Windows
    записывает права 0666 на все файлы, и установленный из такого пакета
    бинарник оказался бы не запускаемым.

.EXAMPLE
    .\build.ps1
    Собирает всё: exe для Windows, бинарник для Linux и .deb.

.EXAMPLE
    .\build.ps1 -Target deb
    Только пакет для Debian и Ubuntu.

.NOTES
    Нужен .NET 9 SDK. Собранные файлы самодостаточны: .NET на целевой
    машине не требуется.
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'windows', 'linux', 'deb')]
    [string]$Target = 'all',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$Project = Join-Path $Root 'readingFish.csproj'
$Dist = Join-Path $Root 'dist'
$Icon = Join-Path $Root 'image.png'

# Собираем и для Windows, и для Linux, если попросили пакет: пакет
# получается из Linux-версии.
$NeedWindows = $Target -in @('all', 'windows')
$NeedLinux = $Target -in @('all', 'linux', 'deb')
$NeedDeb = $Target -in @('all', 'deb')

function Write-Step($text) { Write-Host "==> $text" }
function Write-Info($text) { Write-Host "    $text" }

function Get-ProjectVersion {
    $text = [System.IO.File]::ReadAllText($Project)
    $m = [regex]::Match($text, '<Version>([^<]+)</Version>')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return '1.0.0'
}

# ---------------------------------------------------------------- dotnet

function Publish-SelfContained {
    param(
        [Parameter(Mandatory)] [string]$Runtime,
        [Parameter(Mandatory)] [string]$OutputDir
    )

    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

    Write-Info "dotnet publish -r $Runtime"
    & dotnet publish $Project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:GenerateDocumentationFile=false `
        -o $OutputDir

    if ($LASTEXITCODE -ne 0) { throw "Сборка для $Runtime не удалась." }
}

# ---------------------------------------------------------------- tar

function New-TarArchive {
    <#
    .SYNOPSIS
        Пишет uncompressed tar вручную. Причина — см. описание скрипта:
        bsdtar в Windows не умеет проставлять бит исполнения.
    .PARAMETER Entries
        Список @{ Path = 'usr/bin/readingFish'; Source = 'C:\...\file';
                   Mode = '0755'; Directory = $true; Symlink = '../lib/...' }.
        Для каталога и для ссылки Source не нужен.
    .PARAMETER OutFile
        Куда записать архив.
    #>
    param(
        [Parameter(Mandatory)] [object[]]$Entries,
        [Parameter(Mandatory)] [string]$OutFile
    )

    $stream = [System.IO.File]::Create($OutFile)
    try {
        $block = New-Object byte[] 512

        foreach ($entry in $Entries) {
            # Обращаемся к полям через индексатор: у каталогов и ссылок
            # ключа Source нет, а строгий режим превращает это в ошибку.
            $isDir = [bool]$entry['Directory']
            $isLink = [bool]$entry['Symlink']
            $mode = [string]$entry['Mode']

            $name = ([string]$entry['Path']).TrimEnd('/') + $(if ($isDir) { '/' } else { '' })

            if ($isDir -or $isLink) {
                $data = [byte[]]::new(0)
            }
            else {
                $source = [string]$entry['Source']
                if (-not $source) { throw "У записи $name не указан Source." }
                $data = [System.IO.File]::ReadAllBytes($source)
            }

            if ($name.Length -gt 100) {
                throw "Путь длиннее 100 символов, ustar не потянет: $name"
            }

            [Array]::Clear($block, 0, 512)

            Put-Ascii $block 0 100 $name
            Put-Ascii $block 100 8 $mode
            Put-Ascii $block 108 8 '0000000'
            Put-Ascii $block 116 8 '0000000'
            Put-Octal $block 124 12 $data.Length
            Put-Octal $block 136 12 1593542400               # фиксированное время
            Put-Ascii $block 148 8 '        '                # контрольная сумма, считаем ниже

            if ($isLink) {
                # Символическая ссылка: цель лежит в поле linkname, а не
                # в данных записи.
                Put-Ascii $block 156 1 '2'
                Put-Ascii $block 157 100 ([string]$entry['Symlink'])
            }
            elseif ($isDir) {
                Put-Ascii $block 156 1 '5'
            }
            else {
                Put-Ascii $block 156 1 '0'
            }

            Put-Ascii $block 257 6 "ustar`0"
            Put-Ascii $block 263 2 '00'
            Put-Ascii $block 265 32 'root'
            Put-Ascii $block 297 32 'root'

            $sum = 0
            foreach ($b in $block) { $sum += $b }

            # Контрольная сумма: шесть ОКТАЛЬНЫХ цифр, NUL, пробел.
            # Именно октальных: в десятичной записи tar не читается вовсе,
            # на этом и споткнулась первая сборка пакета.
            Put-Ascii $block 148 8 ([Convert]::ToString($sum, 8).PadLeft(6, '0'))
            $block[154] = 0
            $block[155] = 0x20

            $stream.Write($block, 0, 512)

            if ($data.Length -gt 0) {
                $stream.Write($data, 0, $data.Length)
                $tail = $data.Length % 512
                if ($tail -ne 0) {
                    $stream.Write([byte[]]::new(512 - $tail), 0, 512 - $tail)
                }
            }
        }

        # Две пустые записи означают конец архива.
        $stream.Write([byte[]]::new(1024), 0, 1024)
    }
    finally {
        $stream.Dispose()
    }
}

function Put-Ascii {
    param([byte[]]$Buffer, [int]$Offset, [int]$Length, [string]$Value)
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Value)
    if ($bytes.Length -gt $Length) { $bytes = $bytes[0..($Length - 1)] }
    [Array]::Copy($bytes, 0, $Buffer, $Offset, $bytes.Length)
}

function Put-Octal {
    param([byte[]]$Buffer, [int]$Offset, [int]$Length, [long]$Value)
    $text = [Convert]::ToString($Value, 8).PadLeft($Length - 1, '0')
    Put-Ascii $Buffer $Offset $Length ($text + "`0")
}

function Put-Padded {
    <#
    .SYNOPSIS
        Записывает поле фиксированной ширины, дополняя его ПРОБЕЛАМИ.
        Формат ar требует именно пробельного заполнения: при нулевом
        заполнении имя записи читается вместе с нулевым хвостом, и путь
        получается несуществующим.
    #>
    param([byte[]]$Buffer, [int]$Offset, [int]$Length, [string]$Value)

    # Заполняем пробелами целиком, иначе хвост останется нулевым, и поле
    # прочитается вместе с мусором.
    for ($i = 0; $i -lt $Length; $i++) { $Buffer[$Offset + $i] = 0x20 }

    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Value)
    if ($bytes.Length -gt $Length) { $bytes = $bytes[0..($Length - 1)] }
    [Array]::Copy($bytes, 0, $Buffer, $Offset, $bytes.Length)
}

# ---------------------------------------------------------------- ar

function New-ArArchive {
    <#
    .SYNOPSIS
        Собирает контейнер ar, которым и является .deb. dpkg-deb на Windows
        недоступен, поэтому формат пишется напрямую.
    #>
    param(
        [Parameter(Mandatory)] [object[]]$Members,
        [Parameter(Mandatory)] [string]$OutFile
    )

    $stream = [System.IO.File]::Create($OutFile)
    try {
        # Глобальный заголовок ar.
        $global = [System.Text.Encoding]::ASCII.GetBytes("!<arch>`n")
        $stream.Write($global, 0, $global.Length)

        foreach ($member in $Members) {
            $data = [System.IO.File]::ReadAllBytes($member.Path)
            $header = New-Object byte[] 60
            [Array]::Clear($header, 0, 60)

            # Имя: 16 байт, ДОПОЛНЕНО ПРОБЕЛАМИ. Слеша в конце нет —
            # именно так пишет сам dpkg. Заполнять нужно пробелами: при
            # нулевом заполнении имя читается вместе с хвостом, и путь
            # получается несуществующим.
            Put-Padded $header 0 16 $member.Name
            Put-Padded $header 16 12 '0'                      # mtime
            Put-Padded $header 28 6 '0'                       # uid
            Put-Padded $header 34 6 '0'                       # gid
            Put-Padded $header 40 8 '100644'                  # mode
            Put-Padded $header 48 10 ([string]$data.Length)
            $header[58] = 0x60                                # "`"
            $header[59] = 0x0A

            $stream.Write($header, 0, 60)
            $stream.Write($data, 0, $data.Length)

            # Записи выравниваются по чётной границе.
            if ($data.Length % 2 -ne 0) { $stream.WriteByte(0x0A) }
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Compress-ToGzip {
    param([string]$InFile, [string]$OutFile)
    $input = [System.IO.File]::OpenRead($InFile)
    $output = [System.IO.File]::Create($OutFile)
    try {
        # Без имени файла и метки времени: так gzip-блоки одинаковы при
        # каждой сборке одного и того же содержимого.
        $gzip = [System.IO.Compression.GZipStream]::new($output, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        try { $input.CopyTo($gzip) }
        finally { $gzip.Dispose() }
    }
    finally {
        $input.Dispose()
        $output.Dispose()
    }
}

function Get-TarField {
    # Числовое поле заголовка tar записано В ОКТАЛЬНОМ виде. Обычное
    # приведение к int даёт число в несколько раз больше настоящего,
    # и следующий заголовок ищется не там.
    param([byte[]]$Buffer, [int]$Offset, [int]$Length)

    $text = [System.Text.Encoding]::ASCII.GetString($Buffer, $Offset, $Length).TrimEnd([char]0, ' ')
    if ($text -eq '') { return [int64]0 }

    try { return [Convert]::ToInt64($text, 8) }
    catch { return [int64]0 }
}

function Get-Md5Hex {
    param([string]$Path)
    (Get-FileHash -Path $Path -Algorithm MD5).Hash.ToLowerInvariant()
}

function Write-ArMember {
    <#
    .SYNOPSIS
        Вырезает содержимое записи ar в отдельный файл. Срез PowerShell по
        массиву в десятки мегабайт не годится: он создаёт object[] вместо
        byte[] и на файле в 40 МБ это уже не проверка, а расход памяти.
    #>
    param(
        [Parameter(Mandatory)] [byte[]]$Bytes,
        [Parameter(Mandatory)] [int]$Offset,
        [Parameter(Mandatory)] [int]$Size,
        [Parameter(Mandatory)] [string]$To
    )

    $chunk = New-Object byte[] $Size
    [Array]::Copy($Bytes, $Offset, $chunk, 0, $Size)
    [System.IO.File]::WriteAllBytes($To, $chunk)
}

# ---------------------------------------------------------------- deb

function Build-Deb {
    param(
        [Parameter(Mandatory)] [string]$LinuxDir,
        [Parameter(Mandatory)] [string]$Version,
        [Parameter(Mandatory)] [string]$OutFile
    )

    if (-not (Test-Path $Icon)) {
        throw "Не найдена иконка для меню: $Icon"
    }

    $binary = Join-Path $LinuxDir 'readingFish'
    if (-not (Test-Path $binary)) {
        throw "Не найден бинарник Linux: $binary"
    }

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("readingfish-deb-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    try {
        # ---- ярлык в меню приложений ----
        # Exec указывает на ссылку в /usr/bin: путь не зависит от того, куда
        # пакет установит сам каталог.
        $desktopFile = Join-Path $work 'readingFish.desktop'
        # Пишем UTF-8 БЕЗ BOM. С BOM файл начинается с байтов EF BB BF, и
        # строка [Desktop Entry] для части оболочек перестаёт распознаваться:
        # ярлык просто не появляется в меню.
        $desktopText = @"
[Desktop Entry]
Type=Application
Name=readingFish
Name[ru]=readingFish
Comment=Text-to-speech reader for fish-speech
Comment[ru]=Озвучка текста через сервер fish-speech
Exec=/usr/bin/readingFish
Icon=readingfish
Terminal=false
Categories=AudioVideo;Audio;AudioVideoEditing;
StartupNotify=true
"@
        [System.IO.File]::WriteAllText($desktopFile, $desktopText, [System.Text.UTF8Encoding]::new($false))

        # ---- содержимое, которое попадёт в /usr ----
        $appFiles = @(
            @{ Local = $binary;       Path = 'usr/lib/readingFish/readingFish'; Mode = '0755' }
            @{ Local = $Icon;         Path = 'usr/share/icons/hicolor/512x512/apps/readingfish.png'; Mode = '0644' }
            @{ Local = $desktopFile;  Path = 'usr/share/applications/readingfish.desktop'; Mode = '0644' }
        )

        $installedKb = [int]([Math]::Ceiling((Get-Item $binary).Length / 1KB))
        $description = @(
            'Text-to-speech reader for fish-speech server'
            ' readingFish converts text files to speech using a fish-speech'
            ' server over HTTP and keeps reference voices in one place.'
        ) -join "`n"

        # ---- DEBIAN/control ----
        $controlText = @"
Package: readingfish
Version: $Version
Section: sound
Priority: optional
Architecture: amd64
Depends: libasound2 | libasound2t64, libgtk-3-0 | libgtk-3-0t64, libice6, libsm6, libx11-6
Installed-Size: $installedKb
Maintainer: readingFish
Description: $description
"@

        $controlFile = Join-Path $work 'control'
        Set-Content -Path $controlFile -Value $controlText -Encoding ascii -NoNewline

        # ---- md5sums ----
        $md5Lines = $appFiles | Where-Object { $_.Path -notlike 'usr/share/icons/*' } |
            ForEach-Object { '{0}  {1}' -f (Get-Md5Hex $_.Local), $_.Path }
        $md5File = Join-Path $work 'md5sums'
        Set-Content -Path $md5File -Value ($md5Lines -join "`n") -Encoding ascii -NoNewline

        # ---- maintainer scripts ----
        # Без них иконка и ярлык попадут в кэш только после перезагрузки
        # сессии, поэтому обновляем кэши явно.
        $cacheScript = @'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
'@

        $postinst = Join-Path $work 'postinst'
        Set-Content -Path $postinst -Value $cacheScript -Encoding ascii -NoNewline

        $postrm = Join-Path $work 'postrm'
        Set-Content -Path $postrm -Value $cacheScript -Encoding ascii -NoNewline

        $prerm = Join-Path $work 'prerm'
        Set-Content -Path $prerm -Value $cacheScript -Encoding ascii -NoNewline

        # ---- data.tar: каталоги и файлы ----
        $dataEntries = @()
        foreach ($dir in @('usr', 'usr/lib', 'usr/lib/readingFish', 'usr/bin', 'usr/share',
                            'usr/share/applications', 'usr/share/icons',
                            'usr/share/icons/hicolor', 'usr/share/icons/hicolor/512x512',
                            'usr/share/icons/hicolor/512x512/apps')) {
            $dataEntries += @{ Path = $dir; Directory = $true; Mode = '0755' }
        }

        foreach ($file in $appFiles) {
            $dataEntries += @{ Path = $file.Path; Source = $file.Local; Mode = $file.Mode; Directory = $false }
        }

        # Запускной файл в /usr/bin — символическая ссылка, а не скрипт:
        # обёртка на sh потребовала бы бита исполнения, который в этом
        # пакете мы задаём вручную только для файлов, а не для ссылок.
        $dataEntries += @{ Path = 'usr/bin/readingFish'; Mode = '0777'; Symlink = '../lib/readingFish/readingFish' }

        $dataTar = Join-Path $work 'data.tar'
        New-TarArchive -Entries $dataEntries -OutFile $dataTar

        $dataTarGz = Join-Path $work 'data.tar.gz'
        Compress-ToGzip -InFile $dataTar -OutFile $dataTarGz

        # ---- control.tar ----
        $controlEntries = @(
            @{ Path = 'control';   Source = $controlFile; Mode = '0644' }
            @{ Path = 'md5sums';   Source = $md5File;    Mode = '0644' }
            @{ Path = 'postinst';  Source = $postinst;   Mode = '0755' }
            @{ Path = 'prerm';     Source = $prerm;      Mode = '0755' }
            @{ Path = 'postrm';    Source = $postrm;     Mode = '0755' }
        )

        $controlTar = Join-Path $work 'control.tar'
        New-TarArchive -Entries $controlEntries -OutFile $controlTar

        $controlTarGz = Join-Path $work 'control.tar.gz'
        Compress-ToGzip -InFile $controlTar -OutFile $controlTarGz

        # ---- debian-binary ----
        $debianBinary = Join-Path $work 'debian-binary'
        Set-Content -Path $debianBinary -Value '2.0' -Encoding ascii -NoNewline

        # ---- контейнер ar ----
        $members = @(
            @{ Name = 'debian-binary';  Path = $debianBinary }
            @{ Name = 'control.tar.gz'; Path = $controlTarGz }
            @{ Name = 'data.tar.gz';    Path = $dataTarGz }
        )

        New-ArArchive -Members $members -OutFile $OutFile
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- проверка

function Test-Deb {
    <#
    .SYNOPSIS
        Разбирает готовый .deb обратно и проверяет, что он пригоден:
        верный порядок записей ar, наличие control, права на бинарник.
    #>
    param([Parameter(Mandatory)] [string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $problems = @()

    $magic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 8)
    if ($magic -ne "!<arch>`n") { $problems += "нет глобального заголовка ar" }

    $offset = 8
    $names = @()
    $modes = @{}

    while ($offset + 60 -le $bytes.Length) {
        $name = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 16).TrimEnd(' ', [char]0)
        $mode = [System.Text.Encoding]::ASCII.GetString($bytes, $offset + 40, 8).Trim()
        $sizeText = [System.Text.Encoding]::ASCII.GetString($bytes, $offset + 48, 10).Trim()

        if (-not [int]::TryParse($sizeText, [ref]$null)) { $problems += "не разобран размер записи $name"; break }
        $size = [int]$sizeText

        $names += $name
        $modes[$name] = $mode
        $offset += 60 + $size
        if ($size % 2 -ne 0) { $offset++ }
    }

    $expected = @('debian-binary', 'control.tar.gz', 'data.tar.gz')
    if (($names -join ',') -ne ($expected -join ',')) {
        $problems += "состав записей неверный: $($names -join ', ')"
    }

    # Содержимое data.tar.gz: распаковываем во временный каталог и читаем
    # заголовки tar, чтобы убедиться в правах и именах.
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("readingfish-debcheck-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    try {
        $dataGz = Join-Path $work 'data.tar.gz'
        $controlGz = Join-Path $work 'control.tar.gz'

        # Вырезаем нужные записи из ar.
        $offset = 8
        while ($offset + 60 -le $bytes.Length) {
            $name = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 16).TrimEnd(' ', [char]0)
            $size = [int]([System.Text.Encoding]::ASCII.GetString($bytes, $offset + 48, 10).Trim())
            $start = $offset + 60

            if ($name -eq 'data.tar.gz') { Write-ArMember -Bytes $bytes -Offset $start -Size $size -To $dataGz }
            if ($name -eq 'control.tar.gz') { Write-ArMember -Bytes $bytes -Offset $start -Size $size -To $controlGz }

            $offset = $start + $size
            if ($size % 2 -ne 0) { $offset++ }
        }

        # Распаковываем обе части. Имена здесь относительные: выше уже
        # собраны полные пути, и повторный Join-Path дал бы несуществующий.
        foreach ($pair in @(@('data.tar.gz', 'data.tar'), @('control.tar.gz', 'control.tar'))) {
            $gzPath = Join-Path $work $pair[0]
            $tarPath = Join-Path $work $pair[1]

            if (-not (Test-Path $gzPath)) { $problems += "в пакете нет записи $($pair[0])" ; continue }

            $source = [System.IO.File]::OpenRead($gzPath)
            $target = [System.IO.File]::Create($tarPath)
            try {
                $zs = [System.IO.Compression.GZipStream]::new($source, [System.IO.Compression.CompressionMode]::Decompress)
                try { $zs.CopyTo($target) }
                finally { $zs.Dispose() }
            }
            finally {
                $source.Dispose()
                $target.Dispose()
            }
        }

        # Права бинарника и наличие ярлыка с иконкой внутри data.tar.
        $dataPath = Join-Path $work 'data.tar'
        if (-not (Test-Path $dataPath)) {
            $problems += 'data.tar не распаковался'
        }
        else {
            $data = [System.IO.File]::ReadAllBytes($dataPath)
            $pos = 0
            $foundBinary = $false
            $foundDesktop = $false
            $foundIcon = $false

            while ($pos + 512 -le $data.Length) {
                $name = [System.Text.Encoding]::ASCII.GetString($data, $pos, 100).TrimEnd([char]0)
                if ($name -eq '') { break }

                $mode = [System.Text.Encoding]::ASCII.GetString($data, $pos + 100, 8).TrimEnd([char]0, ' ')
                $size = Get-TarField -Buffer $data -Offset ($pos + 124) -Length 12

                if ($name -eq 'usr/lib/readingFish/readingFish') {
                    $foundBinary = $true
                    if ($mode -ne '0755') { $problems += "у бинарника права $mode, а не 0755" }
                    if ($size -lt 1MB) { $problems += "бинарник подозрительно мал: $size байт" }
                }

                if ($name -eq 'usr/share/applications/readingfish.desktop') { $foundDesktop = $true }
                if ($name -eq 'usr/share/icons/hicolor/512x512/apps/readingfish.png') { $foundIcon = $true }

                $pos += 512 + [Math]::Ceiling($size / 512) * 512
            }

            if (-not $foundBinary) { $problems += 'в data.tar нет бинарника' }
            if (-not $foundDesktop) { $problems += 'в data.tar нет ярлыка для меню приложений' }
            if (-not $foundIcon) { $problems += 'в data.tar нет иконки' }
        }

        # Наличие control и его обязательных полей.
        $controlPath = Join-Path $work 'control.tar'
        $controlTarBytes = if (Test-Path $controlPath) { [System.IO.File]::ReadAllBytes($controlPath) } else { [byte[]]::new(0) }
        $controlText = $null
        $pos = 0
        while ($pos + 512 -le $controlTarBytes.Length) {
            $name = [System.Text.Encoding]::ASCII.GetString($controlTarBytes, $pos, 100).TrimEnd([char]0)
            if ($name -eq '') { break }

            $size = Get-TarField -Buffer $controlTarBytes -Offset ($pos + 124) -Length 12
            if ($name -eq 'control') {
                $controlText = [System.Text.Encoding]::ASCII.GetString($controlTarBytes, $pos + 512, [int]$size)
            }

            $pos += 512 + [Math]::Ceiling($size / 512) * 512
        }

        if (-not $controlText) { $problems += 'в control.tar нет control' }
        else {
            foreach ($field in @('Package:', 'Version:', 'Architecture:', 'Description:')) {
                if ($controlText -notmatch "(?m)^$([regex]::Escape($field))") { $problems += "в control нет поля $field" }
            }
        }
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($problems.Count -eq 0) {
        Write-Host '    проверка пакета: записи ar, control, права 0755, ярлык и иконка — верно' -ForegroundColor Green
        return $true
    }

    foreach ($p in $problems) { Write-Host "    ОШИБКА: $p" -ForegroundColor Red }
    return $false
}

# ---------------------------------------------------------------- main

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Не найден dotnet. Установите .NET 9 SDK.'
}

$version = Get-ProjectVersion
Write-Step "readingFish $version, конфигурация $Configuration"

$winDir = Join-Path $Dist 'win-x64'
$linuxDir = Join-Path $Dist 'linux-x64'

if ($NeedWindows) {
    Write-Step 'Windows x64: автономный exe'
    Publish-SelfContained -Runtime 'win-x64' -OutputDir $winDir
    $exe = Join-Path $winDir 'readingFish.exe'
    if (Test-Path $exe) {
        Write-Info ("readingFish.exe, {0:N1} МБ" -f ((Get-Item $exe).Length / 1MB))
    }
}

if ($NeedLinux) {
    Write-Step 'Linux x64: автономный бинарник'
    Publish-SelfContained -Runtime 'linux-x64' -OutputDir $linuxDir
    $bin = Join-Path $linuxDir 'readingFish'
    if (Test-Path $bin) {
        $head = [System.IO.File]::ReadAllBytes($bin)[0..3]
        $isElf = $head[0] -eq 0x7F -and $head[1] -eq 0x45 -and $head[2] -eq 0x4C -and $head[3] -eq 0x46
        Write-Info ("readingFish, {0:N1} МБ, ELF: {1}" -f ((Get-Item $bin).Length / 1MB), $isElf)
        if (-not $isElf) { throw 'Собранный файл не похож на ELF, что-то пошло не так.' }
    }
}

if ($NeedDeb) {
    Write-Step 'Пакет .deb для Debian и Ubuntu'
    $deb = Join-Path $Dist "readingfish_${version}_amd64.deb"

    Build-Deb -LinuxDir $linuxDir -Version $version -OutFile $deb

    $size = (Get-Item $deb).Length
    Write-Info ("{0} ({1:N1} МБ)" -f (Split-Path $deb -Leaf), ($size / 1MB))

    if (-not (Test-Deb -Path $deb)) {
        throw 'Пакет собран, но проверка его содержимого не пройдена.'
    }
}

Write-Step 'Готово'
Get-ChildItem $Dist -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'deb' } |
    ForEach-Object {
        $note = if ($_.PSIsContainer) { 'каталог' } else { '{0:N1} МБ' -f ($_.Length / 1MB) }
        Write-Host ("    {0,-34} {1}" -f $_.Name, $note)
    }

Write-Host ''
Write-Info "Готовое приложение не требует .NET на целевой машине."
