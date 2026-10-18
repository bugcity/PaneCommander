from pathlib import Path
import hashlib,json,zipfile
root=Path(__file__).resolve().parents[1]
out=root/'docs/verification/shell-context/no-screen-off'
target=root/'artifacts/shell-no-screen-off-deliverables.zip'
if target.exists():raise FileExistsError(target)
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
files=set()
for entry in read(out/'final-manifest.json'):
    path=root/entry['Path']
    assert hashlib.sha256(path.read_bytes()).hexdigest()==entry['SHA256']
    files.add(path)
for directory in ['docs/verification/shell-context/no-screen-off',
                  'docs/verification/session-lock/20261014',
                  'artifacts/shell-gui-no-screen-off-environment',
                  'artifacts/shell-gui-no-screen-off-default-after-idle-verified',
                  'artifacts/shell-gui-no-screen-off-software-after-wait',
                  'artifacts/shell-gui-no-screen-off-final-environment']:
    files.update(p for p in(root/directory).rglob('*')if p.is_file())
for name in ['SCREEN_OFF_DISABLED_GUI_REPORT.md','SESSION_LOCK_DIAGNOSIS_REPORT.md',
             'SHELL_CONTEXT_GUI_RESUME_REPORT.md','SHELL_CONTEXT_IMPLEMENTATION_REPORT.md',
             'SHELL_CONTEXT_CAUSE_REPORT.md','SHELL_CONTEXT_PERFORMANCE_REPORT.md',
             'README.md','IMPLEMENTATION_STATUS.md','DECISIONS.md','PaneCommander.sln',
             'docs/verification/shell-context/GUI_CHECKLIST.md']:
    files.add(root/name)
for fixture in read(out/'status.json')['Fixtures'].values():
    # Metadata only: do not traverse fixture junctions or archive referenced data.
    files.update(p for p in(root/fixture).glob('*.json')if p.is_file())
    files.update(p for p in(root/fixture/'diagnostics').glob('*.json')if p.is_file())
entries=[]
with zipfile.ZipFile(target,'x',zipfile.ZIP_DEFLATED)as archive:
    for path in sorted(files):
        data=path.read_bytes();relative=path.relative_to(root).as_posix()
        assert path.name.lower()not in ['nvui.dll','filesyncshell64.dll']
        archive.writestr(relative,data)
        entries.append(dict(Path=relative,SHA256=hashlib.sha256(data).hexdigest()))
    archive.writestr('PACKAGE-MANIFEST.json',json.dumps(entries,indent=2))
with zipfile.ZipFile(target)as archive:
    assert archive.testzip()is None
    for entry in entries:assert hashlib.sha256(archive.read(entry['Path'])).hexdigest()==entry['SHA256']
result=dict(Files=len(entries),Bytes=target.stat().st_size,SHA256=hashlib.sha256(target.read_bytes()).hexdigest())
(out/'package.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
