"""Prepare reviewed license/source companions using only Python's standard library.
Run from any directory. Never changes Git visibility or publishes anything.
"""
from __future__ import annotations
import argparse, hashlib, io, json, os, pathlib, re, subprocess, tempfile, time, urllib.request, zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
AUDIO = {'.rom', '.wav', '.mp3', '.flac', '.ogg', '.nsf', '.nsfe', '.spc', '.gbs', '.hes', '.vgm', '.vgz', '.pdx', '.mdx', '.mp4', '.m4a', '.aac', '.aiff', '.aif', '.au', '.wma', '.opus', '.sf2', '.dls', '.mid', '.midi', '.mod', '.xm', '.s3m', '.it'}
EXCLUDED = {'.git', '.opencode', '.agents', '.codex', 'scratch', 'publish', 'bin', 'obj', '.dotnet', 'serialtestnd8'}
TEXT = {'.cs', '.ini', '.md', '.txt', '.json', '.ps1', '.py', '.xaml', '.xml', '.bat', '.props', '.csproj'}
# A private scenario path, or any path inside a Windows user profile (it names the account).
PRIVATE_PATH = re.compile(r'[A-Za-z]:[\\/][^\r\n]*reSG[^\r\n]*\.txt|[A-Za-z]:[\\/]+Users[\\/]+[^\\/\r\n<>"*?|]', re.I)
# Bundled upstream sources keep their original text; only the application's own files are checked.
UPSTREAM = ('third_party/', 'native/', 'licenses/', 'Assets/')

BINARY = {'.dll', '.exe'}
# Bundled native binaries end up inside mf.exe. Compilers can embed the absolute build path
# (assert/__FILE__ strings), which names the account or the developer's folder layout.
BINARY_PATH = re.compile(r'[A-Za-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+[^\\/\x00-\x1f<>"*?|]+', re.I)

def _build_root_patterns():
    root = str(ROOT)
    return [p.lower() for p in {root, root.replace('\\', '/')}] + [str(ROOT.parent).lower()]

def private_path(name, data):
    suffix = pathlib.PurePosixPath(name).suffix.lower()
    if suffix == '.zip' and name.startswith('Assets/') and name.endswith('/source.zip'):
        # Native source bundles are shipped as-is; any binary placed inside them is checked too.
        with zipfile.ZipFile(io.BytesIO(data)) as nested:
            return any(private_path(name + '/' + entry.filename, nested.read(entry))
                       for entry in nested.infolist() if not entry.is_dir())
    if suffix in BINARY:
        # Native binaries are checked even when they come from bundled upstream sources.
        # UTF-16 strings can start at an odd offset, so both alignments are decoded.
        for text in (data.decode('latin-1'), data.decode('utf-16-le', errors='ignore'), data[1:].decode('utf-16-le', errors='ignore')):
            if BINARY_PATH.search(text): return True
            lowered = text.lower()
            if any(pattern in lowered for pattern in _build_root_patterns()): return True
        return False
    if name.startswith(UPSTREAM) or suffix not in TEXT: return False
    return PRIVATE_PATH.search(data.decode('utf-8-sig', errors='replace')) is not None

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1048576), b''): h.update(block)
    return h.hexdigest()

def source_entry(name):
    p = pathlib.PurePosixPath(name.replace('\\', '/'))
    if p.is_absolute() or '..' in p.parts: raise ValueError('Unsafe source archive entry: ' + name)
    return p

