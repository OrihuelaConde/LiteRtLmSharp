"""Builds the third-party notices for the LiteRT-LM native libraries from the license files Google ships
inside the official CLiteRTLM.xcframework: the framework's LICENSE plus every
third_party_licenses.bundle/<component>_LICENSE.html of its ios-arm64 slice.

native-release.yml uses it when the upstream release carries no THIRD_PARTY_NOTICES.txt asset (LiteRT-LM
v0.17.0 onward). The v0.16.0 notices asset was itself built from the Apple frameworks and covered every
platform's binaries, so the framework's own bundle is the closest upstream source.

Usage: notices-from-xcframework.py <CLiteRTLM.xcframework.zip> <output.txt> <upstream tag>
"""
import html
import re
import sys
import zipfile


def tidy(text):
    """Drops surrounding blank lines and trailing spaces but keeps the indentation of centered titles."""
    return text.strip("\n").rstrip()


src, out, tag = sys.argv[1:4]
with zipfile.ZipFile(src) as z:
    names = [n for n in z.namelist() if not n.startswith("__MACOSX")]
    license_path = next(n for n in names if n.endswith("/ios-arm64/CLiteRTLM.framework/LICENSE"))
    framework = license_path.rsplit("/", 1)[0]
    bundle = framework + "/third_party_licenses.bundle/"
    components = sorted(n for n in names if n.startswith(bundle) and n.endswith("_LICENSE.html"))
    if not components:
        sys.exit(f"no third_party_licenses.bundle/*_LICENSE.html in {src}")

    rule = "=" * 80
    parts = [
        f"{rule}\nTHIRD-PARTY SOFTWARE NOTICES AND INFORMATION\nLiteRT-LM {tag} native libraries\n{rule}\n\n"
        "This file reproduces the license of LiteRT-LM and the license of each third-party component that\n"
        f"Google lists in the official CLiteRTLM.xcframework of LiteRT-LM {tag}\n"
        "(third_party_licenses.bundle). The upstream release publishes no separate notices file for its\n"
        f"other prebuilt libraries. {len(components)} third-party components follow the LiteRT-LM license.\n",
        f"{rule}\nLiteRT-LM\n{rule}\n\n{tidy(z.read(license_path).decode('utf-8'))}\n",
    ]
    for name in components:
        component = name[len(bundle):-len("_LICENSE.html")]
        page = z.read(name).decode("utf-8", "replace")
        match = re.search(r"<pre>(.*?)</pre>", page, re.S)
        text = html.unescape(match.group(1) if match else re.sub(r"<[^>]+>", "", page))
        parts.append(f"{rule}\n{component}\n{rule}\n\n{tidy(text)}\n")

with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(parts))
print(f"wrote {out}: LiteRT-LM license + {len(components)} third-party components")
