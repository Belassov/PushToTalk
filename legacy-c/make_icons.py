#!/usr/bin/env python3
"""Генератор иконок микрофона для PushToTalk (без внешних зависимостей)."""
import struct, math

SS = 8

def rrect(x, y, x0, y0, x1, y1, r):
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

def mic_coverage(x, y, slash):
    body = rrect(x, y, 12.4, 6.4, 19.6, 19.6, 3.6)
    if not body:
        d = math.hypot(x - 16.0, y - 17.0)
        if y >= 17.0 and 6.2 <= d <= 7.9:
            body = True
    if not body:
        if 15.0 <= x <= 17.0 and 24.0 <= y <= 27.4:
            body = True
    if not body:
        if rrect(x, y, 10.4, 27.0, 21.6, 29.2, 1.0):
            body = True
    if slash:
        dd = abs((x - 16.0) + (y - 16.0))
        if dd < 1.7:
            return 0
        if dd < 1.05:
            return 2
    return 1 if body else 0

def render(size, rgb, slash):
    alpha = [[0] * size for _ in range(size)]
    for py in range(size):
        row = alpha[py]
        for px in range(size):
            hits = 0
            white = 0
            for sy in range(SS):
                for sx in range(SS):
                    x = (px + (sx + 0.5) / SS) * 32.0 / size
                    y = (py + (sy + 0.5) / SS) * 32.0 / size
                    v = mic_coverage(x, y, slash)
                    if v:
                        hits += 1
                        if v == 2:
                            white += 1
            cov = hits / (SS * SS)
            if cov <= 0:
                row[px] = (0, 0, 0, 0)
            else:
                wc = white / (SS * SS)
                r = int(rgb[0] * (1 - wc / cov) + 255 * (wc / cov))
                g = int(rgb[1] * (1 - wc / cov) + 255 * (wc / cov))
                b = int(rgb[2] * (1 - wc / cov) + 255 * (wc / cov))
                row[px] = (r, g, b, int(round(cov * 255)))
    return alpha

def bmp_image(size, px):
    hdr = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            r, g, b, a = px[y][x]
            xor += bytes((b, g, r, a))
    mask_row = ((size + 31) // 32) * 4
    return hdr + bytes(xor) + b'\x00' * (mask_row * size)

def write_ico(path, rgb, slash):
    sizes = [16, 32, 48]
    imgs = [bmp_image(s, render(s, rgb, slash)) for s in sizes]
    out = bytearray(struct.pack('<HHH', 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for s, data in zip(sizes, imgs):
        out += struct.pack('<BBBBHHII', s if s < 256 else 0, s if s < 256 else 0, 0, 0, 1, 32,
                           len(data), offset)
        offset += len(data)
    for data in imgs:
        out += data
    with open(path, 'wb') as f:
        f.write(out)
    print('wrote', path, len(out), 'bytes')

write_ico('mic_live.ico',  (0x2e, 0xcc, 0x71), False)
write_ico('mic_muted.ico', (0xe7, 0x4c, 0x3c), True)
write_ico('mic_off.ico',   (0x95, 0xa5, 0xa6), False)
