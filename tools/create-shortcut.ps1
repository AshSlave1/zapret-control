$target = Join-Path $PSScriptRoot '..\publish\ZapretControl.exe'
$target = [System.IO.Path]::GetFullPath($target)
$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop 'ZAPRET CONTROL.lnk'

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnkPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = Split-Path -Parent $target
$shortcut.IconLocation = "$target,0"
$shortcut.Description = 'ZAPRET//CTRL - панель управления обходом zapret'
$shortcut.Save()

Write-Output ("Shortcut: {0}" -f $lnkPath)
Write-Output ("Target:   {0}" -f $target)
