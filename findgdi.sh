{
    echo '=== System.Drawing imports ==='
    rg -n --glob '*.cs' \
      '^\s*using\s+(System\.Drawing|Drawing\s*=\s*System\.Drawing)' || true

    echo
    echo '=== Fully-qualified references ==='
    rg -n --glob '*.cs' 'System\.Drawing\.' || true

    echo
    echo '=== Graphics and bitmap operations ==='
    rg -n --glob '*.cs' \
      'Graphics\.FromImage|new\s+Bitmap\s*\(|Image\.From(Stream|File)|Bitmap\.From(Stream|File)|ImageFormat|ImageCodecInfo|LockBits|DrawImage|DrawString|MeasureString' || true

    echo
    echo '=== Project references ==='
    rg -n \
      --glob '*.csproj' \
      --glob '*.props' \
      --glob '*.targets' \
      'System\.Drawing|System\.Drawing\.Common' || true
} | tee system-drawing-audit.txt

