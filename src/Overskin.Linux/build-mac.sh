#!/bin/bash
# Builds Overskin for macOS on a Mac (GitHub's Mac runners use this): Overskin.app for Apple
# silicon and for Intel Macs, each signed ad hoc with Apple's codesign, as a .dmg (drag to
# Applications) and a .zip, into dist/mac/.
#
#   bash src/Overskin.Linux/build-mac.sh
#
# (On Windows, build-mac.ps1 makes the zips.)
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dist="$root/dist/mac"
work="$root/build/mac"
mkdir -p "$dist" "$work"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$here/Overskin.Linux.csproj" | head -1)"

# the icon: an .icns at the sizes macOS asks for
iconset="$work/overskin.iconset"
rm -rf "$iconset"; mkdir -p "$iconset"
for size in 16 32 128 256; do
  sips -z $size $size "$here/Assets/overskin.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$here/Assets/overskin.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$work/overskin.icns"

for target in "osx-arm64 AppleSilicon" "osx-x64 Intel"; do
  set -- $target
  rid=$1; label=$2
  publish="$work/publish-$rid"
  rm -rf "$publish"
  echo "Building Overskin $version for macOS ($label)..."
  dotnet publish "$here/Overskin.Linux.csproj" -c Release -r "$rid" --self-contained true -p:DebugType=none -o "$publish" --nologo -v quiet

  app="$work/$label/Overskin.app"
  rm -rf "$work/$label"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp -R "$publish/." "$app/Contents/MacOS/"
  rm -f "$app/Contents/MacOS/"*.pdb
  cp "$work/overskin.icns" "$app/Contents/Resources/overskin.icns"
  printf 'APPL????' > "$app/Contents/PkgInfo"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Overskin</string>
  <key>CFBundleDisplayName</key><string>Overskin</string>
  <key>CFBundleIdentifier</key><string>app.overskin</string>
  <key>CFBundleExecutable</key><string>overskin</string>
  <key>CFBundleIconFile</key><string>overskin</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Overskin records your microphone only when you choose to record sound with a video.</string>
  <key>NSAppleEventsUsageDescription</key><string>Overskin shows a notification when a capture is saved.</string>
</dict>
</plist>
PLIST
  plutil -lint "$app/Contents/Info.plist"
  chmod +x "$app/Contents/MacOS/overskin"

  # signed ad hoc as a whole (Apple silicon won't run unsigned code), then checked
  codesign --force --deep --sign - "$app"
  codesign --verify --deep --strict --verbose=1 "$app"

  zip="$dist/Overskin-mac-$label.zip"
  dmg="$dist/Overskin-mac-$label.dmg"
  rm -f "$zip" "$dmg"
  ditto -c -k --keepParent "$app" "$zip"

  # the disk image: Overskin beside a shortcut to Applications, to drag it across
  stage="$work/dmg-$label"
  rm -rf "$stage"; mkdir -p "$stage"
  cp -R "$app" "$stage/"
  ln -s /Applications "$stage/Applications"
  hdiutil create -volname "Overskin" -srcfolder "$stage" -ov -format UDZO -fs HFS+ "$dmg" >/dev/null
  hdiutil verify "$dmg" >/dev/null
  echo "Wrote $dmg ($(du -h "$dmg" | cut -f1)) and $zip ($(du -h "$zip" | cut -f1))"
done
