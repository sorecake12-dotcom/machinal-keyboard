$startup = [Environment]::GetFolderPath('Startup')
$targetExe = 'd:\mechanical-keyboard-vsc-master\AulaF75KeySound.exe'
$iconPath  = 'd:\mechanical-keyboard-vsc-master\app_icon.ico'
$shortcutFile = Join-Path $startup 'AULA F75 Keyboard Sounds.lnk'

$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut($shortcutFile)
$sc.TargetPath = $targetExe
$sc.WorkingDirectory = 'd:\mechanical-keyboard-vsc-master'
$sc.IconLocation = "$iconPath,0"
$sc.Description = 'AULA F75 Mechanical Keyboard Sounds - start hidden at boot'
$sc.Save()

Write-Output "Created: $shortcutFile"
Write-Output "Exists : $(Test-Path $shortcutFile)"
