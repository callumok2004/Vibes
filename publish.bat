@echo off
set VERSION=%1
if "%VERSION%"=="" set VERSION=1.0.0
dotnet publish Vibes/Vibes.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=%VERSION% -o publish
