"""展开/收起状态动画的逐帧验证。

用屏幕 BitBlt 直接抓真实合成输出（PrintWindow 拿的是合成器缓存帧，
抓不到 WPF 动画中间帧），逐帧量出「包含子文件夹」卡片的真实底边 y：
卡片背景 (22,24,29) 与卡片间隙 (14,15,18) 在 x=490 这一列交替出现，
把被 1px 分隔线切开的同卡片段合并后，第一段结束的位置就是卡片底边。
折叠 ≈135、展开 ≈238；中间的连续取值即证明高度在做状态动画。

用法：python anim_verify.py
前提：应用已启动（脚本会自己点开「目录设置」弹窗）。
"""

import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

user32.GetDC.restype = wintypes.HDC
user32.GetDC.argtypes = [wintypes.HWND]
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.BitBlt.restype = wintypes.BOOL
gdi32.BitBlt.argtypes = [
    wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD,
]
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
gdi32.DeleteDC.argtypes = [wintypes.HDC]
user32.mouse_event.argtypes = [
    wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


gdi32.GetDIBits.restype = ctypes.c_int
gdi32.GetDIBits.argtypes = [
    wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
    ctypes.c_void_p, ctypes.POINTER(BITMAPINFO), wintypes.UINT,
]

MAIN_TITLE = "正画 PivotPic"
DLG_TITLE = "目录设置"
SETTINGS_BTN = (776, 167)   # 主窗口客户区内，测试卡片右侧齿轮
TOGGLE = (575, 104)         # 弹窗客户区内，「包含子文件夹」开关
COL = 490                   # 采样列：卡片内空白处，避开通关/输入框/文案


def activate(hwnd):
    user32.ShowWindow(hwnd, 9)
    user32.keybd_event(0x12, 0, 0, 0)
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(0x12, 0, 2, 0)
    time.sleep(0.35)


class Grabber:
    """复用 DC/Bitmap，逐帧抓客户区像素（屏幕 BitBlt，非 PrintWindow）。"""

    def __init__(self, hwnd):
        self.hwnd = hwnd
        rect = wintypes.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(rect))
        self.w, self.h = rect.right, rect.bottom
        self.origin = wintypes.POINT(0, 0)
        user32.ClientToScreen(hwnd, ctypes.byref(self.origin))
        self.screen_dc = user32.GetDC(0)
        self.mem_dc = gdi32.CreateCompatibleDC(self.screen_dc)
        self.bitmap = gdi32.CreateCompatibleBitmap(self.screen_dc, self.w, self.h)
        gdi32.SelectObject(self.mem_dc, self.bitmap)
        self.bi = BITMAPINFO()
        self.bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        self.bi.bmiHeader.biWidth = self.w
        self.bi.bmiHeader.biHeight = -self.h
        self.bi.bmiHeader.biPlanes = 1
        self.bi.bmiHeader.biBitCount = 24
        self.bi.bmiHeader.biCompression = 0
        self.stride = ((self.w * 3 + 3) // 4) * 4
        self.buf = ctypes.create_string_buffer(self.stride * self.h)

    def grab(self):
        SRCCOPY = 0x00CC0020
        gdi32.BitBlt(self.mem_dc, 0, 0, self.w, self.h,
                     self.screen_dc, self.origin.x, self.origin.y, SRCCOPY)
        gdi32.GetDIBits(self.mem_dc, self.bitmap, 0, self.h, self.buf,
                        ctypes.byref(self.bi), 0)
        return self.buf.raw

    def close(self):
        gdi32.DeleteObject(self.bitmap)
        gdi32.DeleteDC(self.mem_dc)
        user32.ReleaseDC(0, self.screen_dc)


def metrics(cur, stride, height):
    """返回 (卡片1底边 y, 卡片2顶边 y, 卡片1视觉高度)。"""
    runs = []
    run_start = None
    for y in range(60, height - 70):
        i = y * stride + COL * 3
        b, g, r = cur[i], cur[i + 1], cur[i + 2]
        is_card = abs(r - 22) <= 5 and abs(g - 24) <= 5 and abs(b - 29) <= 5
        if is_card:
            if run_start is None:
                run_start = y
        else:
            if run_start is not None and y - run_start > 6:
                runs.append((run_start, y - 1))
            run_start = None
    if run_start is not None and (height - 70) - run_start > 6:
        runs.append((run_start, height - 71))

    if not runs or runs[0][0] > 130:
        return -1, -1, -1

    # 合并被 1px 分隔线切开的同一张卡片，遇到真正卡片间隙（>4px）则停止
    top = runs[0][0]
    bottom = runs[0][1]
    card2_top = -1
    for i in range(1, len(runs)):
        gap = runs[i][0] - runs[i - 1][1] - 1
        if gap <= 4:
            bottom = runs[i][1]
        else:
            card2_top = runs[i][0]
            break
    return bottom, card2_top, bottom - top


def click(x, y, hold=0.04):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)
    user32.mouse_event(0x0002, 0, 0, 0, None)
    time.sleep(hold)
    user32.mouse_event(0x0004, 0, 0, 0, None)


