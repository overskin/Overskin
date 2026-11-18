#!/bin/sh
# Installs Overskin for the current user (no sudo):
#   ~/.local/bin/overskin, a menu entry, and the icon.
# Run it again to update. ./install.sh --uninstall removes it (your settings stay in ~/.config/overskin).
set -e
here="$(cd "$(dirname "$0")" && pwd)"
bin="$HOME/.local/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

if [ "$1" = "--uninstall" ]; then
    "$bin/overskin" --quit 2>/dev/null || true
    rm -f "$bin/overskin" "$apps/overskin.desktop" "$icons/overskin.png"
    rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/overskin.desktop"
    echo "Overskin removed."
    exit 0
fi

mkdir -p "$bin" "$apps" "$icons"
"$bin/overskin" --quit 2>/dev/null || true
install -m 755 "$here/overskin" "$bin/overskin"
install -m 644 "$here/overskin.png" "$icons/overskin.png"
sed "s|^Exec=overskin|Exec=$bin/overskin|" "$here/overskin.desktop" > "$apps/overskin.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" 2>/dev/null || true

if [ "$1" = "--autostart" ]; then
    auto="${XDG_CONFIG_HOME:-$HOME/.config}/autostart"
    mkdir -p "$auto"
    sed "s|^Exec=overskin$|Exec=$bin/overskin --tray|; s|^Exec=overskin |Exec=$bin/overskin |" "$here/overskin.desktop" > "$auto/overskin.desktop"
fi

echo "Overskin is installed. Open it from your app menu, or run: overskin"
echo "For a capture shortcut, bind a key in your keyboard settings to: $bin/overskin --capture"
if [ -n "$WAYLAND_DISPLAY" ]; then
    case "${XDG_CURRENT_DESKTOP:-}${HYPRLAND_INSTANCE_SIGNATURE:+Hyprland}${SWAYSOCK:+sway}" in
        *[Hh]yprland*|*sway*|*river*|*niri*|*wayfire*|*labwc*)
            command -v grim >/dev/null || echo "Install grim for screenshots on this desktop (Arch: sudo pacman -S grim)." ;;
    esac
    command -v wl-copy >/dev/null || echo "Install wl-clipboard so screenshots can go on the clipboard."
elif ! command -v xclip >/dev/null; then
    echo "Install xclip so screenshots can go on the clipboard."
fi
case ":$PATH:" in *":$bin:"*) ;; *) echo "(Add $bin to your PATH to run 'overskin' from a terminal.)" ;; esac
