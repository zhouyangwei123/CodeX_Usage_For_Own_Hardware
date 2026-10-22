"""Read the already identified low-energy G474 via the explicit probe, without a RAM loader.

Arguments: programmer, start address, byte count, output stem. Only the actual
128 KiB dual-bank device ranges and option-byte mirror are allowed. Require a
complete, consecutive result; never synthesize missing bytes or claim partial
reads as backup. Each child has a 25-second deadline and only its own process is
terminated on timeout by subprocess.run. No write/reset/unlock commands exist.
"""
import hashlib, json, pathlib, re, struct, subprocess, sys, time

programmer, address, count, stem = sys.argv[1:]
address, count = int(address, 0), int(count, 0)
assert count > 0 and count % 4 == 0
assert any(lo <= address < address + count <= hi for lo, hi in [
    (0x08000000, 0x08010000), (0x08040000, 0x08050000), (0x1FFF7800, 0x1FFF7840)])
stem = pathlib.Path(stem).resolve()
root = pathlib.Path(__file__).resolve().parent
assert root in stem.parents
args = [programmer, '-c', 'port=SWD', 'sn=53FF77064986484859341587',
        'mode=HOTPLUG', 'freq=1000', '-r32fast', hex(address), str(count)]
started = time.monotonic()
result = subprocess.run(args, capture_output=True, timeout=25, creationflags=subprocess.CREATE_NO_WINDOW)
text = result.stdout.decode('utf-8', errors='replace') + result.stderr.decode('utf-8', errors='replace')
stem.with_suffix('.txt').write_text(text, encoding='utf-8')
if result.returncode or re.search(r'^\s*Error\b', text, re.M):
    raise RuntimeError('Direct read failed; preserved raw output')
image = bytearray()
for match in re.finditer(r'^0x([0-9A-Fa-f]+)\s*:\s*((?:[0-9A-Fa-f]{8}(?:\s+|$))+)', text, re.M):
    at = int(match[1], 16)
    if at != address + len(image):
        raise RuntimeError('Nonconsecutive direct read')
    for word in match[2].split():
        image.extend(struct.pack('<I', int(word, 16)))
if len(image) != count:
    raise RuntimeError(f'Incomplete direct read: {len(image)}/{count} bytes')
stem.with_suffix('.bin').write_bytes(image)
record = dict(address=hex(address), byte_count=count, sha256=hashlib.sha256(image).hexdigest(),
              elapsed_seconds=time.monotonic()-started, return_code=result.returncode, argv=args)
stem.with_suffix('.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in record.items() if k != 'argv'}))
