' ===========================================================================
'  DSH Balance Pet - silent launcher (no console window).
'  Double-click this file to start the pet.
'
'  Prefers the compiled C# app (DshPet.exe) and falls back to the frozen
'  PowerShell build. Deleting the exe is therefore a complete rollback.
'
'  Kept ASCII-only: cscript.exe reads .vbs as ANSI without a BOM.
' ===========================================================================
Option Explicit

Dim fso, shell, here, exePath, script, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

here = fso.GetParentFolderName(WScript.ScriptFullName)
exePath = fso.BuildPath(here, "DshPet.exe")
script = fso.BuildPath(here, "pwsh_version\dsh_pet.ps1")

If fso.FileExists(fso.BuildPath(here, "sprite.png")) = False Then
    MsgBox "sprite.png is missing." & vbCrLf & vbCrLf & _
           "Run this once in PowerShell to build it from your artwork:" & vbCrLf & _
           "  .\make_sprite.ps1 -Source ""<path to your image>""", 48, "DSH Balance Pet"
    WScript.Quit 1
End If

shell.CurrentDirectory = here

If fso.FileExists(exePath) Then
    shell.Run """" & exePath & """", 0, False
ElseIf fso.FileExists(script) Then
    cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & script & """"
    shell.Run cmd, 0, False
Else
    MsgBox "Neither DshPet.exe nor pwsh_version\dsh_pet.ps1 was found.", 16, "DSH Balance Pet"
    WScript.Quit 1
End If
