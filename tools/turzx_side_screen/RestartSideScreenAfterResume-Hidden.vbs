' Hidden one-shot launcher for the TURZX SideScreen resume recovery task.
Dim fso, shell, here, toolsRoot, root, port, intervalMs, delaySeconds, i, arg, command, exitCode

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

here = fso.GetParentFolderName(WScript.ScriptFullName)
toolsRoot = fso.GetParentFolderName(here)
root = fso.GetParentFolderName(toolsRoot)
port = "COM7"
intervalMs = "3000"
delaySeconds = "10"

i = 0
Do While i < WScript.Arguments.Count
    arg = LCase(WScript.Arguments(i))
    Select Case arg
        Case "-root"
            i = i + 1
            If i >= WScript.Arguments.Count Then WScript.Quit 2
            root = WScript.Arguments(i)
        Case "-port"
            i = i + 1
            If i >= WScript.Arguments.Count Then WScript.Quit 2
            port = WScript.Arguments(i)
        Case "-intervalms"
            i = i + 1
            If i >= WScript.Arguments.Count Then WScript.Quit 2
            intervalMs = WScript.Arguments(i)
        Case "-delayseconds"
            i = i + 1
            If i >= WScript.Arguments.Count Then WScript.Quit 2
            delaySeconds = WScript.Arguments(i)
        Case Else
            WScript.Echo "Unsupported TURZX resume argument: " & WScript.Arguments(i)
            WScript.Quit 2
    End Select
    i = i + 1
Loop

shell.CurrentDirectory = here
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & here & "\RestartSideScreenAfterResume.ps1"" -Root """ & root & """ -TaskName ""TURZX SideScreen"" -Port " & port & " -IntervalMs " & intervalMs & " -DelaySeconds " & delaySeconds
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
