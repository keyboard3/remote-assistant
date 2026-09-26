#!/bin/sh
set -eu

cd "$(dirname "$0")"
swift test
swift build -c release
app=".build/远程助手.app"
mkdir -p "$app/Contents/MacOS"
cp .build/release/RemoteAssistantMac "$app/Contents/MacOS/RemoteAssistantMac"
cp Info.plist "$app/Contents/Info.plist"
echo "Built: $app"
