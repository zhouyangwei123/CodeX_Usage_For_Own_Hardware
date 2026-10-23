"""Independently check delivered ZIP, hashes, sealed images and Intel HEX."""
import argparse
import hashlib
import json
import struct
import zipfile
import zlib


def image(data, base, capacity, magic):
    header = struct.unpack_from('<8I', data, 512)
    assert header[:3] == (magic, 1, base)
    assert header[3] == len(data) <= capacity and len(data) % 8 == 0
    assert header[6:] == (0x04690080, 0)
    work = bytearray(data)
    work[528:532] = b'\0' * 4
    assert zlib.crc32(work) == header[4]
    stack, entry = struct.unpack_from('<2I', data)
    assert 0x20000000 < stack <= 0x2001FFF8 and stack % 8 == 0
    assert entry & 1 and base + 1024 <= entry < base + len(data)


def read_hex(data):
    result, upper, ended = {}, 0, False
    for line in data.decode('ascii').splitlines():
        assert not ended and line.startswith(':')
        record = bytes.fromhex(line[1:])
        assert len(record) == record[0] + 5 and sum(record) % 256 == 0
        address = int.from_bytes(record[1:3], 'big')
        kind, payload = record[3], record[4:-1]
        if kind == 4:
            assert len(payload) == 2 and address == 0
            upper = int.from_bytes(payload, 'big') << 16
        elif kind == 0:
            for offset, value in enumerate(payload):
                location = upper + address + offset
                assert 0x08000000 <= location < 0x08010000
                assert location not in result
                result[location] = value
        elif kind == 1:
            assert not payload and address == 0
            ended = True
        else:
            raise AssertionError('Unexpected HEX record')
    assert ended
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('package')
    args = parser.parse_args()
    with zipfile.ZipFile(args.package) as archive:
        manifest = json.loads(archive.read('firmware-manifest.json'))
    version = manifest.get('app_version', '0.9')
    assert len(version.split('.')) == 2 and all(part.isdigit() for part in version.split('.'))
    app_bin = f'Application-{version}.bin'
    app_hex = f'Application-{version}.hex'
    first_hex = f'FirstInstall-Boot1.0-FW{version}.hex'
    expected = {'CodexToolsHost.exe', 'Bootloader-1.0.bin', app_bin,
                app_hex, first_hex,
                'firmware-manifest.json', 'Install-STLink.ps1', 'README.md',
                'VALIDATION.md', 'THIRD-PARTY-NOTICES.md',
                'OFFLINE-TEST-RESULTS.txt', 'SHA256SUMS.txt'}
    with zipfile.ZipFile(args.package) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist()) == expected and len(archive.namelist()) == len(expected)
        files = {name: archive.read(name) for name in expected}
    hashes = {}
    for line in files['SHA256SUMS.txt'].decode('ascii').splitlines():
        digest, name = line.split('  ', 1)
        assert name not in hashes
        hashes[name] = digest
    assert set(hashes) == expected - {'SHA256SUMS.txt'}
    for name, digest in hashes.items():
        assert hashlib.sha256(files[name]).hexdigest() == digest, name
    manifest = json.loads(files['firmware-manifest.json'])
    assert isinstance(manifest['hardware_tested'], bool)
    if manifest['hardware_tested']:
        assert manifest['verification']['host_sha256'] == hashlib.sha256(files['CodexToolsHost.exe']).hexdigest()
        assert manifest['verification']['app_sha256'] == hashlib.sha256(files[app_bin]).hexdigest()
    for name, detail in manifest['files'].items():
        assert len(files[name]) == detail['bytes']
        assert hashlib.sha256(files[name]).hexdigest() == detail['sha256']
    boot, app = files['Bootloader-1.0.bin'], files[app_bin]
    assert struct.unpack_from('<I', app, 532)[0] == (int(version.split('.')[0]) << 16 | int(version.split('.')[1]))
    image(boot, 0x08000000, 8192, 0x42544D4E)
    image(app, 0x08002000, 57344, 0x41544D4E)
    app_bytes = dict(enumerate(app, 0x08002000))
    combined = dict(enumerate(boot, 0x08000000))
    combined.update(app_bytes)
    assert read_hex(files[app_hex]) == app_bytes
    assert read_hex(files[first_hex]) == combined
    assert files['Install-STLink.ps1'].startswith(b'\xef\xbb\xbf')
    assert files['CodexToolsHost.exe'].startswith(b'MZ')
    print('PASS ZIP contents, all SHA-256 hashes, firmware CRC/vectors/bounds, HEX-to-BIN equality, PowerShell UTF-8 BOM')


if __name__ == '__main__':
    main()
