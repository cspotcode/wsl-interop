Import-Module ..\wsl-interop.psm1

# Get-PathBits 'C:\users\abradley\..\.\what\'
# Get-PathBits '.\users\abradley\..\.\what\'
# Get-PathBits 'users\abradley\..\.\what\'
# Get-PathBits '..\..\users'
# Get-PathBits '.\users'
# Get-PathBits '.users'

# Normalize-Path -RealCase 'C:\users\abradley\..\.\what\'
# Normalize-Path -RealCase 'C:\users\abradley'
# Normalize-Path -RealCase 'C:\users\..'
# Normalize-Path -RealCase 'C:\'
# Normalize-Path 'C:\users\abradley\..\.\what\'
# Normalize-Path 'C:\users\abradley'
# Normalize-Path 'C:\users\..'
# Normalize-Path 'C:\'
# Normalize-Path 'foo/bar'
# Normalize-Path 'foo'

Describe 'Normalize-Path' {
    It 'Should do the right thing' {
        Normalize-Path './.././..' | Should BeExactly '..\..'
        Normalize-Path './.././../' | Should BeExactly '..\..\'
        Normalize-Path '.' | Should BeExactly '.'
        Normalize-Path '..' | Should BeExactly '..'
        Normalize-Path '../' | Should BeExactly '..\'
        Normalize-Path './' | Should BeExactly '.\'
        Normalize-Path '/' | Should BeExactly '\'
    }

    It "Fixes casing on parts of the path that do exist; doesn't touch parts that don't exist on filesystem" {
        Normalize-Path 'fixture-file\foo\bar\baz' | Should BeExactly 'fixture-file\foo\bar\baz'
        Normalize-Path -RealCase 'fixture-file\foo\bar\baz' | Should BeExactly 'Fixture-File\foo\bar\baz'
    }
    It 'Misc' {
        Normalize-Path 'c:' | Should BeExactly 'c:'
        Normalize-Path -RealCase 'c:' | Should BeExactly 'C:'
        Normalize-Path 'c:/' | Should BeExactly 'c:\'
        Normalize-Path -RealCase 'c:/' | Should BeExactly 'C:\'
        Normalize-Path 'c:\' | Should BeExactly 'c:\'
        Normalize-Path -RealCase 'c:\' | Should BeExactly 'C:\'
        Normalize-Path 'c' | Should BeExactly 'c'
        Normalize-Path -RealCase 'c' | Should BeExactly 'c'
       ConvertTo-LinuxPath 'C:\Users\abradley\appdata\Local\lxss\rootfs' | Should BeExactly '/'
       ConvertTo-LinuxPath 'C:\Users\abradley\appdata\Local\lxss\rootfs\' | Should BeExactly '/'
       ConvertTo-LinuxPath 'C:\Users\abradley\appdata\Local\lxss\root' | Should BeExactly '/root'
       ConvertTo-LinuxPath 'C:\Users\abradley\appdata\Local\lxss\root\' | Should BeExactly '/root/'
    }
}
