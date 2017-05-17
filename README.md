# WIP wsl-interop
Windows Subsystem for Linux integration between bash and PowerShell.  Call Windows binaries from Linux and vice versa.

WSL already allows calling Windows binaries from within bash. For example `/mnt/c/Windows/System32/notepad.exe`.  It even populates the Linux $PATH with Windows $PATH so you can do `notepad.exe`.  However, it doesn't do lookups with `$PathExt`, so you can't do `notepad` without the extension.  TODO does it allow launching `.cmd` or `.ps1`?

Calling Linux binaries from Windows requires calling `bash.exe -c "linuxCommand arg1 arg2 arg3"`
The argv array must be bash-escaped and then Windows-escaped.

## WIP

* [x] Powershell cmdlets for path conversion
* [x] Test harness
* [ ] Complete tests
* [ ] Windows `which` from Linux
* [ ] Confirm that PSM1 will auto-load when one of the Cmdlets is invoked

### Use cases

#### Convert Windows path to Linux path

```
# In addition to swapping \ for /, also canonicalizes all filenames since Linux is case-sensitive.
PS> LinuxPath C:\Users\abradley\foo\BaR
PS> LinuxPath .\foo\BaR
$ LinuxPath C:\Users\abradley\foo\BaR
$ LinuxPath '.\foo\BaR'
```

#### Convert Linux path to Windows

```
PS> WindowsPath /mnt/c/Users/abradley/foo/bar
PS> WindowsPath ./foo/bar
$ WindowsPath /mnt/c/Users/abradley/foo/bar
$ WindowsPath ./foo/bar
```

#### Launch Linux binary from PowerShell

```
PS> linux .\lpass
# Skip translating from Windows to Linux path
PS> linux -NoTranslate ./lpass
```

#### Launch Linux binary on Linux $PATH from PowerShell

```
# Omit relative path prefix to trigger $PATH lookup
PS> linux vim
```

#### Launch Windows binary from bash

```
$ win ./lpass
# Skip translating from Linux to Windows path
$ win --no-translate .\lpass
```

#### Launch Windows binary on $Env:PATH from Linux

```
# Omit relative path prefix to trigger $Env:PATH lookup
$ win notepad
```

### Notes / Gotchas:

Windows launches stuff in local directory without ./ prefix