def screen_pt(hwnd, x, y):
    pt = wintypes.POINT(x, y)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    return pt.x, pt.y


def find_hwnd(title):
    h = user32.FindWindowW(None, title)
    if h:
        return h
    out = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def proc(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n:
            b = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, b, n + 1)
            if title in b.value:
                out.append(hwnd)
        return True

    user32.EnumWindows(proc, 0)
    return out[0] if out else 0


def run_phase(g, dlg, dur=0.85):
    """点一次开关，逐帧采样卡片几何，返回 [(t_ms, bottom, card2_top, h)]。"""
    activate(dlg)
    if dlg != user32.GetForegroundWindow():
        print("!! 弹窗未取得前台，抓帧可能被遮挡")
    time.sleep(0.35)

    frames = []
    b, c2, hh = metrics(g.grab(), g.stride, g.h)
    frames.append((0.0, b, c2, hh))

    x, y = screen_pt(dlg, *TOGGLE)
    t0 = time.perf_counter()
    click(x, y)

    while True:
        cur = g.grab()
        t = (time.perf_counter() - t0) * 1000
        b, c2, hh = metrics(cur, g.stride, g.h)
        frames.append((t, b, c2, hh))
        if t > dur * 1000:
            break
    return frames


def main():
    dlg = find_hwnd(DLG_TITLE)
    if not dlg:
        main_hwnd = find_hwnd(MAIN_TITLE)
        if not main_hwnd:
            print("未找到主窗口")
            return 2
        for _ in range(4):
            activate(main_hwnd)
            click(*screen_pt(main_hwnd, *SETTINGS_BTN))
            time.sleep(1.0)
            dlg = find_hwnd(DLG_TITLE)
            if dlg:
                break
        if not dlg:
            print("弹窗未能打开")
            return 2

    g = Grabber(dlg)
    print(f"弹窗客户区 {g.w}x{g.h}  原点 ({g.origin.x},{g.origin.y})")
    phase1 = phase2 = None
    try:
        phase1 = run_phase(g, dlg)
        time.sleep(0.7)
        phase2 = run_phase(g, dlg)
    finally:
        g.close()

    for name, frames in (("阶段1", phase1), ("阶段2", phase2)):
        if not frames:
            continue
        first = frames[0]
        last = frames[-1]
        direction = "展开" if last[1] > first[1] else "收起"
        print(f"=== {name}：{direction}  ({first[1]} -> {last[1]}) ===")
        prev = None
        for t, b, c2, hh in frames:
            delta = "" if prev is None else f" Δ{b - prev:+d}"
            print(f"  t={t:6.0f}ms  卡片1底边={b:4d}  卡片2顶边={c2:4d}  卡高={hh:4d}{delta}")
            prev = b
    return 0


if __name__ == "__main__":
    sys.exit(main())
