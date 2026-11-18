#!/bin/sh
# Builds Overskin for Linux into dist/overskin-linux-x64.tar.gz
#   ./build-linux.sh
# The archive holds one self-contained program plus install.sh; nothing else needs installing.
set -e
root="$(cd "$(dirname "$0")" && pwd)"
out="$root/build/linux"
stage="$out/overskin"
rm -rf "$out" && mkdir -p "$stage" "$root/dist"

dotnet publish "$root/src/Overskin.Linux/Overskin.Linux.csproj" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -o "$out/app" --nologo -v quiet

cp "$out/app/overskin" "$stage/overskin"
cp "$root/packaging/linux/install.sh" "$root/packaging/linux/overskin.desktop" "$stage/"
cp "$root/src/Overskin.Linux/Assets/overskin.png" "$stage/overskin.png"
chmod +x "$stage/overskin" "$stage/install.sh"
tar -czf "$root/dist/overskin-linux-x64.tar.gz" -C "$out" overskin
echo "dist/overskin-linux-x64.tar.gz"
