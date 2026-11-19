# AULA F75 Mechanical Keyboard (Windows)

Authentic mechanical keyboard typing sounds across all applications on Windows. Built with a high-performance native audio engine (`waveOut`) supporting true polyphony, zero latency, and minimal resource usage.

## Features
- **Authentic Audio:** High quality AULA F75 switch samples.
- **True Polyphony:** Multi-voice audio engine (`winmm.dll`) prevents sound cut-offs and missed keystrokes during rapid typing.
- **Ultra-low Latency:** Direct native playback with < 5ms response time.
- **Zero bloat:** Pure Windows native solution with no bulky dependencies.
- **Tray Utility:** Volume slider, sound mute toggle, and autostart configuration directly from the system tray.

---

## How to Run

1. Double-click `Windows.bat`.
2. The application will start in your system tray (bottom-right taskbar).
3. Type anywhere — mechanical switch sounds will play across Windows.
4. Double-click the tray icon to open Settings (adjust volume, test sound, or enable startup at boot).

---

## Requirements
- Windows 7, 8, 10, or 11.
- .NET Framework 4.0+ (pre-installed on Windows).

---

## Project Structure
- `sounds/` - WAV audio samples.
- `MechanicalKeyboard.cs` - Native Windows C# source code.
- `Windows.bat` - Launch script (auto-builds on first run using Windows built-in `csc.exe`).
- `app_icon.ico` - Application icon.
- `LICENSE.txt` - MIT License.
