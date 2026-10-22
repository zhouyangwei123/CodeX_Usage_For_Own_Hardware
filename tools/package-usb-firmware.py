"""Seal the boot/application images and emit bounded Intel HEX (stdlib only)."""
import argparse, hashlib, json, pathlib, struct, zlib
BOOT = 0x08000000
APP = 0x08002000
LIMIT = 0x08010000
HEADER = 0x200
TARGET = 0x04690080
def seal(raw, base, capacity, magic):
    image = bytearray(raw)
    if len(image) < 0x400 or len(image) > capacity:
        raise ValueError("Image does not fit its partition")
    image.extend(b"\xff" * (-len(image) % 8))
    fields = list(struct.unpack_from("<8I", image, HEADER))
    if fields[0:3] != [magic, 1, base] or fields[6:] != [TARGET, 0]:
        raise ValueError("Unexpected compiler image header")
    stack, entry = struct.unpack_from("<II", image)
    if not (0x20000000 < stack <= 0x2001FFF8 and stack % 8 == 0 and
            entry & 1 and base + 0x400 <= entry < base + len(image)):
        raise ValueError("Invalid vectors")
    fields[3], fields[4] = len(image), 0
    struct.pack_into("<8I", image, HEADER, *fields)
    fields[4] = zlib.crc32(image)
    struct.pack_into("<8I", image, HEADER, *fields)
    return bytes(image)
def hex_image(segments):
    lines, upper = [], None
    def record(address, kind, data):
        b = bytes([len(data), address >> 8, address & 255, kind]) + data
        return ":" + (b + bytes([-sum(b) & 255])).hex().upper()
    for base, data in segments:
        for offset in range(0, len(data), 16):
            address = base + offset
            if address >> 16 != upper:
                upper = address >> 16
                lines.append(record(0, 4, upper.to_bytes(2, "big")))
            lines.append(record(address & 65535, 0, data[offset:offset+16]))
    return "\n".join(lines + [":00000001FF"]) + "\n"
def main():
    p = argparse.ArgumentParser()
    p.add_argument("--boot", required=True)
    p.add_argument("--app", required=True)
    p.add_argument("--out", required=True)
    a = p.parse_args()
    out = pathlib.Path(a.out); out.mkdir(parents=True, exist_ok=True)
    boot = seal(pathlib.Path(a.boot).read_bytes(), BOOT, APP-BOOT, 0x42544D4E)
    app = seal(pathlib.Path(a.app).read_bytes(), APP, LIMIT-APP, 0x41544D4E)
    outputs = {"Bootloader-1.0.bin": boot, "Application-0.9.bin": app,
        "Application-0.9.hex": hex_image([(APP, app)]).encode("ascii"),
        "FirstInstall-Boot1.0-FW0.9.hex": hex_image([(BOOT, boot), (APP, app)]).encode("ascii")}
    for name, data in outputs.items(): (out/name).write_bytes(data)
    manifest = {"target":"STM32G474RBTx", "layout":1, "boot_address":BOOT,
        "app_address":APP, "app_limit":LIMIT, "hardware_tested":False,
        "files":{name:{"bytes":len(data),"sha256":hashlib.sha256(data).hexdigest()}
                 for name,data in outputs.items()}}
    (out/"firmware-manifest.json").write_text(json.dumps(manifest, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({"boot_bytes":len(boot),"app_bytes":len(app),
                      "app_capacity":LIMIT-APP,"hardware_tested":False}))
if __name__ == "__main__": main()
