"""Create a local evidence archive; no fixtures, binaries, or analysis packages."""
from pathlib import Path
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[1]
evidence = root / 'docs/verification/render-comparison'
validation = json.loads((evidence / 'validation.json').read_text(encoding='utf-8-sig'))
assert validation['complete'] and validation['runs'] == 54 and validation['alternatingOrder']
paths = list(evidence.rglob('*'))
paths += [root / p for p in ['RENDERING_COMPARISON_REPORT.md', 'RENDERING_BENCHMARK_PROTOCOL.md',
    'tools/Compare-Rendering.ps1', 'tools/Start-RenderingComparison.ps1', 'tools/Verify-RenderingIntegrity.ps1',
    'tools/Analyze-Rendering.py', 'tools/Package-RenderingEvidence.py', 'tools/requirements-render-analysis.txt']]
for project in ['PaneCommander.Performance', 'PaneCommander.RenderMonitor']:
    paths += list((root / 'tools' / project).glob('*.cs'))
    paths += list((root / 'tools' / project).glob('*.csproj'))
paths = sorted({p for p in paths if p.is_file() and p.name != 'evidence-sha256.json'})
manifest = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
manifest_path = evidence / 'evidence-sha256.json'
manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding='utf-8')
paths.append(manifest_path)
archive = root / 'artifacts/render-comparison-deliverables.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for path in paths:
        z.write(path, path.relative_to(root).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist()) == len(paths)
print(f'{archive}: {len(paths)} files, {archive.stat().st_size / 1e6:.2f} MB; ZIP CRC verified.')
