#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""从 assets/app-icon-source.png 生成双端应用图标。

产出：
  1. Windows：assets/app.ico（含 16/24/32/48/64/128/256 多个尺寸，
     小尺寸必须单独重采样，直接让系统缩放 256 会糊成一团）
  2. Android：各密度 mipmap 下的方形与圆形图标 PNG
  3. 应用内共用图：assets/app-icon-512.png

圆角处理：源图是白底方图。Windows 11 与 Android 都用圆角方形图标，
所以这里按圆角裁掉四角并做 1px 抗锯齿过渡，避免出现生硬的白色方块。

用法：python generate-app-icons.py
"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = HERE
while not os.path.exists(os.path.join(ROOT, 'README.md')):
    parent = os.path.dirname(ROOT)
    if parent == ROOT:
        raise SystemExit('找不到项目根目录')
    ROOT = parent

SOURCE = os.path.join(ROOT, 'assets', 'app-icon-source.png')
ICO_OUT = os.path.join(ROOT, 'assets', 'app.ico')
SHARED_PNG = os.path.join(ROOT, 'assets', 'app-icon-512.png')

# Android 各密度下 launcher 图标的边长（48dp 基准）
DENSITIES = {
    'mipmap-mdpi': 48,
    'mipmap-hdpi': 72,
    'mipmap-xhdpi': 96,
    'mipmap-xxhdpi': 144,
    'mipmap-xxxhdpi': 192,
}


def rounded_mask(size, radius_ratio=0.18, supersample=4):
    """生成圆角方形蒙版。先在高分辨率上画再缩小，得到平滑边缘。"""
    big = size * supersample
    mask = Image.new('L', (big, big), 0)
    draw = ImageDraw.Draw(mask)
    radius = int(big * radius_ratio)
    draw.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def make_icon(size, rounded=True):
    """把源图缩放到指定尺寸，可选圆角。"""
    base = Image.open(SOURCE).convert('RGBA')
    resized = base.resize((size, size), Image.LANCZOS)
    if not rounded:
        return resized
    out = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    out.paste(resized, (0, 0), rounded_mask(size))
    return out


def main():
    if not os.path.exists(SOURCE):
        raise SystemExit(f'找不到源图：{SOURCE}')

    # ---------- Windows .ico ----------
    # 小尺寸用圆角，大尺寸也统一圆角，保持任务栏/资源管理器观感一致
    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [make_icon(s) for s in ico_sizes]
    frames[-1].save(ICO_OUT, format='ICO',
                    sizes=[(s, s) for s in ico_sizes])
    print(f'生成 {os.path.relpath(ICO_OUT, ROOT)}  ({len(ico_sizes)} 个尺寸)')

    # ---------- 共用 PNG ----------
    make_icon(512).save(SHARED_PNG, format='PNG')
    print(f'生成 {os.path.relpath(SHARED_PNG, ROOT)}')

    # ---------- Android mipmap ----------
    for folder, size in DENSITIES.items():
        target_dir = os.path.join(ROOT, 'PenClient', 'app', 'src', 'main', 'res', folder)
        os.makedirs(target_dir, exist_ok=True)

        square = os.path.join(target_dir, 'ic_launcher.png')
        make_icon(size).save(square, format='PNG')

        # 圆形图标：直接按圆形裁切，交给系统用作圆形桌面
        base = Image.open(SOURCE).convert('RGBA').resize((size, size), Image.LANCZOS)
        circle = Image.new('L', (size * 4, size * 4), 0)
        ImageDraw.Draw(circle).ellipse([0, 0, size * 4 - 1, size * 4 - 1], fill=255)
        circle = circle.resize((size, size), Image.LANCZOS)
        round_out = Image.new('RGBA', (size, size), (0, 0, 0, 0))
        round_out.paste(base, (0, 0), circle)
        round_out.save(os.path.join(target_dir, 'ic_launcher_round.png'), format='PNG')

        print(f'生成 {folder}/ic_launcher.png + ic_launcher_round.png  ({size}px)')

    print('完成')


if __name__ == '__main__':
    main()
