libvips 8.18.x — 第三方组件说明
================================

本目录下的 libvips-42.dll 来自 libvips 项目（https://www.libvips.org/），
通过 npm 包 @img/sharp-libvips-win32-x64 1.3.4 获取，为 x64 Windows 静态链接构建。

许可证：GNU Lesser General Public License v2.1 或更高版本（LGPL-2.1-or-later）
许可证全文：https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
源码获取：https://github.com/libvips/libvips/releases/tag/v8.18.7

使用方式：本程序以「独立的动态链接库 + 运行时加载」方式调用 libvips
（不静态链接、不修改其源码），符合 LGPL 的动态链接条款。
你可以用自行编译的同版本 libvips-42.dll 替换本文件，程序无需改动。

组件版本明细见同目录 versions.json。

本软件仅将 libvips 用于超大图像（TIFF 等）的 90° 旋转，
以降低峰值内存占用；调用失败时会自动回退到 Windows 自带的图像处理链路。
