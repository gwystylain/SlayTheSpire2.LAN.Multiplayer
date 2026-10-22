"""Build a release: the DLL, a pck carrying localization/, and the manifest, zipped by version.

  python tools/package.py --sts2-data-dir "<game>/data_sts2_windows_x86_64"

--sts2-data-dir is the folder holding the game's sts2.dll, 0Harmony.dll, GodotSharp.dll and
Steamworks.NET.dll (STS2_DATA_DIR works too; without either the .csproj default is used).
The version comes from mod_manifest.json and must match the .csproj's AssemblyVersion.

Writes, under dist/:
  mods/SlayTheSpire2.LAN.Multiplayer/   the mod folder; the Workshop upload's contentfolder
  SlayTheSpire2.LAN.Multiplayer-v<version>.zip   that folder as mods/..., for GitHub Releases
  workshop_item.vdf                     tools/workshop_item.vdf with this build's paths filled in
  release-notes.md                      a starting point for the GitHub release's notes
"""
import argparse
import json
import os
import re
import shutil
import string
import subprocess
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pck_tool  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
MOD_ID = "SlayTheSpire2.LAN.Multiplayer"
CSPROJ = ROOT / MOD_ID / f"{MOD_ID}.csproj"
MANIFEST = ROOT / "mod_manifest.json"
LOCALIZATION = ROOT / "localization"
PCK_BASE = ROOT / "tools" / "pck_base.pck"
VDF_TEMPLATE = ROOT / "tools" / "workshop_item.vdf"
PREVIEW = ROOT / "mod_image.png"
GAME_DLLS = ("sts2.dll", "0Harmony.dll", "GodotSharp.dll", "Steamworks.NET.dll")
REPO = "gwystylain/SlayTheSpire2.LAN.Multiplayer"
UPSTREAM = "kmyuhkyuk/SlayTheSpire2.LAN.Multiplayer"


def fail(message):
    sys.exit(f"error: {message}")


def text_bytes(path):
    """A text file's bytes with LF endings, so a CRLF checkout packs the same bytes as git holds."""
    return path.read_bytes().replace(b"\r\n", b"\n")


def read_version():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if manifest.get("id") != MOD_ID:
        fail(f"{MANIFEST.name} id is {manifest.get('id')!r}, expected {MOD_ID!r}")
    version = manifest.get("version", "")
    # The game parses this as a semantic version; the join handshake compares "id-version".
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        fail(f"{MANIFEST.name} version {version!r} is not MAJOR.MINOR.PATCH")

    csproj = CSPROJ.read_text(encoding="utf-8-sig")
    for prop in ("AssemblyVersion", "FileVersion"):
        found = re.search(rf"<{prop}>([^<]*)</{prop}>", csproj)
        if not found or found.group(1) != f"{version}.0":
            fail(f"{CSPROJ.name} {prop} is {found and found.group(1)!r}, expected {version}.0 "
                 f"to match {MANIFEST.name}; bump both together")
    tfm = re.search(r"<TargetFramework>([^<]*)</TargetFramework>", csproj).group(1)
    return version, tfm


def build_dll(data_dir, tfm):
    env = os.environ.copy()
    # The .NET 9 SDK is a user-local install; use it when it's there.
    dotnet_root = Path.home() / ".dotnet"
    if (dotnet_root / "dotnet.exe").exists() or (dotnet_root / "dotnet").exists():
        env["DOTNET_ROOT"] = str(dotnet_root)
        env["PATH"] = str(dotnet_root) + os.pathsep + env.get("PATH", "")
    dotnet = shutil.which("dotnet", path=env["PATH"])
    if not dotnet:
        fail("dotnet not found in ~/.dotnet or on PATH")

    cmd = [dotnet, "build", str(CSPROJ), "-c", "Release", "-nologo", "-v:minimal"]
    if data_dir:
        data_dir = Path(os.path.normpath(data_dir))  # a trailing backslash would escape msbuild's quote
        missing = [d for d in GAME_DLLS if not (data_dir / d).is_file()]
        if missing:
            fail(f"{data_dir} lacks {', '.join(missing)}")
        cmd.append(f"-p:Sts2DataDir={data_dir}")
    print("$", subprocess.list2cmdline(cmd), flush=True)
    if subprocess.run(cmd, cwd=ROOT, env=env).returncode != 0:
        fail("dotnet build failed")
    dll = CSPROJ.parent / "bin" / "Release" / tfm / f"{MOD_ID}.dll"
    if not dll.is_file():
        fail(f"build succeeded but {dll} is missing")
    return dll


