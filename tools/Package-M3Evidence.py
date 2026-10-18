"""Package only final M3 evidence, sources, and reproduction tools."""
from pathlib import Path
import hashlib,json,zipfile
root=Path(__file__).resolve().parents[1]; evidence=root/'docs/verification/m3'
for folder,count in [('product',30),('repeat',12)]:
    result=json.loads((evidence/folder/'validation.json').read_text(encoding='utf-8-sig'))
    assert result['complete'] and result['runs']==count
paths=[]
for folder in ['product','repeat','restart','software','hardware']:
    paths.extend((evidence/folder).rglob('*'))
paths.extend(evidence.glob('*.txt')); paths.extend(evidence.glob('probe-*.json'))
paths.append(evidence/'HUMAN_GUI_REVIEW.md')
for folder in ['src','tests']:
    paths.extend(p for p in (root/folder).rglob('*') if not {'bin','obj'}.intersection(p.relative_to(root/folder).parts))
for name in ['README.md','IMPLEMENTATION_STATUS.md','DECISIONS.md','M3_IMPLEMENTATION_PLAN.md','M3_IMPLEMENTATION_REPORT.md',
             'M3_PERFORMANCE_REPORT.md','M3_RESOURCE_REPEAT_REPORT.md','PaneCommander.sln','Directory.Build.props','global.json',
             'docs/spec/m3_persistence.md','tools/Measure-M3Product.ps1','tools/Verify-M3Restart.ps1',
             'tools/Analyze-M3Product.py','tools/Analyze-M3Repeat.py','tools/Package-M3Evidence.py',
             'tools/Compare-Rendering.ps1','tools/requirements-render-analysis.txt']:
    paths.append(root/name)
for project in ['PaneCommander.RenderMonitor','PaneCommander.Performance','PaneCommander.M3Probe']:
    paths.extend((root/'tools'/project).glob('*.cs')); paths.extend((root/'tools'/project).glob('*.csproj'))
paths=sorted({p for p in paths if p.is_file()})
manifest={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
manifest_file=evidence/'m3-evidence-sha256.json'; manifest_file.write_text(json.dumps(manifest,indent=2,ensure_ascii=False),encoding='utf-8')
paths.append(manifest_file); archive=root/'artifacts/m3-deliverables.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for path in paths: z.write(path,path.relative_to(root).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and len(z.namelist())==len(paths)
    for name,digest in manifest.items(): assert hashlib.sha256(z.read(name)).hexdigest()==digest
print(f'{archive}: {len(paths)} files, {archive.stat().st_size/1e6:.2f} MB; SHA256 and ZIP CRC verified.')
