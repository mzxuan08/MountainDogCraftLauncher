"""Verify hashes, executable architectures, bundle modes, licenses and source inventory."""
import argparse
import hashlib
import io
import json
import struct
import tarfile
import zipfile
from pathlib import Path


def executable(data, rid):
    if rid == 'win-x64':
        assert data[:2] == b'MZ'
        position = struct.unpack_from('<I', data, 60)[0]
        assert data[position:position + 6] == b'PE\0\0\x64\x86'
    else:
        assert data[:4] == b'\xcf\xfa\xed\xfe'
        assert struct.unpack_from('<I', data, 4)[0] == (0x0100000C if rid == 'osx-arm64' else 0x01000007)


def verify(root):
    sums = json.loads((root / 'SHA256SUMS.json').read_text(encoding='utf-8'))
    for record in sums:
        path = root / record['path']
        assert path.stat().st_size == record['size'], path
        assert hashlib.sha256(path.read_bytes()).hexdigest() == record['sha256'], path
    release = json.loads((root / 'release-manifest.json').read_text(encoding='utf-8'))
    assert {t['rid'] for t in release['targets']} == {'win-x64', 'osx-arm64', 'osx-x64'}
    for target in release['targets']:
        rid = target['rid']
        package = root / target['package']
        if rid == 'win-x64':
            with zipfile.ZipFile(package) as archive:
                archive.testzip() is None or (_ for _ in ()).throw(ValueError('CRC failure'))
                files = {name: archive.read(name) for name in archive.namelist() if not name.endswith('/')}
            base, resources, suffix = '', '', '.exe'
        else:
            with tarfile.open(package, 'r:gz') as archive:
                members = archive.getmembers()
                assert not any(m.issym() or m.islnk() for m in members)
                for member in members:
                    if member.name.endswith(('/MDCL.Desktop', '/MDCL.Updater')):
                        assert member.mode & 0o111 == 0o111
                files = {m.name: archive.extractfile(m).read() for m in members if m.isfile()}
            base, resources, suffix = 'MDCL.app/Contents/MacOS/', 'MDCL.app/Contents/Resources/', ''
            assert 'MDCL.app/Contents/Info.plist' in files
            assert 'UNSIGNED-UNVERIFIED.txt' in files
        executable(files[base + 'MDCL.Desktop' + suffix], rid)
        executable(files[base + 'updater/MDCL.Updater' + suffix], rid)
        assert json.loads(files[base + 'build-info.json'].decode('utf-8-sig'))['commit'] == release['commit']
        assert resources + 'LICENSE' in files
        assert resources + 'licenses/dependencies.json' in files
        assert any('dependency-lockfiles/' in name for name in files)
        runtime = json.loads(files[base + 'MDCL.Desktop.runtimeconfig.json'])['runtimeOptions']
        assert 'includedFrameworks' in runtime, 'Not self-contained'
    with zipfile.ZipFile(root / release['sourceArchive']) as archive:
        names = archive.namelist()
        assert 'packaging/publish.ps1' in names and 'packaging/publish.sh' in names
        assert 'LICENSE' in names and 'global.json' in names
        assert archive.read('SOURCE-REVISION').decode().strip() == release['commit']
        for name in names:
            parts = Path(name).parts
            assert not any(part in ('bin', 'obj', 'artifacts', '.git') for part in parts), name
            assert not name.endswith(('.pem', '.pfx', '.key', '.jks', '.jar', '.part')), name
    result = {'version': release['version'], 'commit': release['commit'],
              'hashesVerified': len(sums), 'ridPackagesVerified': 3,
              'sourceVerified': True, 'macRuntimeTested': False, 'signed': False}
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    verify(parser.parse_args().directory.resolve())
