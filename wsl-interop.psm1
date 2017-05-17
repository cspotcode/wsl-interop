# Install this as a module in Powershell

Function Encode-Arguments {
    Process {
        $out = "$( $_ -Replace '(\\+)$','$1$1' -Replace '(\\*)"','$1$1\"' )"
        Write-Output $out
        # If we need to wrap in double quotes:
        #Write-Output "`"$out`""
    }
}

Function Encode-BashArguments {
    Process {
        # Use bash's "strong quoting" e.g. single-quoted string
        # Wrap the string in single-quotes, meaning that nothing in the string has a special meaning except for single-quotes.
        # Replace single-quotes with '\'' which: leaves quoting, inserts a single-quote, then re-enters single-quoting.
        Write-Output ("'" + ($_ -Replace "'","'\''") + "'")
    }
}

Function Normalize-Path {
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Path,
        [switch]
        $RealCase
    )
    $hasTrailingSlash = $Path -Match '[/\\]$'
    $bits = @($Path -Split '[\\/]+' | Where-Object { $_ -ne '.' -and $_ -ne '' })
    $IsAbsolute = $bits[0] -match ':$'
    $acc = ''
    $passedDotDots = 0
    ForEach($i in ($bits.Count - 1)..0) {
        $bit = $bits[$i]
        If($bit -eq '..') {
            $passedDotDots = $passedDotDots + 1
            Continue
        }
        If($passedDotDots -gt 0) {
            $passedDotDots = $passedDotDots - 1
            Continue
        }
        $acc = $bit + '\' + $acc
    }
    While($passedDotDots -gt 0) {
        $acc = '..\' + $acc
        $passedDotDots = $passedDotDots - 1
    }
    # Blank paths should be a single dot
    If($acc -eq '') { $acc = '.' }
    if($RealCase) {
        $acc = __realcase $acc
    }
    # Force path to have / not have a trailing slash, to match the input
    $acc = $acc -Replace '[/\\]+$','' -Replace '$',(&{if($hasTrailingSlash) {'\'} else {''}})
    Return $acc
}

Function __realcase($p) {
    If($p -Match '(^|[\\/])(\.\.|\.)[\\/]*$' -or $p -eq '') {
        # ./ or ../ or empty string
        Return $p
    }
    $parent = Split-Path -Parent $p
    If($parent -eq '' -and (Split-Path -IsAbsolute $p)) {
        # Just a qualifier.  Upper-case it (e.g. c: -> C:) and make sure it has a trailing slash
        Return $p.ToUpper() -Replace '\\$',''
    }
    $leaf =  Split-Path -Leaf $p
    # Attempt to grab the leaf's real casing
    Try {
        # -Force to get hidden items
        $leafFile = Get-ChildItem $parent -Force -ErrorAction Stop | Where-Object { $_.Name -eq $leaf }
        If($leafFile -ne $null) {
            $leaf = $leafFile.Name
        }
    } Catch {}
    If($parent -eq '') {
        Return $leaf
    } Else {
        Return (__realcase $parent) + '\' + $leaf
    }
}

Function Resolve-PathCase([Parameter(Mandatory = $True)][string]$Path) {

    # Remove all instances of .
    # For each adjacent instance of .., remove it and that many previous items
    # 

    Process {
        # TODO if path is relative, try to resolve it
        $doIt = {param($path)
            $basename = Split-Path -Leaf $path
            $dirname = Split-Path -Parent $path
            Try {
                $ci = Get-ChildItem -LiteralPath $dirname
            } Catch {
                If($_.Exception.GetType() -ne [System.Management.Automation.ItemNotFoundException]) {
                    Throw $_.Exception
                }
            }
            $ci = Where-Object { $_.Name -eq $basename }
            Return "$dirname$basename"
        }
    }
}

Function ConvertTo-LinuxPath([Parameter(Mandatory = $True)]$Path) {
    # Normalize Windows path and fix casing
    $WinPath = Normalize-Path -RealCase $Path
    # Replace \ with /
    $Acc = $WinPath -Replace '\\','/'
    $LinuxPrefix = ''
    $mounts = Get-LinuxMounts
    ForEach($i in 0..($mounts.Count - 1)) {
        $mount = $mounts[$i]
        $WinMountPath = $mount.WindowsPath
        # Deliberately use case-insensitive matching
        if((
            $WinPath.Length -gt $WinMountPath.Length -and $WinPath.Substring(0, $WinMountPath.Length + 1) -ieq "$($WinMountPath)\"
        ) -or (
            $WinPath -ieq $WinMountPath
        )) {
            # Slice off the windows mount path
            $Acc = $Acc.Substring($WinMountPath.Length)
            # Prefix with the linux mount path
            $LinuxPrefix = $mount.LinuxPath
            Break
        }
    }
    Return $LinuxPrefix + $Acc
}

Function ConvertFrom-LinuxPath([Parameter(Mandatory = $True)][string]$Path) {
    $Acc = $Path
    $WinPrefix = ''
    $mounts = Get-LinuxMounts
    ForEach($i in ($mounts.Count - 1)..0) {
        $mount = $mounts[$i]
        $LinuxPath = $mount.LinuxPath
        if($LinuxPath -eq '/') { $LinuxPath = '' }
        if((
            $Path.IndexOf("$($LinuxPath)/") -ceq 0
        ) -or (
            $Path -ceq $LinuxPath
        )) {
            # Slice off the linux mount path
            $Acc = $Acc.Substring($LinuxPath.Length)
            # Prefix with the windows mount path
            $WinPrefix = $mount.WindowsPath
            Break
        }
    }
    # Replace / with \
    $Acc = $Acc -Replace '/','\'
    Return $WinPrefix + $Acc
}

class LinuxMount {
    [string] $WindowsPath;
    [string] $LinuxPath;
    [string] $Type;
}

Function Get-LinuxMounts {
    $WindowsPrefix = "$HOME\AppData\Local\lxss\"
    bash -c 'cat /proc/mounts' | ForEach-Object {
        $splits = $_ -Split ' '
        $type = $splits[2]
        # Omit mounts that aren't mounting from the windows filesystem
        if($type -ceq 'lxfs' -or $type -ceq 'drvfs') {
            Write-Output (New-Object LinuxMount -Property @{
                WindowsPath = if($type -ceq 'lxfs') {
                    "$WindowsPrefix$($splits[0])"
                } elseif($type -ceq 'drvfs') {
                    $splits[0]
                } else {
                    $null
                }
                LinuxPath = $splits[1]
                Type = $splits[2]
            })
        }
    }
}

Function Run-Linux {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]
        $argv
    )
    $bashCommand = ($argv | Encode-BashArguments) -Join ' '
    $encodedBashCommand = $bashCommand | Encode-Arguments
    & bash.exe -c $encodedBashCommand
}

Export-ModuleMember -Function Run-Linux,ConvertTo-LinuxPath,ConvertFrom-LinuxPath,Get-LinuxMounts,Normalize-Path
