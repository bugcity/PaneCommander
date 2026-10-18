from pathlib import Path
import runpy,sys
runpy.run_path(str(Path(__file__).with_name('Finalize-ShellHardening.py')),run_name='__main__')
sys.exit(0)
import hashlib,json,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[1];out=root/'docs/verification/shell-context'
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest().upper()
tests={};ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
for mode in ['SoftwareOnly','Default']:
    document=ET.parse(out/'complete'/mode/'links-regression.trx');counter=document.find('.//t:Counters',ns).attrib
    assert int(counter['total'])==66 and int(counter['passed'])==63 and int(counter['failed'])==0,counter
    tests[mode]=counter
measured=root/'artifacts/shell-context-performance-sta/measured-source/ShellContextMenu.cs'
native=root/'src/PaneCommander.Infrastructure/ShellContextMenu.cs';assert sha(measured)==sha(native)
fixture=root/'artifacts/filter-gui/b4d8a9e4caae476d89acd1de7f2250a7/verification.json'
gui=json.loads(fixture.read_text(encoding='utf-8-sig'));assert gui['OwnedProcessStopped'] is True
gui.update(Feature='Windows Shell context menu',InputError='failed to activate captured window',Recovery='Refreshed unique returned window; activation failed again',Screenshot='Desktop wallpaper; not application/menu evidence',ClipboardError='0x800401D0 CLIPBRD_E_CANT_OPEN before changing clipboard',HumanAcceptance='Pending')
(out/'gui-attempt.json').write_text(json.dumps(gui,ensure_ascii=False,indent=2),encoding='utf-8')
paths=[]
for directory in ['src','tests','tools']:
    paths.extend(p for p in (root/directory).rglob('*') if p.is_file() and p.suffix in ['.cs','.xaml','.csproj','.ps1','.py'] and not any(part in ['bin','obj','TestResults'] for part in p.relative_to(root).parts))
paths.extend(p for p in (root/'src/PaneCommander.App/bin/Release/net10.0-windows').iterdir() if p.is_file() and p.suffix in ['.dll','.exe','.json'])
manifest=[{'Path':str(p.relative_to(root)),'SHA256':sha(p)}for p in paths]
(out/'final-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
summary=dict(ReleaseBuild='0 warnings, 0 errors; release-build.txt',Tests=tests,NativeBackendSourceIdenticalToMeasurement=True,FeatureImplementation='Implemented, native popup unverified',ShellCompatibility='Unverified',LinkSafety='Unsafe Shell link verbs blocked; shared junction engine tested; symlinks and actual GUI unverified',Performance='Driver unmet; actual popup unmeasured',OverallAcceptance='Pending')
(out/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'TotalEachMode':66,'PassedEachMode':63,'SkippedEachMode':3,'NativeSourceHash':sha(native),'Overall':'Pending'}))
