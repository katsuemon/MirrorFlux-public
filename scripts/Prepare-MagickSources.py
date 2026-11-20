"""Prepare fixed Magick sources from verified archives or an extracted source companion."""
import argparse, hashlib, json, pathlib, shutil, zipfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--work', type=pathlib.Path, required=True)
g = p.add_mutually_exclusive_group(required=True)
g.add_argument('--cache', type=pathlib.Path)
g.add_argument('--bundle-root', type=pathlib.Path)
a = p.parse_args()
work = a.work.resolve()
if work.exists() and any(work.iterdir()): raise SystemExit('Work directory must be empty; use a new directory or resume the existing build.')
work.mkdir(parents=True, exist_ok=True)
lock = json.loads((ROOT/'licenses/sources.lock.json').read_text(encoding='utf-8'))
components = []
for item in lock['sources']:
    name = item['name']
    if name == 'Configure': target = work/'Configure'
    elif name == 'Dependencies': target = work/'Dependencies'
    elif name == 'Magick.Native': target = work/'Native'
    elif name == 'ImageMagick': target = work/'Native/src/ImageMagick/ImageMagick'
    elif item['repository'].startswith('https://github.com/ImageMagick/'):
        target = work/'Dependencies'/('NonWindowsDependencies' if name == 'fontconfig' else 'Dependencies')/name
    else: continue
    if a.cache:
        archive = a.cache/item['archive']
        if hashlib.sha256(archive.read_bytes()).hexdigest() != item['sha256']: raise SystemExit('Source hash mismatch: '+name)
        with zipfile.ZipFile(archive) as z:
            for entry in z.infolist():
                if entry.is_dir(): continue
                rel = pathlib.PurePosixPath(entry.filename)
                if rel.is_absolute() or '..' in rel.parts or len(rel.parts)<2: raise ValueError('Invalid archive entry')
                dest = target.joinpath(*rel.parts[1:]); dest.parent.mkdir(parents=True,exist_ok=True); dest.write_bytes(z.read(entry))
    else:
        candidates = list((a.bundle_root/'upstream'/name).iterdir())
        if len(candidates)!=1 or not candidates[0].is_dir(): raise SystemExit('Unexpected source companion structure: '+name)
        # The companion includes editable source. Its release ZIP hash authenticates the original snapshot.
        shutil.copytree(candidates[0],target,dirs_exist_ok=True)
    components.append({'name':name,'commit':item['commit']})
# Public build: remove codec implementations and their libheif plugin registrations.
heif = work/'Dependencies/Dependencies/heif/.ImageMagick/Config.txt'
text = heif.read_text(encoding='utf-8-sig')
for line in ['HAVE_LIBDE265', 'HAVE_OpenH264_DECODER', 'LIBDE265_STATIC_BUILD', 'de265', 'openh264']:
    if text.splitlines().count(line) != 1: raise ValueError('Unexpected heif config: '+line)
    text = '\n'.join(x for x in text.splitlines() if x != line)+'\n'
text = text.replace('[EXCLUDES]\n','[EXCLUDES]\nlibheif\\plugins\\decoder_libde265.cc\nlibheif\\plugins\\decoder_openh264.cc\n')
heif.write_text(text,encoding='utf-8')
header = work/'Native/src/Magick.Native/Stdafx.h'
text = header.read_text(encoding='utf-8-sig')
for name in ['de265','openh264']:
    line='  MAGICK_NATIVE_LINK_LIB('+name+')'
    if text.splitlines().count(line)!=1: raise ValueError('Unexpected Magick.Native link declarations: '+name)
    text='\n'.join(x for x in text.splitlines() if x!=line)+'\n'
header.write_text(text,encoding='utf-8')
(work/'.mirrorflux-magick-source.json').write_text(json.dumps({'profile':'no-avc-hevc','components':components,'patches':['heif/.ImageMagick/Config.txt: disable/remove de265 and OpenH264 plugins, definitions and references','Magick.Native/Stdafx.h: remove de265 and OpenH264 static link directives']},indent=2)+'\n')
print('Prepared',len(components),'fixed source components in',work)
