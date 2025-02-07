@echo off
setlocal

set FRAMEWORK=net6.0

if not exist slsk-batchdl\bin\zips mkdir slsk-batchdl\bin\zips

call :publish_and_zip win-x86 	false 	sldl_win-x86.zip
call :publish_and_zip win-x86 	true 	sldl_win-x86_self-contained.zip
call :publish_and_zip linux-x64 true 	sldl_linux-x64.zip
call :publish_and_zip linux-arm true 	sldl_linux-arm.zip

endlocal
exit /b

:publish_and_zip
dotnet publish slsk-batchdl\slsk-batchdl.csproj -c Release -r %1 -p:PublishSingleFile=true -p:PublishTrimmed=%2 -p:DefineConstants=WINDOWS --self-contained=%2
if exist slsk-batchdl\bin\zips\%3 del /F /Q slsk-batchdl\bin\zips\%3
powershell.exe -nologo -noprofile -command "& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('slsk-batchdl\bin\Release\%FRAMEWORK%\%1\publish', 'slsk-batchdl\bin\zips\%3'); }"
exit /b

