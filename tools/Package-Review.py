"""Package the reviewed source, runnable Release output, and measurement evidence."""
from pathlib import Path
import hashlib, json, zipfile
ROOT=Path(__file__).resolve().parents[1]
archive=ROOT/'artifacts/review-deliverables.zip'
paths=set()
def add_tree(relative, allow_binaries=False):
    for p in (ROOT/relative).rglob('*'):
        if not p.is_file(): continue
        if not allow_binaries and any(part in ['bin','obj','__pycache__'] for part in p.relative_to(ROOT/relative).parts): continue
        if p.suffix in ['.pyc','.dmp','.etl']: continue
        paths.add(p)
for directory in ['src','tests','tools','docs']: add_tree(directory)
for directory in ['artifacts/review-performance-final','artifacts/review-restart-final','artifacts/m3-product-verified','artifacts/review-baseline/app','artifacts/review-baseline/driver','artifacts/review-pre-link-policy','artifacts/link-final']:
    add_tree(directory,True)
for p in (ROOT/'src/PaneCommander.App/bin/Release/net10.0-windows').glob('*'):
    if p.is_file() and p.suffix in ['.exe','.dll','.json']: paths.add(p)
for p in ROOT.iterdir():
    if p.is_file() and (p.suffix in ['.md','.sln','.props'] or p.name in ['global.json','.gitignore']): paths.add(p)
for relative in ['artifacts/review-gui/verification.json','artifacts/review-baseline/source-manifest.json','artifacts/link-gui/2e005808d2734b859a42ff42928119ca/verification.json']:
    paths.add(ROOT/relative)
manifest=[{'Path':p.relative_to(ROOT).as_posix(),'SHA256':hashlib.sha256(p.read_bytes()).hexdigest().upper()} for p in sorted(paths)]
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(paths): z.write(p,p.relative_to(ROOT).as_posix())
    z.writestr('PACKAGE_MANIFEST.json',json.dumps(manifest,ensure_ascii=False,indent=2))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    stored=json.loads(z.read('PACKAGE_MANIFEST.json'))
    for e in stored: assert hashlib.sha256(z.read(e['Path'])).hexdigest().upper()==e['SHA256']
result={'Archive':str(archive),'Files':len(manifest),'Bytes':archive.stat().st_size,'SHA256':hashlib.sha256(archive.read_bytes()).hexdigest().upper(),'CrcVerified':True,'EveryEntryShaVerified':True}
(ROOT/'artifacts/review-deliverables-verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
