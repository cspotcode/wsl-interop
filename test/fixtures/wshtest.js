// A script that can run in Windows Script Host
var WshShell = new ActiveXObject("WScript.Shell");
WshShell.Run("calc");
WScript.Sleep(30000);
// WshShell.Run("c:/windows/system32/notepad.exe");
// WScript.Sleep(3000);
WshShell.AppActivate("calc");
