Import-Module ..\src\windows\wsl-interop.psm1 -ArgumentList $true

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

Describe 'Encode-Arguments' {
    It 'Should do the right thing' {
        Function echoargs($a) {
            Write-Host $a.count
            & node -e 'console.log(process.argv.length - 1); for(arg of process.argv.slice(1)) console.log(`>${arg}<`)' $a
        }
        Function test {
            $encoded = Write-Output $args | encode-arguments
            $output = @(echoargs $encoded)
            Write-Host 'Args: ' $args.count
            Write-Host $args
            Write-Host 'Encoded: ' $encoded.count
            Write-Host $encoded
            Write-Host 'Output: ' $output.count
            Write-Host $output
            $encoded.count | should beexactly $args.count
            $output[0] | should beexactly $args.count
            $output.count - 1 | should beexactly $args.count
            Foreach($i in 0..($args.count - 1)) {
                $output[$i + 1] | Should BeExactly $args[$i]
            }
        }
        # test 'foo'
        # test 'foo bar'
        # test 'foo' 'bar'
        # test 'foo"bar'
        test 'foo" bar'
        test 'foo\bar'
        test 'foobar\'
        test 'foobar\"'
        test 'foo bar\'
        test 'foo bar\"'
        test '"foobar'
        test '"foo bar'
        test '\foobar'
        test '\foo bar'
        test '\"foobar'
        test '\"foo bar'
    }
}