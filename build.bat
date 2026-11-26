@echo off
setlocal
set "compiler=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%compiler%" set "compiler=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%compiler%" (
    echo .NET Framework C# compiler was not found.
    exit /b 1
)
"%compiler%" /target:winexe /out:"%~dp0ImageNotepad.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xml.dll /r:Microsoft.CSharp.dll "%~dp0ImageNotepad.cs" "%~dp0EditorOptions.cs" "%~dp0LanguageTools.cs" "%~dp0CrashRecovery.cs"
exit /b %errorlevel%
