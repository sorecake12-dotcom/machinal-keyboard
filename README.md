# AULA F75 Mechanical Keyboard Studio Pro

Professional mechanical keyboard sound utility designed for an authentic typing experience.

## Features
- **Authentic Sound:** High-quality mechanical switch audio samples.
- **Low Latency:** Optimized playback engine with minimal CPU impact.
- **Customizable:** Adjust volume and settings.
- **Cross-Platform:** Supports Windows, macOS, and Linux.

---

## Getting Started

### Prerequisites
- **Windows:** .NET Framework 4.0+ (usually pre-installed).
- **macOS:** `afplay` (built-in).
- **Linux:** `paplay` or `aplay` (check your distribution).

### Launching the Application

#### Windows
1. Double-click `Windows.bat`.
2. The application will start in the background and appear in your system tray.

#### macOS
1. Open Terminal and run: `chmod +x Mac.command`
2. Double-click `Mac.command`.

#### Linux
1. Open Terminal and run: `chmod +x Linux.sh`
2. Run: `./Linux.sh`

---

## Folder Structure
- `/sounds`: Contains WAV audio samples.
- `/src`: Application source code.
- `/Windows.bat`: Windows launch script.
- `/Mac.command`: macOS launch script.
- `/Linux.sh`: Linux launch script.

---

## Known Limitations
- Global keyboard hooking is natively supported on Windows. macOS and Linux support for system-wide background typing sound may require additional permissions or specific desktop environment configurations.
- Some audio features are optimized specifically for the Windows audio engine.

## Credits
- Sound assets provided by mechanical keyboard enthusiasts.
- Licensed under MIT.
