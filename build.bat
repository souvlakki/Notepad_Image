@echo off
rem Optional: rebuild ImageNotepad.exe from source using the compiler built into Windows
"%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /out:ImageNotepad.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ImageNotepad.cs
pause
