$ErrorActionPreference = 'Stop'

Function Get-ShellOpenCommand {
    param(
        # Gets command to open files with this extension
        $Extension,
        # Gets command to open filetype by this name (in registry)
        $Name,
        # Gets command to open this file, with filepath interpolated into the command string
        $FilePath,
        # Use this commandline; do not look it up in Windows registry
        $CommandLine,
        # Interpolates these arguments into the command string
        $Arguments
    )
    $hkcr = 'Microsoft.PowerShell.Core\Registry::HKEY_CLASSES_ROOT\'
    if($name -eq $null) {
        if($Extension -eq $null -and $FilePath -ne $null) {
            if($FilePath -Match '\.[^./\\]+?$') {
                $Extension = $matches[0]
            } else {
                $Extension = ''
            }
        }
        Write-Host $Extension
        # TODO handle empty-string extension
        $Name = (Get-Item (Join-Path $hkcr $Extension)).GetValue('')
        # TODO error-check
    }
    # TODO deal with names containing '..' or slashes?
    $cmdLine = (Get-Item (Join-Path (Join-Path $hkcr $Name) 'shell\open\command')).GetValue('')
    # TODO insert FilePath into cmdLine
    # TODO insert other args into cmdLine
    return $cmdLine
}

# TODO move into .\test
get-shellopencommand -extension .py
get-shellopencommand -filepath .\help\whatever.py
get-shellopencommand -filepath .\help\whatever
get-shellopencommand -filepath .\help\whatever\