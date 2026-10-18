"""Package current source, Release output and evidence, without installed third-party DLLs."""
from pathlib import Path
import hashlib,json,zipfile

root=Path(__file__).resolve().parents[1]
target=root/'artifacts/shell-hardening-deliverables.zip'
if target.exists():
    raise FileExistsError(target)
manifest=json.loads((root/'docs/verification/shell-context/hardening/final-manifest.json').read_text())
files=set()
for entry in manifest:
    path=root/entry['Path']
    assert hashlib.sha256(path.read_bytes()).hexdigest().upper()==entry['SHA256'],path
    files.add(path)
for name in ['SHELL_CONTEXT_IMPLEMENTATION_REPORT.md','SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md','README.md','IMPLEMENTATION_STATUS.md','DECISIONS.md','PaneCommander.sln','Directory.Build.props']:
    path=root/name
    if path.exists():files.add(path)
for directory in ['docs/verification/shell-context','docs/spec','artifacts/shell-hardening-before','artifacts/shell-context-performance-sta','artifacts/shell-context-type-isolation','artifacts/shell-hardening-after-mixed','artifacts/shell-hardening-after-types','artifacts/shell-hardening-baseline','artifacts/shell-hardening-idle','artifacts/shell-hardening-gdi-diagnosis','artifacts/shell-lifetime-pin-control','artifacts/shell-native-module-cycle','artifacts/shell-desktop-environment']:
    files.update(p for p in (root/directory).rglob('*') if p.is_file())
entries=[]
with zipfile.ZipFile(target,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
    for path in sorted(files):
        relative=path.relative_to(root).as_posix()
        assert path.name.lower() not in ['nvui.dll','filesyncshell64.dll']
        data=path.read_bytes()
        archive.writestr(relative,data)
        entries.append(dict(Path=relative,SHA256=hashlib.sha256(data).hexdigest()))
    archive.writestr('PACKAGE-MANIFEST.json',json.dumps(entries,indent=2))
with zipfile.ZipFile(target) as archive:
    assert archive.testzip() is None
    for entry in entries:
        assert hashlib.sha256(archive.read(entry['Path'])).hexdigest()==entry['SHA256']
result=dict(Path=str(target),Files=len(entries),Bytes=target.stat().st_size,SHA256=hashlib.sha256(target.read_bytes()).hexdigest())
(root/'docs/verification/shell-context/hardening/package.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