def build_pck(template):
    """The template's entries, with everything under <id>/localization/ taken from localization/."""
    header, entries, _, _ = pck_tool.read_pck(template)
    prefix = f"{MOD_ID}/localization"
    added = []
    languages = {}
    for path in sorted(LOCALIZATION.glob("*/*.json")):
        data = text_bytes(path)
        try:
            json.loads(data.decode("utf-8"))
        except ValueError as e:
            fail(f"{path.relative_to(ROOT)} is not valid JSON: {e}")
        languages.setdefault(path.parent.name, []).append(path.name)
        added.append((f"{prefix}/{path.parent.name}/{path.name}", data))
    if not languages:
        fail(f"no localization files under {LOCALIZATION}")
    expected = languages.get("eng") or next(iter(languages.values()))
    for lang, files in languages.items():
        if files != expected:
            fail(f"localization/{lang} has {files}, but the other languages have {expected}")

    merged = pck_tool.replace_prefix(entries, prefix, added)
    kept = len(merged) - len(added)
    print(f"pck: {kept} entries kept from {template.name}, {len(languages)} languages x "
          f"{len(expected)} files from localization/")
    return pck_tool.write_pck(header, merged), merged


def git(*args):
    result = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else ""


def vdf_string(value):
    # KeyValues strings take backslash escapes, so a Windows path's separators are doubled.
    return str(value).replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--sts2-data-dir", default=os.environ.get("STS2_DATA_DIR"),
                        help="folder with the game's sts2.dll and friends (default: $STS2_DATA_DIR, "
                             "else the .csproj default)")
    parser.add_argument("--template", type=Path, default=PCK_BASE,
                        help="pck whose non-localization entries are kept (default: tools/pck_base.pck)")
    parser.add_argument("--out", type=Path, default=ROOT / "dist", help="output folder (default: dist/)")
    parser.add_argument("--changes", default="",
                        help="what changed in this version; leads the Workshop change note and the release notes")
    args = parser.parse_args()

    version, tfm = read_version()
    commit = git("rev-parse", "HEAD")
    dirty = bool(git("status", "--porcelain"))
    print(f"{MOD_ID} {version} from {commit[:12] or 'unknown commit'}{' with uncommitted changes' if dirty else ''}")

    dll = build_dll(args.sts2_data_dir, tfm)
    pck, pck_entries = build_pck(args.template)

    out = args.out.resolve()
    stage = out / "mods" / MOD_ID
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)
    shutil.copyfile(dll, stage / f"{MOD_ID}.dll")
    (stage / f"{MOD_ID}.pck").write_bytes(pck)
    (stage / "mod_manifest.json").write_bytes(text_bytes(MANIFEST))
    if pck_tool.read_pck(stage / f"{MOD_ID}.pck")[1] != pck_entries:
        fail("the written pck did not read back as built")

    zip_path = out / f"{MOD_ID}-v{version}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in sorted(stage.iterdir()):
            zf.write(f, f"mods/{MOD_ID}/{f.name}")

    built_from = f"v{version}, built from https://github.com/{REPO}/tree/{commit or 'master'}"
    vdf = string.Template(VDF_TEMPLATE.read_text(encoding="utf-8")).substitute(
        contentfolder=vdf_string(stage),
        previewfile=vdf_string(PREVIEW),
        changenote=vdf_string(f"{args.changes}\n\n{built_from}" if args.changes else built_from),
    )
    (out / "workshop_item.vdf").write_text(vdf, encoding="utf-8")

    (out / "release-notes.md").write_text(
        f"{args.changes or f'<!-- What changed in v{version}. -->'}\n\n"
        "## Install\n"
        f"Extract the zip into the game folder, so the files land in `mods/{MOD_ID}/`. "
        "Everyone in a run needs the same version; joining with a different one fails and names it.\n\n"
        "## Credits\n"
        f"A fork of [kmyuhkyuk's {MOD_ID}](https://github.com/{UPSTREAM}), licensed GPL-3.0. "
        f"Source for this build: https://github.com/{REPO}/tree/{commit or 'master'}\n",
        encoding="utf-8")

    print(f"staged  {stage}")
    print(f"zip     {zip_path}")
    print(f"vdf     {out / 'workshop_item.vdf'}")
    print(f"notes   {out / 'release-notes.md'}")
    print("\nto publish, after writing what changed into the notes:")
    print(f'  gh release create v{version} "{zip_path}" --repo {REPO} --target {commit or "<commit>"} '
          f'--title v{version} --notes-file "{out / "release-notes.md"}"')
    print(f'  steamcmd +login <your Steam account name> +workshop_build_item "{out / "workshop_item.vdf"}" +quit')
    if re.search(r'^\s*"publishedfileid"\s+"0"', vdf, re.MULTILINE):
        print("note: publishedfileid is 0, so a Workshop upload creates a NEW item; after the first one, "
              "put its id in tools/workshop_item.vdf")
    if dirty:
        print("warning: the working tree has uncommitted changes, so no commit matches this build")
    elif commit and not git("branch", "-r", "--contains", commit):
        print("warning: this commit isn't on any remote branch yet; push it before gh release create")


if __name__ == "__main__":
    main()
