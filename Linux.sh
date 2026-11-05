#!/bin/bash
cd "$(dirname "$0")"
if command -v python3 &>/dev/null; then
    python3 mech_keyboard.py
else
    echo "Error: python3 is required but not found."
fi
