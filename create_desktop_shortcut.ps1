$desk = [Environment]::GetFolderPath('Desktop')
$targetExe = 'd:\mechanical-keyboard-vsc-master\AulaF75KeySound.exe'
$iconPath = 'd:\mechanical-keyboard-vsc-master\app_icon.ico'
$shortcutFile = Join-Path $desk 'Mechanical Keyboard.lnk'

$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($shortcutFile)
$sc.TargetPath = $targetExe
$sc.WorkingDirectory = 'd:\mechanical-keyboard-vsc-master'
$sc.IconLocation = "$iconPath,0"
$sc.Description = 'AULA F75 Mechanical Keyboard System-Wide App'
$sc.Save()

Write-Output "Shortcut created at: $shortcutFile"
Write-Output "Exists: $(Test-Path $shortcutFile)"
