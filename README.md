# WIP wsl-interop

*Currently work-in-progress, not usable as-is*

Windows Subsystem for Linux integration between bash and PowerShell.  Call Windows binaries from Linux and vice versa, convert paths between WSL and Windows.  Also includes a couple argv encoding commands that are useful even  if you don't use WSL.

WSL already allows calling Windows binaries from within bash. For example `/mnt/c/Windows/System32/notepad.exe`.  It even populates the Linux $PATH with Windows' $PATH so you can do `notepad.exe`.  *AND* you can run .ps1 and .cmd / .bat scripts directly.  However, it doesn't do lookups with `$PathExt`, so you can't launch `notepad` without the extension.  
*TODO does it allow launching `.cmd` or `.ps1`?  Yes!  Neither of those extensions are in $PathExt, though.*

Calling Linux binaries from Windows requires calling `bash.exe -c "linuxCommand arg1 arg2 arg3"`
The argv array must be bash-escaped and then Windows-escaped.

## WIP

* [x] Powershell cmdlets for path conversion
* [x] Test harness
* [ ] Complete tests
* [ ] Windows `which` from Linux
* [ ] Confirm that PSM1 will auto-load when one of the Cmdlets is invoked

### APIs / Commands

#### Powershell

`ConvertTo-LinuxPath -Path <Path>`  
Convert a Windows path to a WSL path

`ConvertFrom-LinuxPath -Path <Path>`  
Convert a WSL path to a Windows Path

`Get-LinuxCommand -Name <Name>`  
Equivalent to Linux `which` command.  Uses the Linux $PATH and Linux execute permissions.
*TODO this command must query permissions.*

`Get-PathIsWsl -Path <Path>`  
Returns true if the path is controlled by WSL.  
*TODO come up with better description than "controlled by"*  
This means:

* This file / directory has Linux permissions. (Whereas all Windows files are mode 777 within WSL)
* This file / directory should not be manipulated by Windows processes because they'll mess up the Linux permissions.

`Run-Linux`  
Run a Linux command.  Converts relative or absolute paths to Linux paths.  Resolves all others using Linux `which`.
Takes care of encoding and escaping argv so they are passed accurately to the Linux process.

`Get-LinuxMounts`  
Returns an array of Windows paths mounted into WSL.  For example, C:\ is mounted at /mnt/c/ in WSL.

`Encode-CommandLine`  
Encode argv-style arguments for passing as the command-line string to a Windows process.
Windows API technically doesn't do argv; it does a single command-line string.  There is a standard encoding by which this string is parsed into an argv array, but that encoding is unusual and unintuitive.

`Encode-BashArguments`  
*TODO export this function?  It shouldn't be necessary to users who can instead use Run-Linux.*
Encode strings using Bash's "strong quoting" (single-quoted string)

#### Linux

`win <path> [args]`  
Run a Windows executable, taking $PathExt into account and properly encoding arguments.  Note that WSL already lets us run Windows binaries directly from WSL, and it adds our Windows $Path to our Linux $Path, but it doesn't do $PathExt lookup.  Thus this command is unnecessary for `./foo.exe bar.txt` but is necessary for `win ./foo bar.txt` and `win notepad bar.txt`.

TODO also lookup launchable extensions and use a powershell shim to launch them.  (For example, .cmd or .js files)

`winpath ...`  
Convert a Linux path to Windows.

`linuxpath ...`  
Convert a Windows path to Linux.

`winwhich <name>`  
Like Linux `which` or Powershell's `Get-Command`.  Returns the *Linux* path to a *Windows* executable found on Windows' $Path, taking $PathExt into account as well.  Also finds .ps1 files because, even though they are not in $PathExt, they are directly executable.  
*Note: unlike `Get-Command`, this does not find Cmdlets.*  
*TODO add flag to conditonally take Windows' $Path into account?  Because WSL by default already has windows' $Path.*

`pscmd` / `psexec` (??)  
Run Powershell commands without the `-NoLogo -NoProfile -Command` ceremony?

`encwincmd`  
Encode all positional parameters as a Windows command-line, handling all the proper escaping.
When WSL launches a Windows executable, it wraps args in double-quotes if they need it but it doesn't perform the necessary escaping.

#### Windows Non-Powershell

For example, if you want to interop with Linux from .cmd or any non-Powershell environment.

*TODO decide what this looks like.  Match the Linux API?*

### Use cases

#### Convert Windows path to Linux path

```
# In addition to swapping \ for /, also canonicalizes all filenames since Linux is case-sensitive.
PS> ConvertTo-LinuxPath C:\Users\abradley\foo\BaR
PS> ConvertTo-LinuxPath .\foo\BaR
$ linuxpath C:\Users\abradley\foo\BaR
$ linuxpath '.\foo\BaR'
```

#### Convert Linux path to Windows

```
PS> ConvertFrom-LinuxPath /mnt/c/Users/abradley/foo/bar
PS> ConvertFrom-LinuxPath ./foo/bar
$ winpath /mnt/c/Users/abradley/foo/bar
$ winpath ./foo/bar
```

#### Launch Linux binary from PowerShell

```
PS> Run-Linux .\lpass
# Skip translating from Windows to Linux path
PS> Run-Linux -NoTranslate ./lpass
```

#### Launch Linux binary on Linux $PATH from PowerShell

```
# Omit relative path prefix to trigger $PATH lookup
PS> Run-Linux vim
```

#### Launch Windows binary on $Env:Path from bash

This is built-in to the Creators Update.  However, looking up the executable
on your Windows PATH and PATHEXT is not.

```
# Lookup lpass on Windows PATH and invoke it
win lpass
# If path is relative or absolute, path lookup is skipped.  However, in this case, `win` is unnecessary.
```

#### Locate Windows binary on $Env:Path from bash

Uses Windows PATH and PATHEXT values to lookup a Windows binary.  Returns the Linux path to the file unless the -w option is passed.

```
winwhich lpass # -> /mnt/c/users/abradley/projects/lpass/bin/lpass.exe
winwhich -w lpass # -> C:\users\abradley\projects\lpass\bin\lpass.exe
# Allows overriding the PATH and/or PATHEXT values (but note that these are in their Windows forms, not Linux)
WINPATH=whatever WINPATHEXT=whatever winwhich lpass
WINPATH=whatever PATHEXT=whatever winwhich lpass
```

### Notes / Gotchas:

Windows launches stuff in local directory without ./ prefix
