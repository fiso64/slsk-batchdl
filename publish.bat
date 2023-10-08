@echo off
setlocal

if not exist slsk-batchdl\bin\zips mkdir slsk-batchdl\bin\zips
 
REM win-x86
dotnet publish -c Release -r win-x86 -p:PublishSingleFile=true -p:DefineConstants=WINDOWS --self-contained false
if exist slsk-batchdl\bin\Release\net6.0\win-x86\publish\*.pdb del /F /Q slsk-batchdl\bin\Release\net6.0\win-x86\publish\*.pdb 
if exist slsk-batchdl\bin\zips\slsk-batchdl_win-x86.zip del /F /Q slsk-batchdl\bin\zips\slsk-batchdl_win-x86.zip
powershell.exe -nologo -noprofile -command "& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('slsk-batchdl\bin\Release\net6.0\win-x86\publish', 'slsk-batchdl\bin\zips\slsk-batchdl_win-x86.zip'); }"

REM win-x86 self-contained
dotnet publish -c Release -r win-x86 -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DefineConstants=WINDOWS --self-contained true
if exist slsk-batchdl\bin\Release\net6.0\win-x86\publish\*.pdb del /F /Q slsk-batchdl\bin\Release\net6.0\win-x86\publish\*.pdb 
if exist slsk-batchdl\bin\zips\slsk-batchdl_win-x86_self-contained.zip del /F /Q slsk-batchdl\bin\zips\slsk-batchdl_win-x86_self-contained.zip
powershell.exe -nologo -noprofile -command "& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('slsk-batchdl\bin\Release\net6.0\win-x86\publish', 'slsk-batchdl\bin\zips\slsk-batchdl_win-x86_self-contained.zip'); }"

REM linux-x64
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true
if exist slsk-batchdl\bin\Release\net6.0\linux-x64\publish\*.pdb del /F /Q slsk-batchdl\bin\Release\net6.0\linux-x64\publish\*.pdb 
if exist slsk-batchdl\bin\zips\slsk-batchdl_linux-x64.zip del /F /Q slsk-batchdl\bin\zips\slsk-batchdl_linux-x64.zip
powershell.exe -nologo -noprofile -command "& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('slsk-batchdl\bin\Release\net6.0\linux-x64\publish', 'slsk-batchdl\bin\zips\slsk-batchdl_linux-x64.zip'); }"


endlocal
