"""Freeze only numeric lifecycle metadata locally, then compare Python and host."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

from live_console import Monitor

out = Path(sys.argv[1]).resolve()
home = out/('metadata-home-'+uuid.uuid4().hex)
original = Path(os.environ.get('CODEX_HOME', Path.home()/'.codex'))
paths = sorted(p for folder in ('sessions','archived_sessions') for p in (original/folder).rglob('*.jsonl'))
kept = 0
for i,path in enumerate(paths):
    folder = 'archived_sessions' if 'archived_sessions' in path.parts else 'sessions'
    dest = home/folder/f'{i:04d}.jsonl'; dest.parent.mkdir(parents=True,exist_ok=True)
    selected=[]
    for line in path.open(encoding='utf-8'):
        if not any(s in line for s in ('"session_meta"','"task_started"','"task_complete"',
                                      '"turn_aborted"','"item_completed"','"token_usage_record"')):continue
        try:row=json.loads(line)
        except ValueError:continue
        kind,q=row.get('type'),row.get('payload',{})
        if kind=='session_meta':q={k:q[k] for k in ('id','history_mode','timestamp') if k in q}
        elif kind=='event_msg' and q.get('type') in ('task_started','task_complete','turn_aborted','item_completed'):
            q={k:q[k] for k in ('type','thread_id','turn_id','started_at_ms','completed_at_ms','item') if k in q}
            if 'item' in q:q['item']={k:q['item'][k] for k in ('type','id') if k in q['item']}
        elif kind=='token_usage_record':q={k:q[k] for k in ('thread_id','turn_id','response_id','usage') if k in q}
        else:continue
        selected.append(json.dumps({'type':kind,'timestamp':row.get('timestamp'),'payload':q}))
    dest.write_text('\n'.join(selected)+'\n',encoding='utf-8');kept+=len(selected)
probe=out/'metadata-comparison';probe.mkdir(exist_ok=True)
subprocess.run([str(out/'HostTokenSpeedTests.exe'),str(out/'fixtures.json'),str(probe),
                '--live','--probe-home='+str(home)],check=True)
actual=json.loads((probe/'live-native.json').read_text())
py=Monitor();py.poll(home);expected=py.snapshot()
# No new model response is requested during the frozen replay. The lifecycle
# inputs are identical; only a sub-second wall-clock boundary can differ.
assert actual['sources']==expected['discoveredSessions']
assert actual['active']==expected['activeSessions'] and actual['measured']==expected['measuredSessions']
assert (actual['rate'] is None and expected['speed'] is None) or actual['rate']==expected['speed']
record={'passed':True,'files':len(paths),'numericMetadataRecords':kept,
        'sources':actual['sources'],'sameActiveAndMeasuredCounts':True,'sameAggregateRate':True,
        'messageBodiesRemoved':True,'personalContentOrIdentityExported':False}
(out/'metadata-parity.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
print(json.dumps(record))
