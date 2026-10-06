"""Small helpers for fetch-capi-libs.sh, kept in Python so the script needs no unzip or jq on any runner.

  zip-member.py extract <archive> <member> <output>
      Copies one member of a zip archive (a wheel is a zip) to a file; fails when the member is missing.
  zip-member.py pypi-file <pypi.json> <filename regex>
      Prints "<filename> <url> <sha256>" of the single release file whose name matches the regex, or an
      empty line when none or several match.
"""
import json
import re
import shutil
import sys
import zipfile

command, *args = sys.argv[1:]
if command == "extract":
    archive, member, out = args
    with zipfile.ZipFile(archive) as z:
        if member not in z.namelist():
            sys.exit(f"::error::{archive} has no member {member}")
        with z.open(member) as src, open(out, "wb") as dst:
            shutil.copyfileobj(src, dst)
elif command == "pypi-file":
    metadata, pattern = args
    with open(metadata, encoding="utf-8") as f:
        files = [u for u in json.load(f)["urls"] if re.search(pattern, u["filename"])]
    print(f'{files[0]["filename"]} {files[0]["url"]} {files[0]["digests"]["sha256"]}' if len(files) == 1 else "")
else:
    sys.exit(f"unknown command {command}")
