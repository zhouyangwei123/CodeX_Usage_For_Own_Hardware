"""Compile the real driver's pure drawing functions without the HAL/I2C layer."""
import argparse
import pathlib
import re

p = argparse.ArgumentParser()
p.add_argument('driver')
p.add_argument('output')
a = p.parse_args()
source = pathlib.Path(a.driver).read_text(encoding='utf-8')

def function(name):
    start = source.index('\nvoid ' + name + '(') + 1
    opening = source.index('{', start)
    end, depth = opening + 1, 1
    while depth:
        if source[end] == '{': depth += 1
        elif source[end] == '}': depth -= 1
        end += 1
    return source[start:end]

font = 'static const uint8_t font5x7[96][5] = {' + source.split('font5x7[96][5] = {', 1)[1].split('\n};', 1)[0] + '\n};\n'
parts = ['#include "oled.h"\n#include <string.h>\n#define OLED_WIDTH 128\n#define OLED_HEIGHT 64\nuint8_t fb[1024];\nunsigned pixel_overflow;\n', font]
parts.append(function('oled_draw_pixel').replace('void oled_draw_pixel(', 'static void driver_pixel('))
parts.append('void oled_draw_pixel(uint8_t x,uint8_t y,uint8_t color){if(x>=128||y>=64)pixel_overflow++;driver_pixel(x,y,color);}')
for name in ['oled_clear', 'oled_fill_rect', 'oled_draw_hline', 'oled_draw_vline', 'oled_draw_rect', 'oled_draw_line', 'oled_draw_char', 'oled_draw_string']:
    parts.append(function(name))
pathlib.Path(a.output).write_text('\n'.join(parts) + '\n', encoding='utf-8')
