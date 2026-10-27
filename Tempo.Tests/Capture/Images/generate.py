#!/usr/bin/env python3
"""Generate the four section-11 live-check images (stdlib only: no PIL on this machine).

A small 5x7 bitmap font scaled up, written as plain RGB PNGs. Crisp high-contrast
text is all the vision check needs; the images are committed as test assets next
to CaptureLiveTests.
"""
import struct
import zlib

FONT = {
    'B': ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    'C': ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
    'D': ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    'H': ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    'J': ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
    'L': ["10000", "10000", "10000", "10000", "10000", "10001", "11111"],
    'M': ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    'O': ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    'P': ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    'S': ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    'T': ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    'U': ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    'a': ["00000", "00000", "01110", "00001", "01111", "10001", "01111"],
    'b': ["10000", "10000", "10110", "11001", "10001", "10001", "11110"],
    'c': ["00000", "00000", "01110", "10000", "10000", "10001", "01110"],
    'd': ["00001", "00001", "01101", "10011", "10001", "10001", "01111"],
    'e': ["00000", "00000", "01110", "10001", "11111", "10000", "01110"],
    'g': ["00000", "00000", "01111", "10001", "01111", "00001", "01110"],
    'h': ["10000", "10000", "10110", "11001", "10001", "10001", "10001"],
    'i': ["00100", "00000", "01100", "00100", "00100", "00100", "01110"],
    'k': ["10000", "10000", "10010", "10100", "11000", "10100", "10010"],
    'l': ["01100", "00100", "00100", "00100", "00100", "00100", "01110"],
    'm': ["00000", "00000", "11010", "10101", "10101", "10101", "10101"],
    'n': ["00000", "00000", "10110", "11001", "10001", "10001", "10001"],
    'o': ["00000", "00000", "01110", "10001", "10001", "10001", "01110"],
    'p': ["00000", "00000", "11110", "10001", "11110", "10000", "10000"],
    'r': ["00000", "00000", "10111", "11000", "10000", "10000", "10000"],
    's': ["00000", "00000", "01111", "10000", "01110", "00001", "11110"],
    't': ["00100", "00100", "01110", "00100", "00100", "10001", "01110"],
    'u': ["00000", "00000", "10001", "10001", "10001", "10011", "01101"],
    'v': ["00000", "00000", "10001", "10001", "10001", "01010", "00100"],
    'y': ["00000", "00000", "10001", "10001", "01111", "00001", "01110"],
    '0': ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
    '1': ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    '2': ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    '3': ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
    '9': ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    ' ': ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
    ',': ["00000", "00000", "00000", "00000", "00110", "00100", "01000"],
    ':': ["00000", "00110", "00110", "00000", "00110", "00110", "00000"],
    '-': ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
    '(': ["00010", "00100", "01000", "01000", "01000", "00100", "00010"],
    ')': ["01000", "00100", "00010", "00010", "00010", "00100", "01000"],
}


class Canvas:
    def __init__(self, w, h, bg):
        self.w, self.h = w, h
        self.px = bytearray(bg * (w * h))

    def rect(self, x0, y0, x1, y1, color):
        for y in range(max(0, y0), min(self.h, y1)):
            for x in range(max(0, x0), min(self.w, x1)):
                i = (y * self.w + x) * 3
                self.px[i:i + 3] = color

    def round_rect(self, x0, y0, x1, y1, r, color):
        self.rect(x0 + r, y0, x1 - r, y1, color)
        self.rect(x0, y0 + r, x1, y1 - r, color)
        for cx, cy in [(x0 + r, y0 + r), (x1 - r, y0 + r), (x0 + r, y1 - r), (x1 - r, y1 - r)]:
            for y in range(cy - r, cy + r):
                for x in range(cx - r, cx + r):
                    if (x - cx) ** 2 + (y - cy) ** 2 <= r * r and 0 <= x < self.w and 0 <= y < self.h:
                        i = (y * self.w + x) * 3
                        self.px[i:i + 3] = color

    def text(self, x, y, s, color, scale=6, bold=False):
        end = x + len(s) * 6 * scale
        assert end <= self.w, f"text {s!r} overflows canvas ({end} > {self.w})"
        for ci, ch in enumerate(s):
            glyph = FONT.get(ch)
            if glyph is None:
                raise ValueError(f"no glyph for {ch!r}")
            for row in range(7):
                for col in range(5):
                    if glyph[row][col] == '1':
                        self.rect(x + (ci * 6 + col) * scale, y + row * scale,
                                  x + (ci * 6 + col) * scale + scale + (1 if bold else 0),
                                  y + row * scale + scale, color)

    def save_png(self, path):
        raw = bytearray()
        for y in range(self.h):
            raw.append(0)
            raw.extend(self.px[y * self.w * 3:(y + 1) * self.w * 3])

        def chunk(typ, data):
            return (struct.pack(">I", len(data)) + typ + data +
                    struct.pack(">I", zlib.crc32(typ + data) & 0xffffffff))

        with open(path, "wb") as f:
            f.write(b"\x89PNG\r\n\x1a\n")
            f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", self.w, self.h, 8, 2, 0, 0, 0)))
            f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
            f.write(chunk(b"IEND", b""))


def whatsapp(path):
    c = Canvas(1000, 560, bytes([11, 20, 26]))
    c.round_rect(60, 90, 940, 470, 30, bytes([0, 92, 75]))
    c.text(110, 130, "Lisa", bytes([83, 189, 235]))
    c.text(110, 230, "Dinner Thursday 19:00", bytes([255, 255, 255]))
    c.text(110, 330, "at De Jonge Haan", bytes([255, 255, 255]))
    c.save_png(path)


def school_letter(path):
    c = Canvas(1160, 560, bytes([255, 255, 255]))
    black = bytes([20, 20, 20])
    c.text(80, 80, "Beste ouders,", black)
    c.text(80, 200, "Ouderavond: do 30 okt 19:30", black)
    c.text(80, 300, "Locatie: aula (begane grond)", black)
    c.save_png(path)


def calendar_event(path):
    c = Canvas(1000, 560, bytes([232, 234, 237]))
    c.round_rect(40, 80, 960, 480, 16, bytes([255, 255, 255]))
    c.rect(40, 80, 70, 480, bytes([26, 115, 232]))
    dark = bytes([32, 33, 36])
    c.text(200, 120, "Team meeting", dark, bold=True)
    c.text(200, 220, "Thursday, October 29", dark)
    c.text(200, 310, "19:00 - 20:00", dark)
    c.text(200, 390, "Utrecht Centraal", dark)
    c.save_png(path)


def shopping_list(path):
    c = Canvas(1000, 560, bytes([253, 251, 243]))
    dark = bytes([40, 40, 40])
    c.text(80, 80, "Shopping list", dark, bold=True)
    c.rect(80, 140, 80 + 13 * 6 * 6, 146, dark)
    c.text(80, 200, "- milk", dark)
    c.text(80, 300, "- eggs", dark)
    c.text(80, 400, "- bread", dark)
    c.save_png(path)


if __name__ == "__main__":
    import os
    out = os.path.dirname(os.path.abspath(__file__))
    whatsapp(os.path.join(out, "whatsapp.png"))
    school_letter(os.path.join(out, "school-letter.png"))
    calendar_event(os.path.join(out, "calendar-event.png"))
    shopping_list(os.path.join(out, "shopping-list.png"))
    print("wrote 4 images to", out)
