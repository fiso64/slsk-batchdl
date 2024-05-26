#!/bin/bash

### Download dotnet from https://dotnet.microsoft.com/en-us/download/dotnet/6.0

# Create directory if it doesn't exist
mkdir -p slsk-batchdl/bin/zips

# For macOS ARM64
dotnet publish -c Release -r osx-arm64 -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true
rm -f slsk-batchdl/bin/Release/net6.0/osx-arm64/publish/*.pdb
rm -f slsk-batchdl/bin/zips/slsk-batchdl_osx-arm64.zip
zip -r slsk-batchdl/bin/zips/slsk-batchdl_osx-arm64.zip slsk-batchdl/bin/Release/net6.0/osx-arm64/publish

# For macOS x64
# dotnet publish -c Release -r osx-x64 -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true
# rm -f slsk-batchdl/bin/Release/net6.0/osx-x64/publish/*.pdb
# rm -f slsk-batchdl/bin/zips/slsk-batchdl_osx-x64.zip
# zip -r slsk-batchdl/bin/zips/slsk-batchdl_osx-x64.zip slsk-batchdl/bin/Release/net6.0/osx-x64/publish
