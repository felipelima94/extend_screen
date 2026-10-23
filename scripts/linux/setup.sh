#!/usr/bin/env bash
# Prepares Fedora (GNOME/Wayland) to run the ExtendedScreen host. Shows each command before running it.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

run() {
    echo "+ $*"
    "$@"
}

run sudo dnf install -y dotnet-sdk-10.0 android-tools gstreamer1-plugins-bad-free gstreamer1-plugin-openh264 pipewire-gstreamer

# Virtual pen with pressure: allow the desktop user to use /dev/uinput.
# uaccess only takes effect in rules that sort before 73-seat-late.rules.
run sudo rm -f /etc/udev/rules.d/99-extendedscreen-uinput.rules
run sudo install -m 0644 "$here/70-extendedscreen-uinput.rules" /etc/udev/rules.d/70-extendedscreen-uinput.rules
run sudo modprobe uinput
run sudo udevadm control --reload-rules
run sudo udevadm trigger --name-match=uinput

if [[ "${1:-}" == "--gpu" ]]; then
    # Fedora's Mesa ships without H.264 encoding; RPM Fusion's build enables it (VA-API, vah264enc).
    if ! rpm -q rpmfusion-free-release >/dev/null 2>&1; then
        run sudo dnf install -y "https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm"
    fi
    run sudo dnf swap -y mesa-va-drivers mesa-va-drivers-freeworld
fi

echo
echo "Pronto. Teste com: dotnet run --project pc/ExtendedScreen.Linux -- --check"
