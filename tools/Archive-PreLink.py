"""Preserve the previous measured revision only when its recorded SHA256 matches.

The small reverse transformations recover source changed by the subsequent link policy.
They never edit working product files. A mismatch is reported, never accepted as a snapshot.
"""
from pathlib import Path
import hashlib,json,re,shutil
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/review-pre-link-policy'
manifest=json.loads((root/'artifacts/review-performance-final/after-product/source-manifest-during.json').read_text(encoding='utf-8-sig'))
report=[]
for e in manifest:
    relative=Path(e['Path']); p=root/relative
    data=p.read_bytes()
    if hashlib.sha256(data).hexdigest().upper()!=e['SHA256']:
        t=data.decode('utf-8-sig')
        if p.name=='Services.cs':
            t=t.replace('コピー・移動・削除はリンク本体です。相対ターゲットは文字列を維持します。','コピーはWindows Shellに従います。').replace('コピー・移動・削除はリンク本体です。','Windows標準コピーはリンク先内容を複製します。移動・削除はリンク本体です。')
        elif p.name=='FileOperationDialog.cs':
            t=t.replace('リンク自体を複製します。相対ターゲットはそのまま保持するため、送り先では参照先が変わる場合があります。','コピーはWindows標準動作です。ジャンクションはリンク先の内容を複製します。')
        elif p.name=='SettingsDialog.cs':
            t=t.replace('コピー・移動・削除はリンク本体を扱います。相対ターゲットは文字列を維持するため、送り先では参照先が変わる場合があります。リンクを含む項目のExplorerへのドラッグは安全のため無効です。','コピーはWindows Shellの標準動作です。ジャンクションはリンク先の内容を複製します。')
        elif p.name=='ShellFileOperationService.cs':
            t=t.replace('attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)','attributes.HasFlag(FileAttributes.Directory) && (request.Kind == FileOperationKind.Copy || !attributes.HasFlag(FileAttributes.ReparsePoint)))',1).replace('ReparsePoint))))','ReparsePoint)))')
            # The replacement above includes the closing condition parenthesis already in the source.
            t=t.replace('ReparsePoint))))','ReparsePoint)))')
            t=re.sub(r'^.*if \(request.Kind == FileOperationKind.Copy && request.NewName is null && string.Equals\(Path.GetFullPath\(source\).* continue;\n','',t,flags=re.M)
            a=t.index('                var containsLinks = false;'); b=t.index('                operation = ',a);t=t[:a]+t[b:]
            a=t.index('    public static bool RequiresInternalDrag');b=t.index('    [DllImport("shell32.dll"',a);t=t[:a]+t[b:]
        elif p.name=='FileDragBehavior.cs':
            t=t.replace('using PaneCommander.Infrastructure;\n','')
            t=re.sub(r'^    public const string InternalFilesFormat.*\n','',t,flags=re.M)
            t=re.sub(r'^    public static string\[\]\? ReadPaths.*\n','',t,flags=re.M)
            a=t.index('                var internalOnly = await Task.Run');b=t.index('                var returned = DragDrop.DoDragDrop',a);t=t[:a]+t[b:]
            anchor='            try\n            {\n                var returned = DragDrop.DoDragDrop'
            t=t.replace(anchor,'            var data = new DataObject(); var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files);\n'+anchor)
            t=t.replace('var sources = ReadPaths(e.Data)!;','var sources = (string[])e.Data.GetData(DataFormats.FileDrop);')
            t=t.replace('ReadPaths(e.Data) is not string[] paths','!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] paths')
        elif p.name=='ReviewTests.cs':
            t=t.replace('Assert.AreEqual(FileLinkKind.Junction, LinkMetadata.Kind(copyPath)); Assert.AreEqual(LinkObjectOperations.Read(link).Target, LinkObjectOperations.Read(copyPath).Target);','Assert.AreEqual("must survive",await File.ReadAllTextAsync(Path.Combine(copyPath,"keep.txt")),"ジャンクションのコピーは内容を複製");')
            t=t.replace('"シンボリックリンク作成失敗: " + ex.ToString()','"OSがシンボリックリンク作成を許可しません: " + ex.GetType().Name')
            t=t.replace('LinkObjectOperations.Delete(root, CancellationToken.None);','foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)) if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(dir); Directory.Delete(root, true);')
        elif p.name=='ReviewWpfChecks.cs':
            t=re.sub(r'^            (var internalFiles=|Assert.IsFalse\(internalFiles).*\n','',t,flags=re.M)
        normalized=t.replace('\r\n','\n')
        candidates=[t.encode('utf-8'),normalized.encode('utf-8'),normalized.replace('\n','\r\n').encode('utf-8')]
        candidates += [b'\xef\xbb\xbf'+x for x in candidates]
        data=next((x for x in candidates if hashlib.sha256(x).hexdigest().upper()==e['SHA256']),b'')
    ok=bool(data) and hashlib.sha256(data).hexdigest().upper()==e['SHA256']
    report.append({'Path':str(relative),'SHA256':e['SHA256'],'Verified':ok})
    if ok:
        target=out/'source'/relative;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
# Preserve any measured driver files still matching; common dependencies come from the archived product.
for e in json.loads((root/'artifacts/review-performance-final/after-repeat/binary-manifest.json').read_text(encoding='utf-8-sig')):
    original=Path(e['Path']); candidates=[out/'app'/original.name,original]
    candidate=next((p for p in candidates if p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest().upper()==e['SHA256']),None)
    if candidate:
        target=out/'driver'/original.name;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(candidate,target)
    report.append({'Path':str(original),'SHA256':e['SHA256'],'Verified':candidate is not None})
(out/'archive-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'Verified':sum(e['Verified'] for e in report),'Total':len(report),'Missing':[e['Path'] for e in report if not e['Verified']]},ensure_ascii=False))
