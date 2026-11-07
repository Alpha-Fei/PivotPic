"""无依赖屏幕截图：用 GDI 抓取指定标题的窗口并保存为 BMP。

用于界面视觉验收，不需要 Pillow / pywin32。
"""

import ctypes
import struct
import sys
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

# 让 ctypes 正确处理 64 位句柄
user32.GetWindowDC.restype = wintypes.HDC
user32.GetWindowDC.argtypes = [wintypes.HWND]
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", ctypes.c_long),
        ("biHeight", ctypes.c_long),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


# 必须显式声明：否则 64 位 DC 句柄会被当成 int32 传入，触发 OverflowError
gdi32.BitBlt.restype = wintypes.BOOL
gdi32.BitBlt.argtypes = [
    wintypes.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HDC, ctypes.c_int, ctypes.c_int, wintypes.DWORD,
]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.GetDIBits.argtypes = [
    wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
    ctypes.c_void_p, ctypes.POINTER(BITMAPINFO), wintypes.UINT,
]
user32.PrintWindow.restype = wintypes.BOOL
user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
gdi32.DeleteDC.argtypes = [wintypes.HDC]


def capture(hwnd: int, out_path: str, client_only: bool = True) -> tuple:
    rect = wintypes.RECT()
    if client_only:
        user32.GetClientRect(hwnd, ctypes.byref(rect))
        origin = wintypes.POINT(0, 0)
        user32.ClientToScreen(hwnd, ctypes.byref(origin))
        width, height = rect.right, rect.bottom
        src_x, src_y = origin.x, origin.y
    else:
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        width = rect.right - rect.left
        height = rect.bottom - rect.top
        src_x, src_y = rect.left, rect.top

    if width <= 0 or height <= 0:
        raise RuntimeError(f"窗口尺寸无效: {width}x{height}")

    screen_dc = user32.GetDC(0)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    gdi32.SelectObject(mem_dc, bitmap)

    # 优先 PrintWindow：让目标窗口自己渲染到内存 DC，不受被其他窗口遮挡影响。
    # BitBlt 只作为兜底（它拿的是屏幕像素，窗口被挡住时会截到别的程序）。
    SRCCOPY = 0x00CC0020
    PW_RENDERFULLCONTENT = 0x00000002
    ok = user32.PrintWindow(hwnd, mem_dc, PW_RENDERFULLCONTENT)
    if not ok:
        ok = gdi32.BitBlt(mem_dc, 0, 0, width, height, screen_dc, src_x, src_y, SRCCOPY)
        if not ok:
            raise RuntimeError("BitBlt 与 PrintWindow 均失败")

    stride = ((width * 3 + 3) // 4) * 4
    buf_size = stride * height
    buf = ctypes.create_string_buffer(buf_size)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = width
    bi.bmiHeader.biHeight = -height  # 负数表示自上而下
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 24
    bi.bmiHeader.biCompression = 0

    gdi32.GetDIBits(mem_dc, bitmap, 0, height, buf, ctypes.byref(bi), 0)

    file_header = b"BM" + struct.pack("<IHHI", 14 + 40 + buf_size, 0, 0, 14 + 40)
    info_header = struct.pack(
        "<IiiHHIIiiII", 40, width, -height, 1, 24, 0, buf_size, 2835, 2835, 0, 0
    )
    with open(out_path, "wb") as f:
        f.write(file_header)
        f.write(info_header)
        f.write(buf.raw)

    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(0, screen_dc)
    return width, height


if __name__ == "__main__":
    title = sys.argv[1] if len(sys.argv) > 1 else "枢图 PivotPic"
    import os
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.environ["TEMP"], "shot.bmp")
    full = "--full" in sys.argv

    hwnd = user32.FindWindowW(None, title)
    if not hwnd:
        # 退一步：遍历顶层窗口按标题模糊匹配
        found = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def enum_proc(h, _):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if title in b.value:
                    found.append((h, b.value))
            return True

        user32.EnumWindows(enum_proc, 0)
        if not found:
            print(f"未找到标题含「{title}」的窗口")
            sys.exit(2)
        hwnd = found[0][0]

    w, h = capture(hwnd, out, client_only=not full)
    print(f"已截图 {out}  {w}x{h}")


def capture_screen(out_path: str) -> tuple:
    """抓取整个主屏幕（用于验证无主窗口时的浮窗）。"""
    width = user32.GetSystemMetrics(0)
    height = user32.GetSystemMetrics(1)
    screen_dc = user32.GetDC(0)
    mem_dc = gdi32.CreateCompatibleDC(screen_dc)
    bitmap = gdi32.CreateCompatibleBitmap(screen_dc, width, height)
    gdi32.SelectObject(mem_dc, bitmap)

    SRCCOPY = 0x00CC0020
    if not gdi32.BitBlt(mem_dc, 0, 0, width, height, screen_dc, 0, 0, SRCCOPY):
        raise RuntimeError("BitBlt 失败")

    stride = ((width * 3 + 3) // 4) * 4
    buf_size = stride * height
    buf = ctypes.create_string_buffer(buf_size)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = width
    bi.bmiHeader.biHeight = -height
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 24
    bi.bmiHeader.biCompression = 0

    gdi32.GetDIBits(mem_dc, bitmap, 0, height, buf, ctypes.byref(bi), 0)

    file_header = b"BM" + struct.pack("<IHHI", 14 + 40 + buf_size, 0, 0, 14 + 40)
    info_header = struct.pack("<IiiHHIIiiII", 40, width, -height, 1, 24, 0, buf_size, 2835, 2835, 0, 0)
    with open(out_path, "wb") as f:
        f.write(file_header)
        f.write(info_header)
        f.write(buf.raw)

    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(mem_dc)
    user32.ReleaseDC(0, screen_dc)
    return width, height
