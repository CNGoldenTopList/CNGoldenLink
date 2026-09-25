"""Publish a versioned package; credentials are supplied only through the environment."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys


CDN = "https://aliyun-static.diving-fish.com/"
LATEST_KEY = "cngist/CNGoldenLink-latest.json"


def bucket_for(sdk):
    for name in ("OSS_ACCESS_KEY_ID", "OSS_ACCESS_KEY_SECRET"):
        if not os.environ.get(name):
            raise ValueError(f"Missing required secret: {name}")
    auth = sdk.Auth(os.environ["OSS_ACCESS_KEY_ID"], os.environ["OSS_ACCESS_KEY_SECRET"])
    return sdk.Bucket(auth, "https://oss-cn-shanghai.aliyuncs.com",
                      "aliyun-static-diving-fish", connect_timeout=60)


def publish_latest(version, sdk, release_url=None):
    """Point the in-game update check at an already uploaded package. Run after the GitHub Release exists."""
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Expected a major.minor.patch version")
    bucket = bucket_for(sdk)
    key = f"cngist/CNGoldenLink-{version}.zip"
    existing = bucket.head_object(key)
    manifest = {"schema": "cngoldenlink.release/1", "version": version, "downloadUrl": CDN + key,
                "sha256": existing.headers.get("x-oss-meta-sha256"), "releaseUrl": release_url}
    # Short cache: clients poll this file; the versioned ZIP itself stays immutable.
    bucket.put_object(LATEST_KEY, json.dumps(manifest, ensure_ascii=False).encode("utf-8"), headers={
        "Content-Type": "application/json; charset=utf-8", "Cache-Control": "public, max-age=300"})
    print(f"Latest manifest: {CDN + LATEST_KEY} -> {version}")
    return manifest


def upload(package, sdk):
    package = Path(package)
    if not re.fullmatch(r"CNGoldenLink-\d+\.\d+\.\d+\.zip", package.name):
        raise ValueError("Expected a versioned CNGoldenLink ZIP filename")
    if not package.is_file():
        raise ValueError("Package does not exist")
    bucket = bucket_for(sdk)
    key = "cngist/" + package.name
    with package.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    try:
        bucket.put_object_from_file(key, str(package), headers={
            "Content-Type": "application/zip",
            "Cache-Control": "public, max-age=31536000, immutable",
            "x-oss-forbid-overwrite": "true",
            "x-oss-meta-sha256": digest,
        })
    except sdk.exceptions.ServerError as error:
        if error.status != 409 or error.code != "FileAlreadyExists":
            raise
        # Allow retry after OSS succeeded but GitHub Release creation failed.
        existing = bucket.head_object(key)
        if (existing.headers.get("x-oss-meta-sha256") != digest
                or existing.content_length != package.stat().st_size):
            raise ValueError("A different package already exists; publish a new version") from None
    url = CDN + key
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(f"url={url}\n")
    print(f"CDN download: {url}")
    return url


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", nargs="?")
    parser.add_argument("--latest", metavar="VERSION", help="publish the update manifest for an uploaded version")
    parser.add_argument("--release-url")
    args = parser.parse_args()
    if bool(args.package) == bool(args.latest):
        parser.error("pass either a package or --latest VERSION")
    try:
        import oss2
        if args.latest:
            publish_latest(args.latest, oss2, args.release_url)
        else:
            upload(args.package, oss2)
    except ValueError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        # SDK exception bodies can contain request details. Do not log credentials.
        print("OSS upload failed; check credentials, bucket permissions and connectivity.", file=sys.stderr)
        sys.exit(1)
