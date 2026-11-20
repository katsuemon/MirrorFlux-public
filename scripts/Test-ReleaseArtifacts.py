"""Audit matching portable/source/notice artifacts without publishing or changing approval."""
from __future__ import annotations
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import runpy
import struct
import subprocess
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[1]
RULES = runpy.run_path(str(ROOT / 'scripts/Build-LicenseBundle.py'))
MEDIA, EXCLUDED = RULES['AUDIO'], RULES['EXCLUDED']


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def inspect_zip(z, label, exclude_media=False):
    names = z.namelist()
    require(len(names) == len(set(names)), f'Duplicate ZIP entries: {label}')
    require(z.testzip() is None, f'ZIP integrity: {label}')
    for name in names:
        path = RULES['source_entry'](name)
        require(not {'.git', '.opencode', '.codex', '.agents', 'serialtestnd8'}.intersection(path.parts), f'Private history/metadata: {label}/{name}')
        if exclude_media:
            require(path.suffix.lower() not in MEDIA, f'Excluded media: {label}/{name}')
    return names


def bundle_entries(data):
    signature = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
    marker = data.index(signature)
    pos = struct.unpack_from('<q', data, marker - 8)[0]
    major, minor, count = struct.unpack_from('<III', data, pos)
    require(major in (2, 6), f'Unsupported .NET bundle version: {major}.{minor}')
    pos += 12
    def read_string():
        nonlocal pos
        size = shift = 0
        while True:
            n = data[pos]; pos += 1
            size |= (n & 127) << shift
            if n < 128:
                break
            shift += 7
            require(shift <= 28, 'Invalid bundle string')
        value = data[pos:pos + size].decode('utf-8'); pos += size
        return value
    read_string()
    pos += 40
    entries = {}
    for _ in range(count):
        offset, size = struct.unpack_from('<qq', data, pos)
        compressed = struct.unpack_from('<q', data, pos + 16)[0] if major >= 6 else 0
        pos += 16 + (8 if major >= 6 else 0) + 1
        name = read_string()
        blob = data[offset:offset + (compressed or size)]
        # Compressed single-file entries are raw Deflate; their strings are invisible in the EXE bytes.
        entries[name] = zlib.decompress(blob, -15) if compressed else blob
        require(len(entries[name]) == size, f'Bundle entry size mismatch: {name}')
    return entries


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for flag in ('portable', 'sources', 'notices', 'manifest', 'cache', 'report'):
        p.add_argument('--' + flag, type=Path, required=True)
    a = p.parse_args()
    version = re.search(r'<Version>([^<]+)</Version>', (ROOT/'Version.props').read_text()).group(1)
    manifest = json.loads(a.manifest.read_text(encoding='utf-8'))
    require(manifest['version'] == version, 'Companion manifest version does not match the project')
    for artifact in (a.sources, a.notices):
        record = next((x for x in manifest['artifacts'] if x['name'] == artifact.name), None)
        require(record is not None and record['sha256'] == RULES['digest'](artifact), f'Manifest hash mismatch: {artifact.name}')
    exe_path = ROOT/'publish/win-x64/mf.exe'
    command = "[Diagnostics.FileVersionInfo]::GetVersionInfo('" + str(exe_path).replace("'", "''") + "').ProductVersion"
    exe_version = subprocess.check_output(['powershell.exe', '-NoProfile', '-Command', command], text=True).strip().split('+')[0]
    require(exe_version == version, f'EXE version mismatch: {exe_version} vs {version}')
    with zipfile.ZipFile(a.portable) as portable:
        names = inspect_zip(portable, 'portable', True)
        top = {'mf.exe', 'mf.ini', 'README.txt', 'EXTERNAL-DATA.txt', 'LICENSE.txt', 'COPYRIGHT.txt', 'THIRD_PARTY_NOTICES.md'}
        expected_licenses = {'licenses/' + p.relative_to(ROOT/'licenses').as_posix() for p in (ROOT/'licenses').rglob('*') if p.is_file() and p.name != 'release-status.json'}
        require(set(n for n in names if not n.endswith('/')) == top | expected_licenses,
                'Portable must hold the EXE, INI, guides, license texts and licenses/ only')
        require(portable.read('README.txt') == (ROOT/'docs/readme-dist.txt').read_bytes(), 'Portable README differs')
        require(portable.read('LICENSE.txt') == (ROOT/'LICENSE').read_bytes(), 'Portable LICENSE differs')
        exe = portable.read('mf.exe')
        require(sha(exe) == RULES['digest'](exe_path), 'Portable EXE does not match the published EXE')
        require(portable.read('mf.ini') == (ROOT/'mf.default.ini').read_bytes(), 'Portable INI differs from the default template')
        require(portable.read('EXTERNAL-DATA.txt') == (ROOT/'docs/external-data.txt').read_bytes(), 'Portable external-data guide differs')
    bundle = bundle_entries(exe)
    require("MirrorFlux.App.dll" in bundle and not any(Path(name).name.lower() == "mf.dll" for name in bundle),
            "Application assembly must not shadow Windows Media Foundation MF.dll")
    require(not any(any(term in name.lower() for term in ('sqlclient', 'sni.dll', 'mdsound', 'libvgm', 'magick.native-q8-x64.dll')) for name in bundle), 'Removed native/runtime component remains in the EXE bundle')
    # Managed DLLs can carry a PDB path, and embedded native DLLs assert paths; check every bundled binary.
    require(not RULES['private_path']('mf.exe', exe), 'Private path in the published EXE')
    for name, data in bundle.items():
        require(not RULES['private_path'](name, data), f'Private path in EXE bundle entry: {name}')
    tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode('utf-8').split('\0')
    files = [name for name in tracked if name and (ROOT/name).is_file() and not EXCLUDED.intersection(PurePosixPath(name).parts)
             and Path(name).suffix.lower() not in MEDIA and name not in {'HANDOFF.md', 'AGENTS.md'}]
    lock = json.loads((ROOT/'licenses/sources.lock.json').read_text(encoding='utf-8'))
    require(not {'de265', 'openh264', 'sharpcompress'}.intersection(x['name'].lower() for x in lock['sources']), 'Excluded upstream component in source lock')
    tree = hashlib.sha256()
    upstream_count = 0
    with zipfile.ZipFile(a.sources) as sources, zipfile.ZipFile(a.notices) as notices:
        names = inspect_zip(sources, 'sources', True)
        notice_names = inspect_zip(notices, 'notices', True)
        expected = {'MirrorFlux/' + name for name in files} | {'EXCLUDED-MEDIA.json', 'BUILD-AND-LICENSE-STATUS.json'}
        expected_notices = set()
        for name in files:
            data = (ROOT/name).read_bytes()
            require(sources.read('MirrorFlux/' + name) == data, f'Project source mismatch: {name}')
            tree.update((name + '\0' + sha(data) + '\n').encode('utf-8'))
            suffix = Path(name).suffix.lower()
            require(not RULES['private_path'](name, data), f'Private path: {name}')
            if name in {'LICENSE', 'COPYRIGHT.txt', 'THIRD_PARTY_NOTICES.md'} or name.startswith('licenses/') or (name.startswith('Assets/') and suffix in {'.txt', '.json'}):
                expected_notices.add(name)
                require(notices.read(name) == data, f'Notice mismatch: {name}')
            if name.startswith('Assets/') and name.endswith('/source.zip'):
                with zipfile.ZipFile(io.BytesIO(data)) as native:
                    inspect_zip(native, name, True)
        require(set(notice_names) == expected_notices, 'Missing or extra notice entries')
        omitted = []
        for component in lock['sources']:
            archive = a.cache/component['archive']
            require(RULES['digest'](archive) == component['sha256'], 'Changed fixed source archive: ' + component['name'])
            with zipfile.ZipFile(archive) as original:
                for entry in original.infolist():
                    if entry.is_dir():
                        continue
                    path = RULES['source_entry'](entry.filename)
                    if path.suffix.lower() in MEDIA:
                        omitted.append({'source': component['archive'], 'entry': entry.filename, 'reason': 'Optional media excluded; code/build files retained'})
                        continue
                    name = 'upstream/' + component['name'] + '/' + str(path)
                    require(sources.read(name) == original.read(entry), 'Upstream source mismatch: ' + name)
                    expected.add(name); upstream_count += 1
        require(set(names) == expected, 'Missing or extra source entries')
        require(json.loads(sources.read('EXCLUDED-MEDIA.json')) == omitted, 'Excluded-media manifest mismatch')
        require(json.loads(sources.read('BUILD-AND-LICENSE-STATUS.json')) == json.loads((ROOT/'licenses/release-status.json').read_text()), 'Release status mismatch')
        require(not any('/Compressors/Rar/' in name or '/Archives/Rar/' in name for name in names), 'Removed SharpCompress RAR code present')
    report = {'version': version, 'publicationPerformed': False, 'result': 'passed', 'checks': {
        'executableVersion': exe_version, 'portableEntries': 3, 'defaultIniExactMatch': True,
        'projectFilesExactMatch': len(files), 'fixedUpstreamArchives': len(lock['sources']),
        'upstreamFilesExactMatch': upstream_count, 'noticeFilesExactMatch': len(notice_names),
        'sourceArchiveEntries': len(names), 'excludedMediaAndHistory': True,
        'removedComponentsAbsent': True, 'publishedBundleEntries': len(bundle)},
        'projectTreeSha256': tree.hexdigest(), 'executableSha256': sha(exe),
        'artifacts': [{'name': f.name, 'sha256': RULES['digest'](f), 'bytes': f.stat().st_size} for f in (a.portable, a.sources, a.notices)]}
    a.report.parent.mkdir(parents=True, exist_ok=True)
    a.report.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
