"""Report matched ETW heap requests separately from process commitment."""
import csv,json,sys
from pathlib import Path
root=Path(__file__).resolve().parents[1]
folder=root/'docs/verification/shell-lifetime-20261016'/(sys.argv[1] if len(sys.argv)>1 else 'native-heap')
name=sys.argv[2] if len(sys.argv)>2 else 'heap-matched-v2.json'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
d=read(folder/name)
assert d['AllocatedBytes']==d['FreedBytes']+d['HeapDestroyedBytes']+d['LiveBytesAtEnd']
assert d['UnknownFreeCount']==0 and d['DuplicateAddressCount']==0 and d['EventsLost']==0
conditions=read(folder/'target-started.json');restore=read(folder/'restoration.json');completed=read(folder/'completed.json')
assert restore['Restored'] and completed['StartToStopSeconds']<=180 and conditions['Elevated'] is False
with (folder/'file-phases/samples.csv').open(encoding='utf-8-sig',newline='') as f: process=list(csv.DictReader(f))
lines=['# ネイティブHeap記録の照合', '',
       f"対象PID {d['TargetPid']}、通常権限の診断EXE、ウォームアップ20回・測定{conditions['Measured']}回。WPR開始〜stop処理完了{completed['StartToStopSeconds']:.3f}秒（上限180秒）。IFEO復元確認済み。製品・クリップボード・ユーザーファイルは操作していない。トレースは性能測定ではない。", '',
       '|指標|結果|','|---|---:|',
       f"|記録内累積要求量（再割当を含む）|{d['AllocatedBytes']/1e6:.6f} MB|",
       f"|対応したFree|{d['FreedBytes']/1e6:.6f} MB|",
       f"|HeapDestroyによる解放|{d['HeapDestroyedBytes']/1e6:.6f} MB|",
       f"|最終追跡要求領域|{d['LiveBytesAtEnd']/1e6:.6f} MB|",
       f"|未対応Free / 重複アドレス|{d['UnknownFreeCount']} / {d['DuplicateAddressCount']}|",
       f"|イベント欠落|{d['EventsLost']}|", '',
       'HeapHandle＋アドレスをキーにAlloc/Free/ReAlloc/Destroyを順次処理。ReAllocに対応するAlloc/Freeが先行通知されるケースは二重計上しない。要求量=Free+HeapDestroy+最終追跡量の一致を確認。最初の二重通知未対応解析heap-matched.jsonは判断に使わず、改訂版とともに保存している。', '',
       'この要求量はヒープに要求したバイト数であり、Private Bytes、ヒープコミット量、GPU確保量と同一ではない。HeapExpand/Contractの最後のCommittedSpace合計も観測対象ヒープに限られ、全プロセスのコミット量ではない。プロセス終了後までFreeイベントが出ない領域があり、最終追跡量をそのままリーク量とはしない。', '',
       '## 割当・解放の例', '', '|割当スタックID|累積要求 MB|Free MB|HeapDestroy MB|最終追跡 MB|', '|---|---:|---:|---:|---:|']
for s in sorted(d['StackTotals'],key=lambda s:s['Allocated'],reverse=True)[:12]:
    lines.append(f"|{s['Stack']}|{s['Allocated']/1e6:.6f}|{s['Freed']/1e6:.6f}|{s['Destroyed']/1e6:.6f}|{s['OutstandingBytes']/1e6:.6f}|")
if 'FreeEventsWithStack' in d:
    lines += ['',f"Freeイベントのスタック付き件数：{d['FreeEventsWithStack']} / {d['FreeEvents']}。記録内マーカー{len(d.get('Markers',[]))}件。"]
lines += ['', 'スタックはJSONのStackExamples参照。DLLがスタックに現れることと、永続的な所有者・保持ルートの確定は別。COMの内部参照・ネイティブオブジェクトの参照グラフはこのHeapイベント記録から取得していない。短時間のアイドル安定、累積割当の大部分の解放を長期安定の合格にしない。', '',
          '## 今回の判断', '', '製品側の不要な保持は未確定。最小修正の根拠がないため製品コードは変更しない。要求領域・コミット・Private Bytesの推移、区間の再操作と解放経路を照合してから原因を判断する。']
(folder/'REPORT.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
sys.path.insert(0,str(root/'artifacts/render-analysis-deps'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
s=d['Samples'];x=[r['Ms']/1000 for r in s]
fig,axes=plt.subplots(2,1,figsize=(10,7),sharex=True)
axes[0].plot(x,[r['AllocatedBytes']/1e6 for r in s],label='Cumulative requested')
axes[0].plot(x,[(r['FreedBytes']+r['HeapDestroyedBytes'])/1e6 for r in s],label='Free + heap destruction')
axes[1].plot(x,[r['LiveBytes']/1e6 for r in s],label='Tracked outstanding requests')
if 'ClrJitStackOutstandingBytes' in s[0]:
    axes[1].plot(x,[r['ClrJitStackOutstandingBytes']/1e6 for r in s],label='Outstanding requests with clrjit in allocation stack')
axes[1].plot(x,[r['LastReportedHeapCommittedBytes']/1e6 for r in s],label='Last reported heap commit (partial coverage)')
for ax in axes:ax.set_ylabel('Decimal MB');ax.legend(fontsize=8);ax.grid(alpha=.2)
axes[1].set_xlabel('ETW seconds relative to recording start')
fig.suptitle('Diagnostic heap tracing; not process Private Bytes')
fig.tight_layout();fig.savefig(folder/'heap-timeseries.png',dpi=140);plt.close(fig)
print(folder/'REPORT.md')
