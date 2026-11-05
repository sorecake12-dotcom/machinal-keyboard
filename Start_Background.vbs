Set WshShell = CreateObject("WScript.Shell")
WshShell.CurrentDirectory = "d:\mechanical-keyboard-vsc-master"
WshShell.Run """d:\mechanical-keyboard-vsc-master\AulaF75KeySound.exe""", 0, False
Set WshShell = Nothing
