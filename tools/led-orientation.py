#!/usr/bin/env python3
"""Settle the physical LED order of a WLED perimeter without guessing.

The plugin assumes one clockwise run that starts at the top-left corner of the
picture: top left to right, right top to bottom, bottom right to left, left
bottom to top. Nothing in the configuration can express a strip wired any other
way, so when one side reads as mirrored the only honest next step is to look at
what the hardware actually does with a known index.

Both modes drive WLED directly over DDP on port 4048, exactly as the plugin
does, so what you see is the same mapping the plugin would produce. The plugin
itself must not be streaming at the same time; disable it first, or stop
playback.

Usage:
    python3 tools/led-orientation.py sides [host]
    python3 tools/led-orientation.py chase [host]

"sides" paints each of the four sides the plugin believes in with its own
colour, and drives the first 15 LEDs of each side white. Each white marker
shows where that side begins. On a correctly wired strip the white markers sit
at: top-left, top-right, bottom-right, bottom-left, in that order.

"chase" walks a single white dot from index 0 to the last LED. Watch where it
starts and which way it travels.
"""

import socket
import sys
import time

TOP, RIGHT, BOTTOM, LEFT = 265, 150, 266, 150
LED_COUNT = TOP + RIGHT + BOTTOM + LEFT
DDP_PORT = 4048
CHANNELS_PER_PACKET = 1440
MARKER_LENGTH = 15

SIDE_COLOURS = [
    ("top", TOP, (120, 0, 0)),
    ("right", RIGHT, (0, 120, 0)),
    ("bottom", BOTTOM, (0, 0, 160)),
    ("left", LEFT, (120, 90, 0)),
]


def send_frame(sock, host, rgb, sequence):
    """One DDP frame, PUSH on the final packet, matching DdpPacketizer."""
    offset = 0
    packets = (len(rgb) + CHANNELS_PER_PACKET - 1) // CHANNELS_PER_PACKET
    for index in range(packets):
        payload = rgb[offset:offset + CHANNELS_PER_PACKET]
        header = bytearray(10)
        header[0] = 0x41 if index == packets - 1 else 0x40
        header[1] = sequence
        header[2] = 0x0B
        header[3] = 0x01
        header[4:8] = offset.to_bytes(4, "big")
        header[8:10] = len(payload).to_bytes(2, "big")
        sock.sendto(bytes(header) + payload, (host, DDP_PORT))
        offset += len(payload)
        sequence = sequence % 15 + 1
    return sequence


def paint_sides():
    frame = bytearray(LED_COUNT * 3)
    index = 0
    for _name, count, colour in SIDE_COLOURS:
        for position in range(count):
            pixel = (255, 255, 255) if position < MARKER_LENGTH else colour
            frame[(index + position) * 3:(index + position) * 3 + 3] = bytes(pixel)
        index += count
    return frame


def run_sides(sock, host, seconds=90):
    frame = paint_sides()
    print("Each side has its own colour; the white run of 15 marks where that")
    print("side starts. Expected on a strip wired the way the plugin assumes:")
    print("  red bottom-right of top row  -> white marker at the TOP-LEFT")
    print("  green right side             -> white marker at the TOP-RIGHT")
    print("  blue bottom                  -> white marker at the BOTTOM-RIGHT")
    print("  amber left side              -> white marker at the BOTTOM-LEFT")
    print()
    print(f"Holding the pattern for {seconds}s. Ctrl+C to stop early.")
    sequence = 1
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        sequence = send_frame(sock, host, bytes(frame), sequence)
        time.sleep(0.2)


def run_chase(sock, host, dwell=0.012):
    print(f"Walking one white dot across all {LED_COUNT} LEDs, index 0 first.")
    print("Note where it starts and which way it goes. Ctrl+C to stop.")
    sequence = 1
    while True:
        for led in range(LED_COUNT):
            frame = bytearray(LED_COUNT * 3)
            for tail in range(6):
                position = led - tail
                if position < 0:
                    continue
                level = 255 >> tail
                frame[position * 3:position * 3 + 3] = bytes((level, level, level))
            sequence = send_frame(sock, host, bytes(frame), sequence)
            time.sleep(dwell)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "sides"
    host = sys.argv[2] if len(sys.argv) > 2 else "10.0.0.8"
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        if mode == "sides":
            run_sides(sock, host)
        elif mode == "chase":
            run_chase(sock, host)
        else:
            print(__doc__)
            return 2
    except KeyboardInterrupt:
        print("\nStopped. WLED returns to its own effect once realtime input times out.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
