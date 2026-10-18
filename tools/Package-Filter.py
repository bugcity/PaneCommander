from pathlib import Path
import hashlib,json,zipfile
root=Path(__file__).resolve().parents[1];paths=set()
def add(folder,binaries=False):
    for p in (root/folder).rglob('*'):
        if p.is_file() and p.suffix not in ['.dmp','.etl','.pyc'] and (binaries or not any(x in ['bin','obj','__pycache__'] for x in p.relative_to(root/folder).parts)):paths.add(p)
for folder in ['src','tests','tools','docs']:add(folder)
for folder in ['artifacts/filter-before/app','artifacts/filter-comparison-complete']:add(folder,True)
paths.add(root/'artifacts/filter-comparison/EXCLUDED.json');paths.add(root/'artifacts/filter-gui/c622a1f014274d42b8135b73ea9e5726/verification.json')
paths.add(root/'artifacts/filter-comparison-verified/EXCLUDED.json')
for p in root.iterdir():
    if p.is_file() and (p.suffix in ['.md','.sln','.props'] or p.name in ['global.json','.gitignore']):paths.add(p)
for p in (root/'src/PaneCommander.App/bin/Release/net10.0-windows').glob('*'):
    if p.suffix in ['.exe','.dll','.json']:paths.add(p)
frozen=root/'artifacts/filter-comparison-complete/builds/after/PaneCommander.App.dll'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
assert sha(frozen)==sha(root/'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll')
for p in (root/'src/PaneCommander.App/bin/Release/net10.0-windows').glob('*'):
    if p.suffix in ['.exe','.dll','.json']:assert sha(p)==sha(frozen.parent/p.name),p.name
manifest=[{'Path':p.relative_to(root).as_posix(),'SHA256':sha(p)} for p in sorted(paths)]
archive=root/'artifacts/filter-deliverables.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(paths):z.write(p,p.relative_to(root).as_posix())
    z.writestr('PACKAGE_MANIFEST.json',json.dumps(manifest,ensure_ascii=False,indent=2))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for e in json.loads(z.read('PACKAGE_MANIFEST.json')):assert hashlib.sha256(z.read(e['Path'])).hexdigest().upper()==e['SHA256']
result={'Archive':str(archive),'Files':len(paths),'Bytes':archive.stat().st_size,'SHA256':sha(archive),'AllEntriesVerified':True,'CurrentProductMatchesMeasuredBuild':True}
(root/'artifacts/filter-deliverables-verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))
