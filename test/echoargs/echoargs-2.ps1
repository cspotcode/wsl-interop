Foreach($i in 0..($args.count - 1)) {
    write-host "Arg $($i): <$($args[$i])>"
}
Write-Host
write-host "Command line:"
write-host (get-wmiobject -Query "select * from Win32_Process where ProcessId = $pid").commandline