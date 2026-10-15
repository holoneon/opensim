#!/usr/bin/env bash
set -euo pipefail

cd /opt/opensim10

# Generate .NET 10 project files
dotnet bin/prebuild.dll \
    /target vs2022 \
    /targetframework net10_0 \
    /excludedir="obj|bin" \
    /file prebuild.xml

# Remove generated BinaryFormatter overrides
find . -name '*.csproj' \
    -not -path '*/obj/*' \
    -not -path '*/bin/*' \
    -exec sed -i '/EnableUnsafeBinaryFormatterSerialization/d' {} +

# Clean generated projects
dotnet clean OpenSim.sln -c Release

# Build OpenSim for .NET 10
dotnet build OpenSim.sln -c Release \
    2>&1 | tee /tmp/holoneon10-build.log
