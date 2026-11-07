"""界面探针：定位主窗口，自动点击左侧导航，逐页截图。

用于视觉验收，不依赖任何第三方库。
"""

import ctypes
import os
import sys
import time
from ctypes import wintypes

from screenshot import capture, user32

NAV_X = 36          # 导航栏中心（相对客户区）
NAV_TOP = 48 + 8    # 标题栏 48 + 导航列表上边距 8
NAV_ITEM_H = 56


def find_window(exact_title: str):
    hwnd = user32.FindWindowW(None, exact_title)
    if hwnd:
        return hwnd

    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_proc(h, _):
        n = user32.GetWindowTextLengthW(h)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(h, buf, n + 1)
            if exact_title in buf.value:
                found.append(h)
        return True

    user32.EnumWindows(enum_proc, 0)
    return found[0] if found else 0


def client_origin(hwnd):
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    return pt.x, pt.y


def click(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.12)
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.05)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
    time.sleep(0.7)


def main():
    title = "枢图 PivotPic"
    hwnd = find_window(title)
    if not hwnd:
        print("未找到主窗口")
        return 2

    user32.ShowWindow(hwnd, 9)      # SW_RESTORE
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.6)

    ox, oy = client_origin(hwnd)
    pages = [("monitor", 0), ("settings", 1), ("log", 2), ("about", 3)]

    for name, index in pages:
        cx = ox + NAV_X
        cy = oy + NAV_TOP + index * NAV_ITEM_H + NAV_ITEM_H // 2
        user32.SetForegroundWindow(hwnd)
        click(cx, cy)
        time.sleep(0.5)
        out = os.path.join(os.environ["TEMP"], f"page_{name}.bmp")
        w, h = capture(hwnd, out, client_only=True)
        print(f"{name:10s} -> {out}  {w}x{h}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
