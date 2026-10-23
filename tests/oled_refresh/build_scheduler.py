"""Extract the actual app scheduler for a native HAL/I2C boundary test."""
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text(encoding='utf-8')
functions = []
for name in ['oled_refresh_interval_ms', 'oled_refresh_due']:
    start = source.index('static uint', source.rindex('\n', 0, source.index(name + '(')))
    opening = source.index('{', start)
    end, depth = opening + 1, 1
    while depth:
        if source[end] == '{': depth += 1
        elif source[end] == '}': depth -= 1
        end += 1
    functions.append(source[start:end])
Path(sys.argv[2]).write_text('\n\n'.join(functions) + '\n', encoding='utf-8')
