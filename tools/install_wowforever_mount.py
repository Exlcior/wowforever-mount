"""Build and install the World of Warcraft Forever mount into the local s&box engine, or remove it.

The mount reads the player's installed game; nothing from the game is copied. install refuses to
replace a DLL it did not install (recorded by hash); remove deletes only that recorded file.
Usage: python -B tools/install_wowforever_mount.py build|install|remove|status
"""
import argparse, hashlib, json, subprocess, shutil, time
from pathlib import Path
from sbox_cli import find_cli

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'source/mount/wowforever/WowForever.Mount.csproj'
OUTPUT = ROOT / 'tmp/wowforever-mount-build'
RECORD = ROOT / 'validation/wowforever-mount-installation.json'
ASSETS = ROOT / 'source/mount/wowforever/Assets'


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def engine():
    return find_cli().parents[2].resolve()


def build():
    result = subprocess.run(['dotnet', 'build', str(PROJECT), '-c', 'Release', '-nologo', '-v', 'q',
                             f'-p:SboxEngineDir={engine()}', f'-p:OutputPath={OUTPUT}/'], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stdout[-4000:] + result.stderr[-2000:])
    return OUTPUT / 'wowforever.dll'


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('mode', choices=['build', 'install', 'remove', 'status'])
    args = ap.parse_args()
    directory = engine() / 'mount/wowforever'; target = directory / 'wowforever.dll'
    record = json.loads(RECORD.read_text()) if RECORD.exists() else {}
    if args.mode == 'build':
        source = build(); print(json.dumps({'built': str(source), 'sha256': sha(source)})); return
    if args.mode == 'status':
        print(json.dumps({'target': str(target), 'present': target.exists(), 'sha256': sha(target) if target.exists() else None, 'record': record}, indent=1)); return
    if args.mode == 'install':
        source = build()
        if target.exists() and sha(target) != record.get('sha256'):
            raise RuntimeError('A wowforever.dll not installed by this tool exists; preserve it.')
        directory.mkdir(parents=True, exist_ok=True)
        # A running s&box keeps the loaded DLL locked: Windows still allows renaming it aside,
        # and the engine only loads <dir>/<dir>.dll, so the new build is picked up at the next start.
        for old in directory.glob('wowforever.dll.old-*'):
            try: old.unlink()
            except PermissionError: pass
        if target.exists():
            try: target.unlink()
            except PermissionError: target.rename(directory / f'wowforever.dll.old-{int(time.time())}')
        shutil.copy2(source, target)
        # The mount's own shaders (no game data) sit beside the DLL, like the engine's other mounts.
        assets = []
        for file in sorted(ASSETS.rglob('*')):
            if file.is_file() and file.suffix in ('.shader', '.shader_c'):
                destination = directory / 'Assets' / file.relative_to(ASSETS)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, destination); assets.append(str(destination))
        record = {'target': str(target), 'sha256': sha(target), 'installedAt': time.time(), 'assets': assets}
    else:
        if target.exists():
            if sha(target) != record.get('sha256'): raise RuntimeError('Installed DLL changed; preserve it.')
            target.unlink()
        for asset in record.get('assets', []):
            if Path(asset).exists(): Path(asset).unlink()
        for folder in sorted((directory / 'Assets').rglob('*'), reverse=True) if (directory / 'Assets').exists() else []:
            if folder.is_dir() and not any(folder.iterdir()): folder.rmdir()
        if (directory / 'Assets').exists() and not any((directory / 'Assets').iterdir()): (directory / 'Assets').rmdir()
        if directory.exists() and not any(directory.iterdir()): directory.rmdir()
        record = {'target': str(target), 'removedAt': time.time(), 'removed': not target.exists()}
    RECORD.parent.mkdir(parents=True, exist_ok=True); RECORD.write_text(json.dumps(record, indent=1) + '\n')
    print(json.dumps(record, indent=1))


if __name__ == '__main__':
    main()
