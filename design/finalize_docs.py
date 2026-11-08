"""Consolidate the observed mockup copy into UI_SPEC's single copy table."""
from pathlib import Path
import json, re

root = Path(__file__).parent
rows = {}
for item in json.loads((root/'qa/copy-observed.json').read_text(encoding='utf-8')):
    for text in item['text'].splitlines():
        text = text.strip()
        if not text or re.fullmatch(r'[\d.,%−— ×]+',text):
            continue
        rows.setdefault(text,set()).update(item['pages'])

extra = {
 'Shared': ['Dismiss message','SimVizion Studios · {product}','{product} · {page}', '✓'],
 'Dashboard · dynamic': ['Version {version}','Host {version} · channel {channel} · build {build}',
 'Status file written {timestamp}.','Saved status; this does not mean the game is running. Status file written {timestamp}.',
 'Services: {services}','Services: None reported','{count} samples this interval. Shares reflect sampled wall time, including waits.',
 '{process} · PID {pid}','Across {cores} logical cores','{count} samples','{tid} · main',
 'Current average {average} ms · slowest 1% boundary {p99} ms. Sample trend includes an occasional spike.',
 'Capture: {state} · {hint}. {permissions}.','Snapshot {time} · Mode {mode} · Attached {attached} · Verdict {label}: {reason} · {note}',
 'Full Windows permissions','Standard Windows permissions',
 'The last readings below are from {time}. Waiting to reconnect to the PC running {product}.',
 'This is an estimate from current loads.', 'Frame times in milliseconds, synthetic five-minute sample',
 'Processor and graphics load in percent, synthetic five-minute sample','−5 min','Now'],
 'Settings · action results': ['Preview: Windows would ask for permission here. No system changes were made.',
 'Preview address: http://192.168.1.42:8765/ · home network only',
 'Preview: Windows would ask once to allow access on your private home network.',
 'Preview: Windows would ask once to update how {product} starts.',
 'Preview changes applied to this page. Nothing was saved to your PC.','On','Off'],
 'Welcome · action results': ['Preview: permission granted. Sign out of Windows and back in to finish.','Sign out needed'],
 'Log · dynamic': ['Live · paused (+{count})','Resume','Resume ({count})','{count} lines · {shown} shown','WARN','ERROR','PASS','FAIL',
 'Game restarted · {time}','Some lines were missed while this page was disconnected.',
 'Reconnected. Showing the newest available lines.'],
 'Tray · action results': ['Preview only. Exit would stop monitoring, stop frame capture and remove the tray icon.',
 'App icon, 16 pixels','App icon, 32 pixels','Waiting, 16 pixels','Waiting, 32 pixels',
 'Watching, 16 pixels','Watching, 32 pixels','Waiting · light Windows tray','Watching · light Windows tray',
 'Waiting · light Windows tray, 16 pixels','Waiting · light Windows tray, 32 pixels',
 'Watching · light Windows tray, 16 pixels','Watching · light Windows tray, 32 pixels'],
}
for area, strings in extra.items():
    for text in strings: rows.setdefault(text,set()).add(area)

spec=(root/'UI_SPEC.md').read_text(encoding='utf-8')
spec=spec.split('\n<!-- COPY_TABLE -->')[0]
spec=spec.replace('The adjacent [COPY_DECK.md](COPY_DECK.md) is the single table of static and state-dependent user-facing strings, including mockup-only controls.', 'The single table at the end of this specification contains static and state-dependent user-facing strings, including mockup-only controls and observed sample data.')
spec+='\n<!-- COPY_TABLE -->\n\n## Complete mockup copy table\n\nObserved strings include source-data examples so reviewers can audit every state. Replace numeric, timestamp and plugin values with live data. Braced rows define dynamic templates; preview-only strings must not ship.\n\n| Area | User-facing string |\n| --- | --- |\n'
for text, pages in sorted(rows.items(),key=lambda i:(sorted(i[1])[0],i[0].lower())):
    spec+='| '+', '.join(sorted(pages))+' | '+text.replace('|','\\|').replace('<','&lt;').replace('>','&gt;')+' |\n'
(root/'UI_SPEC.md').write_text(spec,encoding='utf-8')
print(f'Consolidated {len(rows)} copy strings in UI_SPEC.md.')
