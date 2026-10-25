"""Resolve provenance in a Git checkout or a verified exported source archive."""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path


def is_checkout(root):
    if shutil.which('git') is None:
        return False
    result = subprocess.run(['git', 'rev-parse', '--show-toplevel'], cwd=root,
                            capture_output=True, text=True, encoding='utf-8')
    return result.returncode == 0 and Path(result.stdout.strip()).resolve() == root.resolve()


def revision(root):
    if is_checkout(root):
        changes = subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=root, text=True, encoding='utf-8')
        if changes.strip():
            raise ValueError('Release requires a clean committed checkout; commit source and evidence first')
        return subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True, encoding='utf-8').strip()
    manifest = json.loads((root / 'SOURCE-FILES-SHA256.json').read_text(encoding='utf-8'))
    actual = set()
    for directory, folders, files in os.walk(root):
        folders[:] = [f for f in folders if f not in ('bin', 'obj', '__pycache__')
                      and not (Path(directory) == root and f in ('artifacts', '.git'))]
        actual.update((Path(directory) / name).relative_to(root).as_posix() for name in files)
    allowed = set(manifest) | {'SOURCE-REVISION', 'SOURCE-FILES-SHA256.json'}
    if actual != allowed:
        raise ValueError('Source inventory changed; initialize Git and commit before making a new release')
    for name, expected in manifest.items():
        file = (root / name).resolve()
        if not file.is_relative_to(root.resolve()) or not file.is_file():
            raise ValueError(f'Missing source file: {name}')
        if hashlib.sha256(file.read_bytes()).hexdigest() != expected:
            raise ValueError(f'Source changed: {name}; initialize Git and commit before making a new release')
    value = (root / 'SOURCE-REVISION').read_text(encoding='ascii').strip()
    if len(value) != 40 or any(c not in '0123456789abcdef' for c in value):
        raise ValueError('Invalid source revision')
    return value


def export(root, output):
    commit = revision(root)
    if is_checkout(root):
        subprocess.run(['git', 'archive', '--format=zip', '--output=' + str(output), 'HEAD'], cwd=root, check=True)
        with zipfile.ZipFile(output, 'a', zipfile.ZIP_DEFLATED) as archive:
            hashes = {i.filename: hashlib.sha256(archive.read(i)).hexdigest()
                      for i in archive.infolist() if not i.is_dir()}
            archive.writestr('SOURCE-REVISION', commit + '\n')
            archive.writestr('SOURCE-FILES-SHA256.json', json.dumps(hashes, indent=2) + '\n')
    else:
        hashes = json.loads((root / 'SOURCE-FILES-SHA256.json').read_text(encoding='utf-8'))
        with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
            for name in [*hashes, 'SOURCE-REVISION', 'SOURCE-FILES-SHA256.json']:
                archive.write(root / name, name)


if __name__ == '__main__':
    print(revision(Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent))
