#!/bin/bash
# usage: render.sh name -> name.png (exactly 1600x900, sRGB)
CH=$(ls /opt/pw-browsers/chromium-*/chrome-linux*/chrome 2>/dev/null | head -1)
D="$(cd "$(dirname "$0")" && pwd)"
"$CH" --headless --no-sandbox --disable-gpu --force-color-profile=srgb --hide-scrollbars --force-device-scale-factor=1 --window-size=1600,1100 --screenshot="$D/$1.full.png" "file://$D/$1.html" >/dev/null 2>&1
python3 -I - "$D/$1.full.png" "$D/$1.png" <<'P'
import sys
from PIL import Image
im=Image.open(sys.argv[1]).convert('RGB').crop((0,0,1600,900)); im.save(sys.argv[2],optimize=True); print(im.size)
P
rm -f "$D/$1.full.png"
