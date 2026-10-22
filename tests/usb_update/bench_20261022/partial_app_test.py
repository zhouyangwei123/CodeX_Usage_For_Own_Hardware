"""Bench-only incomplete-update test; never writes boot, options, or another device.

Uses the known protected desktop board after the user physically entered DFU.
Writes one 2-KiB application page from the delivered image over the rebuilt
image, verifies the exact resulting mismatch, then asks boot to run. The saved
full image and the production updater provide recovery in a separate step.
"""
import hashlib, json, pathlib, re, struct, subprocess, time, zlib

root = pathlib.Path(__file__).resolve().parent
out = root / 'hardware' / 'partial-application'
out.mkdir(exist_ok=False)
cli = r'D:\Software\Code\ARM\STM32CubeProg\bin\STM32_Programmer_CLI.exe'
serial = '20553872524E'
connection = ['-c', 'port=USB1', 'sn=' + serial]
step = 0

def run(args, timeout=30):
    """Run one bounded CLI step and retain exact arguments/output; abort on error."""
    global step
    step += 1
    (out / f'{step:02}-argv.json').write_text(json.dumps(args), encoding='utf-8')
    p = subprocess.run([cli] + args, capture_output=True, timeout=timeout,
                       creationflags=subprocess.CREATE_NO_WINDOW)
    text = (p.stdout + p.stderr).decode('utf-8', errors='replace')
    (out / f'{step:02}-output.txt').write_text(text, encoding='utf-8')
    if p.returncode or re.search(r'^\s*Error\b', text, re.M):
        raise RuntimeError('Programmer step failed; inspect preserved output')
    return text

def crc_of(image):
    """Return stored and computed CRC for the length declared by the saved header."""
    length, expected = struct.unpack_from('<II', image, 0x20c)
    assert 1024 <= length <= len(image)
    data = bytearray(image[:length]); data[0x210:0x214] = b'\0' * 4
    return expected, zlib.crc32(data)

found = re.findall(r'Serial number\s*:\s*([A-Za-z0-9]+)', run(['-l', 'usb']))
assert found == [serial], 'Unexpected DFU identity; no write'
for address, count, name in [(0x08000000, 8192, 'boot-before'),
                             (0x1fff7800, 44, 'options-before'),
                             (0x08002000, 57344, 'app-before')]:
    run(connection + ['-u', hex(address), str(count), str(out / (name + '.bin'))])
boot = (out / 'boot-before.bin').read_bytes()
options = (out / 'options-before.bin').read_bytes()
before = (out / 'app-before.bin').read_bytes()
assert boot == (root / 'device-after-install/bank1.bin').read_bytes()[:8192]
assert options == (root / 'device-after-install/options.bin').read_bytes()[:44]
wrp = struct.unpack_from('<I', options, 24)[0]
assert (wrp & 127) == 0 and ((wrp >> 16) & 127) == 3
rebuilt = (root.parent / 'usb-update/package/Application-0.9.bin').read_bytes()
delivered = (root / 'delivery/Application-0.9.bin').read_bytes()
assert before[:len(rebuilt)] == rebuilt and crc_of(before)[0] == crc_of(before)[1]
partial = delivered[:2048]
expected = partial + before[2048:]
assert crc_of(expected)[0] != crc_of(expected)[1], 'Test must leave invalid CRC'
(out / 'first-application-page.bin').write_bytes(partial)
run(connection + ['-w', str(out / 'first-application-page.bin'), '0x08002000', '-v'])
run(connection + ['-u', '0x08002000', '57344', str(out / 'app-partial.bin')])
actual = (out / 'app-partial.bin').read_bytes()
assert actual == expected, 'Unexpected partial write contents'
run(connection + ['-s', '0x08000000'])
time.sleep(2)
found = re.findall(r'Serial number\s*:\s*([A-Za-z0-9]+)', run(['-l', 'usb']))
assert found == [serial], 'Expected automatic recovery DFU'
run(connection + ['-u', '0x08000000', '8192', str(out / 'boot-after.bin')])
assert (out / 'boot-after.bin').read_bytes() == boot
result = dict(passed=True, usb_serial=serial, written_address='0x08002000',
              written_bytes=2048, application_bytes_match_expected=True,
              crc_stored=hex(crc_of(actual)[0]), crc_actual=hex(crc_of(actual)[1]),
              automatically_returned_to_dfu=True, boot_8192_unchanged=True,
              physical_cable_interruption=False, steps=step)
(out / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result))