def replace_artifact(source, destination):
    # Drive synchronization/virus scanning can briefly lock an existing ZIP on Windows.
    # Do not delete the previous artifact or fall back to a partial in-place copy.
    for attempt in range(20):
        try:
            os.replace(source, destination)
            return
        except PermissionError:
            if attempt == 19: raise
            time.sleep(1)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=pathlib.Path, required=True)
    parser.add_argument('--download', action='store_true', help='Fetch missing fixed archives, checking SHA-256')
    parser.add_argument('--release', action='store_true', help='Require all recorded release blockers to be closed')
    args = parser.parse_args()
    status = json.loads((ROOT/'licenses/release-status.json').read_text(encoding='utf-8'))
    if args.release and (not status.get('publicReleaseApproved', False) or status['blockers'] or status['notYetVerified']):
        raise SystemExit('Release blocked: see licenses/release-status.json; no release artifact was created.')
    lock = json.loads((ROOT/'licenses/sources.lock.json').read_text(encoding='utf-8'))
    args.cache.mkdir(parents=True, exist_ok=True)
    archives = []
    for item in lock['sources']:
        name = item['archive']
        if pathlib.Path(name).name != name: raise ValueError('Invalid archive name')
        path = args.cache/name
        if not path.exists() and args.download:
            with urllib.request.urlopen(item['url'], timeout=120) as response:
                with tempfile.NamedTemporaryFile(dir=args.cache, delete=False) as f:
                    temp = pathlib.Path(f.name)
                    try:
                        while block := response.read(1048576): f.write(block)
                    except BaseException:
                        f.close(); temp.unlink(missing_ok=True); raise
            try:
                if digest(temp) != item['sha256']: raise ValueError('Downloaded source hash mismatch: ' + name)
                os.replace(temp, path)
            finally: temp.unlink(missing_ok=True)
        if not path.exists() or digest(path) != item['sha256']:
            raise ValueError('Missing or altered corresponding source: ' + name)
        archives.append((item, path))
    # Only versioned files; callers must stage newly added source files first.
    files = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode('utf-8').split('\0')
    files = [name for name in files if name and (ROOT/name).is_file() and not EXCLUDED.intersection(pathlib.PurePosixPath(name).parts)]
    files = [name for name in files if pathlib.Path(name).suffix.lower() not in AUDIO and name not in {'HANDOFF.md', 'AGENTS.md'}]
    required = {'LICENSE', 'COPYRIGHT.txt', 'THIRD_PARTY_NOTICES.md', 'licenses/sources.lock.json', 'native/gme/Build.ps1', 'native/rar/Build.ps1', 'third_party/SharpCompress/SharpCompress.csproj', 'scripts/Build-Magick.ps1', 'scripts/Prepare-MagickSources.py', 'Assets/Image/BUILD.json'}
    if not required.issubset(files): raise ValueError('Stage all new licensing/source files before packaging')
    # Defense against private paths without retaining the actual private path.
    for name in files:
        if private_path(name, (ROOT/name).read_bytes()):
            raise ValueError('Private path found in ' + name)
    destination = ROOT/'publish';destination.mkdir(exist_ok=True)
    label = 'Release' if args.release else 'Review'
    version = re.search(r'<Version>([^<]+)</Version>', (ROOT/'Version.props').read_text()).group(1)
    omitted = []
    with tempfile.TemporaryDirectory(prefix='.license-build-', dir=destination) as work:
        work = pathlib.Path(work)
        notices = work/f'MirrorFlux_{version}_{label}_Licenses.zip'
        sources = work/f'MirrorFlux_{version}_{label}_Sources.zip'
        with zipfile.ZipFile(notices,'w',zipfile.ZIP_DEFLATED) as z:
            for name in files:
                p = pathlib.PurePosixPath(name)
                if name in {'LICENSE','COPYRIGHT.txt','THIRD_PARTY_NOTICES.md'} or name.startswith('licenses/') or (name.startswith('Assets/') and p.suffix.lower() in {'.txt','.json'}):
                    z.write(ROOT/name,name)
        with zipfile.ZipFile(sources,'w',zipfile.ZIP_DEFLATED) as z:
            for name in files: z.write(ROOT/name,'MirrorFlux/'+name)
            for item,path in archives:
                with zipfile.ZipFile(path) as original:
                    for entry in original.infolist():
                        if entry.is_dir(): continue
                        p = source_entry(entry.filename)
                        if p.suffix.lower() in AUDIO:
                            omitted.append({'source':item['archive'],'entry':entry.filename,'reason':'Optional media excluded; code/build files retained'})
                            continue
                        z.writestr('upstream/'+item['name']+'/'+str(p),original.read(entry))
            z.writestr('EXCLUDED-MEDIA.json',json.dumps(omitted,ensure_ascii=False,indent=2))
            z.writestr('BUILD-AND-LICENSE-STATUS.json',json.dumps(status,indent=2))
        report={'version':version,'mode':label,'sourceArchives':len(archives),'sourceFiles':len(files),'omittedMedia':omitted,'artifacts':[]}
        for p in [notices,sources]:
            report['artifacts'].append({'name':p.name,'sha256':digest(p),'bytes':p.stat().st_size})
            replace_artifact(p,destination/p.name)
        (destination/f'MirrorFlux_{version}_{label}_LicenseManifest.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k!='omittedMedia'},indent=2))
        print('Optional media excluded:',len(omitted))

if __name__ == '__main__': main()
