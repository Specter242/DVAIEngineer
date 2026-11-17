#!/usr/bin/env python3
"""List this version in repository.json, Unity Mod Manager's update feed.

Info.json's Repository link points at repository.json on the public GitHub
repository's main branch. Unity Mod Manager reads it to flag a newer version
in game, and its installer downloads the listed DownloadUrl, the zip attached
to that version's GitHub release. Adds Info.json's version, newest first, if
missing; with --check, only verifies that it is listed and newest.

A version is listed only in the publish step, after its GitHub release holds
the frozen zip: run with --release-exists once that release's asset answers,
so the feed never offers a download that isn't there.
"""

import argparse
import json
from pathlib import Path
import re
import sys


REPO = Path(__file__).resolve().parent.parent
INFO = REPO / "src/AIEngineer/Info.json"
FEED = REPO / "repository.json"
RAW = re.compile(r"^https://raw\.githubusercontent\.com/([^/]+)/([^/]+)/main/repository\.json$")


def download_answers(url):
    """Whether the release's zip downloads (a HEAD request, following GitHub's redirect)."""
    import urllib.request
    try:
        request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "update-repository"})
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status == 200
    except Exception:
        return False


def version_key(version):
    return tuple(int(part) for part in version.split("."))


def release(mod_id, version, owner, name):
    url = f"https://github.com/{owner}/{name}/releases/download/v{version}/{mod_id}-{version}.zip"
    return {"Id": mod_id, "Version": version, "DownloadUrl": url}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail instead of adding a missing version")
    parser.add_argument("--info", type=Path, default=INFO, help="the mod's Info.json")
    parser.add_argument("--feed", type=Path, default=FEED, help="its repository.json")
    parser.add_argument("--release-exists", action="store_true",
                        help="the version's GitHub release and its zip exist (checked here); required to add it")
    args = parser.parse_args()
    feed = args.feed

    info = json.loads(args.info.read_text(encoding="utf-8-sig"))
    mod_id, version = info["Id"], info["Version"]
    link = RAW.match(info.get("Repository", ""))
    if not link:
        sys.exit("Info.json's Repository must be the public repository's raw main/repository.json")
    if not info.get("HomePage"):
        sys.exit("Info.json needs its HomePage")
    owner, name = link.groups()

    releases = json.loads(feed.read_text())["Releases"] if feed.exists() else []
    expected = release(mod_id, version, owner, name)
    listed = [r for r in releases if r.get("Version") == version]
    if listed and listed[0] != expected:
        sys.exit(f"repository.json lists {version} with another download: {listed[0]}")
    if not listed:
        if args.check:
            sys.exit(f"repository.json does not list {version}")
        if not args.release_exists:
            sys.exit(f"{version} is listed only once its GitHub release holds the zip: publish it, then run with --release-exists")
        if not download_answers(expected["DownloadUrl"]):
            sys.exit(f"{expected['DownloadUrl']} does not answer: the GitHub release or its zip is missing")
        releases.append(expected)
    releases.sort(key=lambda r: version_key(r["Version"]), reverse=True)
    if releases[0]["Version"] != version:
        sys.exit(f"repository.json lists {releases[0]['Version']}, newer than Info.json's {version}")
    if any(r.get("Id") != mod_id for r in releases):
        sys.exit(f"repository.json lists a release that is not {mod_id}")

    text = json.dumps({"Releases": releases}, indent=2) + "\n"
    if not feed.exists() or feed.read_text() != text:
        if args.check:
            sys.exit("repository.json is not in its sorted form")
        feed.write_text(text)
        print(f"repository.json lists {mod_id} {version}")


if __name__ == "__main__":
    main()
