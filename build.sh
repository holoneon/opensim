#!/usr/bin/env bash
set -euo pipefail

dotnet bin/prebuild.dll \
    /target vs2022 \
    /targetframework net8_0 \
    /excludedir="obj|bin" \
    /file prebuild.xml

find . -name "*.csproj" \
    -exec sed -i '/EnableUnsafeBinaryFormatterSerialization/d' {} +

dotnet clean -c Release OpenSim.sln

dotnet restore OpenSim.sln --force

dotnet build -c Release --no-restore OpenSim.sln
