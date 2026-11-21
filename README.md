# AULA F75 Mechanical Keyboard (Windows)

Authentic mechanical keyboard typing sounds across all applications on Windows. Built with a high-performance native audio engine (`waveOut`) supporting true polyphony, zero latency, and minimal resource usage.

---

## Features
- **Authentic Audio:** High-quality AULA F75 switch samples.
- **True Polyphony:** 16-channel hardware voice pool (`winmm.dll`) prevents cut-offs and missed keystrokes during rapid typing.
- **Ultra-low Latency:** Direct native playback (< 5ms response time).
- **Zero Dependencies:** Pure Windows solution. No external runtimes, Python, Node.js, or pre-installed tools needed.
- **Self-Compiling:** `Windows.bat` automatically builds the executable in 0.5s via Windows built-in `csc.exe` if not present.
- **System Tray:** Full volume control (0%–100%), mute toggle, desktop shortcut creator, and auto-start on boot.

---

## Step-by-Step Usage Guide

### Step 1: Launch Application
- Double-click `Windows.bat`.
- If `MechanicalKeyboard.exe` is missing or was deleted, `Windows.bat` will automatically compile it from source in under 1 second.
- The app will start running silently in the background.

### Step 2: System Tray & Status
- Look at the bottom-right taskbar (System Tray).
- You will see the **AULA F75 Keyboard icon**.
- Start typing anywhere (browser, VS Code, Word, games) — authentic mechanical sounds will play instantly.

### Step 3: Volume & Settings Control
- **Double-click** (or single left-click) the tray icon to open the Settings window:
  - **Volume Slider:** Adjust sound volume from 0% to 100%.
  - **Enable Typing Sounds:** Quick checkbox to toggle sounds on/off.
  - **Test Sound:** Instantly test switch audio without typing.
  - **Start automatically with Windows:** Check this box so sounds start automatically whenever you turn on your PC.
  - **Desktop Shortcut:** Click to create a shortcut directly on your desktop.
  - **Hide to Tray:** Hides the settings window back to the system tray.

### Step 4: Quick Tray Actions
- **Right-click** the tray icon for quick access:
  - **Open Settings:** Re-open the configuration UI.
  - **Mute Sounds / Unmute Sounds:** Instant mute toggle.
  - **Exit:** Completely closes the application and unhooks keyboard listening.

---

## Project Structure
- `sounds/` - 11 WAV audio switch samples.
- `MechanicalKeyboard.cs` - Native Windows C# source code (Audio engine + Hook + UI).
- `Windows.bat` - One-click launch and auto-compile script.
- `app_icon.ico` - High-resolution application icon.
- `LICENSE.txt` - MIT License.
- `README.md` - Documentation.

