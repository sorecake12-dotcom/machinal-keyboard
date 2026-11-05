import os
import sys
import time
import random
import ctypes
from ctypes import wintypes
import winsound
from pathlib import Path

# Base directories
BASE_DIR = Path(__file__).resolve().parent
SOUNDS_DIR = BASE_DIR / "sounds"

# Sound mappings
SPECIAL_KEYS = {
    0x08: "backspace.wav",
    0x09: "tab.wav",
    0x0D: "enter.wav",
    0x20: "space.wav",
    0x10: "shift.wav",
    0xA0: "shift.wav",
    0xA1: "shift.wav",
}
RANDOM_KEYS = ["a.wav", "b.wav", "c.wav", "d.wav", "e.wav", "f.wav"]

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_SYSKEYDOWN = 0x0104

class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_ulong),
    ]

# Preload sound paths
cached_sounds = {}
for name in ["a", "b", "c", "d", "e", "f", "backspace", "enter", "shift", "space", "tab"]:
    p = SOUNDS_DIR / f"{name}.wav"
    if p.exists():
        cached_sounds[f"{name}.wav"] = str(p)

last_time = 0

def play_key(vk_code):
    global last_time
    now = time.time()
    if now - last_time < 0.018:
        return
    last_time = now

    wav_name = SPECIAL_KEYS.get(vk_code, random.choice(RANDOM_KEYS))
    path = cached_sounds.get(wav_name, cached_sounds.get("a.wav"))
    if path:
        try:
            winsound.PlaySound(path, winsound.SND_FILENAME | winsound.SND_ASYNC)
        except Exception:
            pass

HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)

def hook_proc(nCode, wParam, lParam):
    if nCode >= 0 and wParam in (WM_KEYDOWN, WM_SYSKEYDOWN):
        kb_struct = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        play_key(kb_struct.vkCode)
    return ctypes.windll.user32.CallNextHookEx(None, nCode, wParam, lParam)

def main():
    print("=" * 60)
    print("  AULA F75 Mechanical Keyboard - Laptop Global Sound App")
    print("=" * 60)
    print("Typing sounds active across full laptop (all apps/browsers).")
    print("Press Ctrl+C in this terminal to exit.")
    print("-" * 60)

    callback = HOOKPROC(hook_proc)
    hook = ctypes.windll.user32.SetWindowsHookExW(
        WH_KEYBOARD_LL,
        callback,
        ctypes.windll.kernel32.GetModuleHandleW(None),
        0
    )

    if not hook:
        print("[Error] Failed to install keyboard hook.")
        return

    msg = wintypes.MSG()
    try:
        while ctypes.windll.user32.GetMessageW(ctypes.byref(msg), None, 0, 0) != 0:
            ctypes.windll.user32.TranslateMessage(ctypes.byref(msg))
            ctypes.windll.user32.DispatchMessageW(ctypes.byref(msg))
    except KeyboardInterrupt:
        pass
    finally:
        ctypes.windll.user32.UnhookWindowsHookEx(hook)
        print("\nMechanical keyboard stopped.")

if __name__ == "__main__":
    main()
