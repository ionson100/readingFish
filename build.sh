#!/usr/bin/env bash
# Сборка автономных дистрибутивов readingFish: Linux (один файл) и Windows (exe),
# плюс .deb-пакет для Debian/Ubuntu.
#
#   ./build.sh              — всё вместе
#   ./build.sh linux        — только linux
#   ./build.sh windows      — только windows
#   ./build.sh deb          — только .deb
#
# Требуется установленный .NET 9 SDK. Под Windows тот же результат даёт
# build.ps1, он собирает .deb без dpkg-deb.
#
# Содержимое пакета держим тем же, что и build.ps1: тот же путь установки,
# та же иконка, та же ссылка в /usr/bin.

set -euo pipefail

cd "$(dirname "$0")"

VERSION=$(grep -oP '(?<=<Version>)[^<]+' readingFish.csproj | head -1)
VERSION="${VERSION:-1.0.0}"
DIST="dist"
ICON="image.png"

echo "==> Сборка readingFish $VERSION"

build_linux() {
    echo "--> Linux x64 (автономный, один файл)"
    rm -rf "$DIST/linux-x64"
    dotnet publish -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none \
        -o "$DIST/linux-x64"
    chmod +x "$DIST/linux-x64/readingFish"
}

build_windows() {
    echo "--> Windows x64 (автономный exe)"
    rm -rf "$DIST/win-x64"
    dotnet publish -c Release -r win-x64 --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=none \
        -o "$DIST/win-x64"
}

build_deb() {
    echo "--> Debian/Ubuntu пакет"
    build_linux

    if [ ! -f "$ICON" ]; then
        echo "ОШИБКА: не найдена иконка $ICON" >&2
        exit 1
    fi

    local stage="$DIST/deb"
    rm -rf "$stage"
    mkdir -p "$stage/DEBIAN"
    mkdir -p "$stage/usr/lib/readingFish"
    mkdir -p "$stage/usr/bin"
    mkdir -p "$stage/usr/share/applications"
    mkdir -p "$stage/usr/share/icons/hicolor/512x512/apps"

    cp "$DIST/linux-x64/readingFish" "$stage/usr/lib/readingFish/readingFish"
    cp "$ICON" "$stage/usr/share/icons/hicolor/512x512/apps/readingfish.png"

    # Ссылка вместо обёртки на sh: ровно как в build.ps1.
    ln -s ../lib/readingFish/readingFish "$stage/usr/bin/readingFish"

    # Ярлык в меню приложений. Exec указывает на ссылку в /usr/bin.
    cat > "$stage/usr/share/applications/readingfish.desktop" <<'DESKTOP'
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
DESKTOP

    # Скрипты после установки: обновляем кэш ярлыков и иконок, иначе они
    # подхватятся только после перезахода в сессию.
    cat > "$stage/DEBIAN/postinst" <<'MAINTAINER'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
MAINTAINER
    cp "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm"
    cp "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm"
    chmod +x "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/DEBIAN/postrm"

    # control-файл
    local installed=$(du -k "$stage/usr/lib/readingFish/readingFish" | cut -f1)
    cat > "$stage/DEBIAN/control" <<CONTROL
Package: readingfish
Version: $VERSION
Section: sound
Priority: optional
Architecture: amd64
Depends: libasound2 | libasound2t64, libgtk-3-0 | libgtk-3-0t64, libice6, libsm6, libx11-6
Installed-Size: $installed
Maintainer: readingFish
Description: Text-to-speech reader for fish-speech server
 readingFish converts text files to speech using a fish-speech server
 over HTTP and keeps reference voices in one place.
CONTROL

    # md5sums: dpkg проверяет по нему целостность установленного.
    ( cd "$stage" && find usr -type f -exec md5sum {} \; > DEBIAN/md5sums )

    local pkg="$DIST/readingfish_${VERSION}_amd64.deb"
    rm -f "$pkg"
    fakeroot dpkg-deb --build --root-owner-group "$stage" "$pkg"
    echo "    пакет: $pkg"
}

case "${1:-all}" in
    linux)   build_linux ;;
    windows) build_windows ;;
    deb)     build_deb ;;
    all)     build_linux; build_windows; build_deb ;;
    *)       echo "Неизвестный аргумент: $1"; exit 1 ;;
esac

echo
echo "==> Готово. Результаты в $DIST/"
ls -la "$DIST" 2>/dev/null || true
