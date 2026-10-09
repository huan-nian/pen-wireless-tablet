#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成 Android 端左侧工具栏的矢量图标。

为什么用脚本生成而不是手写 11 个 XML：这些图标形状简单但字段重复，
手写容易把 tint/viewport 之类的属性写漏。图标统一使用 24x24 视口、
2 像素描边、圆头圆角，保证在同一列里视觉重量一致。

用法：python generate-tool-icons.py
"""
import os

# 向上找到包含 settings.gradle.kts 的目录作为项目根，避免脚本放在 tools/ 下时
# 把资源写到 tools/app/... 里去。
_here = os.path.dirname(os.path.abspath(__file__))
ROOT = _here
while not os.path.exists(os.path.join(ROOT, 'settings.gradle.kts')):
    parent = os.path.dirname(ROOT)
    if parent == ROOT:
        raise SystemExit('找不到 settings.gradle.kts，无法确定项目根目录')
    ROOT = parent

RES = os.path.join(ROOT, 'app', 'src', 'main', 'res', 'drawable')

# 统一描边参数，保证一列图标粗细一致
STROKE = ('android:strokeColor="#FFFFFF" android:strokeWidth="1.8" '
          'android:strokeLineCap="round" android:strokeLineJoin="round"')


def vector(body, filled=False):
    """把一个或多个 path 的 body 包成完整的 vector drawable。"""
    return f'''<?xml version="1.0" encoding="utf-8"?>
<!--
  由 tools/generate-tool-icons.py 生成的工具栏图标。
  统一 24x24 视口，线宽 1.8，圆头圆角；颜色为白色，实际着色由 ImageButton 的
  tint 控制（见 styles.xml 的 ToolButton）。
-->
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="24dp"
    android:height="24dp"
    android:viewportWidth="24"
    android:viewportHeight="24">
{body}
</vector>
'''


def stroked(path_d):
    return (f'    <path\n        android:pathData="{path_d}"\n'
            f'        {STROKE}\n        android:fillColor="#00000000" />\n')


def solid(path_d):
    return (f'    <path\n        android:pathData="{path_d}"\n'
            f'        android:fillColor="#FFFFFF" />\n')


ICONS = {
    # 桌面：屋顶 + 屋身
    'ic_tool_home': stroked('M3.5,11.2 L12,4 L20.5,11.2') +
                    stroked('M5.6,10.2 L5.6,19.4 L18.4,19.4 L18.4,10.2'),

    # 多任务：后方一张卡片 + 前方一张卡片，表达「层叠的窗口」
    'ic_tool_task': stroked('M8.5,3.6 L20.4,3.6 L20.4,14.2') +
                    stroked('M3.6,8.4 L15.5,8.4 L15.5,19.6 L3.6,19.6 Z'),

    # 保存：软盘轮廓 + 标签
    'ic_tool_save': stroked('M4.4,4.4 L16.6,4.4 L19.6,7.4 L19.6,19.6 L4.4,19.6 Z') +
                    stroked('M8,4.4 L8,9.6 L15.4,9.6 L15.4,4.4') +
                    stroked('M7.4,19.6 L7.4,14 L16.6,14 L16.6,19.6'),

    # 撤销：向左弯的箭头
    'ic_tool_undo': stroked('M4.4,10.4 L9.6,10.4') +
                    stroked('M4.4,10.4 L4.4,5.2') +
                    stroked('M4.6,10.6 C8.4,5.4 15.4,6.0 17.6,10.4 '
                            'C19.6,14.4 16.6,19.6 11.6,19.6'),

    # 取消撤销：撤销的镜像
    'ic_tool_redo': stroked('M19.6,10.4 L14.4,10.4') +
                    stroked('M19.6,10.4 L19.6,5.2') +
                    stroked('M19.4,10.6 C15.6,5.4 8.6,6.0 6.4,10.4 '
                            'C4.4,14.4 7.4,19.6 12.4,19.6'),

    # 滚轮上：上下两个箭头，上方实心
    'ic_tool_scroll_up': solid('M12,3.2 L16.6,9.4 L7.4,9.4 Z') +
                         stroked('M12,12.4 L12,20.4'),

    # 滚轮下：上下两个箭头，下方实心
    'ic_tool_scroll_down': stroked('M12,3.6 L12,11.6') +
                           solid('M12,20.8 L7.4,14.6 L16.6,14.6 Z'),

    # 详情：圆圈 + i
    'ic_tool_info': stroked('M12,3.2 A8.8,8.8 0 1,1 11.99,3.2 Z') +
                    solid('M11.2,10.6 L12.9,10.6 L12.9,17.4 L11.2,17.4 Z') +
                    solid('M11.2,6.8 L12.9,6.8 L12.9,8.7 L11.2,8.7 Z'),

    # 关闭：电源符号
    'ic_tool_close': stroked('M12,3.4 L12,10.6') +
                     stroked('M7.4,6.6 C4.9,8.2 3.4,10.9 3.4,14.1 '
                             'C3.4,18.8 7.3,22.6 12,22.6 '
                             'C16.7,22.6 20.6,18.8 20.6,14.1 '
                             'C20.6,10.9 19.1,8.2 16.6,6.6'),

    # 背景色：调色盘
    'ic_palette': stroked('M12,3.4 C7.2,3.4 3.4,7.0 3.4,11.6 '
                          'C3.4,16.2 7.2,20.0 11.8,20.0 '
                          'C13.0,20.0 13.8,19.2 13.8,18.2 '
                          'C13.8,17.7 13.6,17.3 13.4,16.9 '
                          'C13.2,16.5 13.0,16.1 13.0,15.7 '
                          'C13.0,14.7 13.8,13.9 14.8,13.9 '
                          'L17.0,13.9 C19.4,13.9 20.6,12.2 20.6,10.2 '
                          'C20.6,6.4 16.8,3.4 12,3.4 Z') +
                     solid('M7.6,10.2 A1.25,1.25 0 1,1 7.59,10.2 Z') +
                     solid('M11.2,7.4 A1.25,1.25 0 1,1 11.19,7.4 Z') +
                     solid('M15.4,8.4 A1.25,1.25 0 1,1 15.39,8.4 Z'),

    # 笔迹消退：秒表
    'ic_timer': stroked('M12,6.2 A7.4,7.4 0 1,1 11.99,6.2 Z') +
                stroked('M12,6.2 L12,13.6') +
                stroked('M9.6,2.4 L14.4,2.4'),
}


def main():
    os.makedirs(RES, exist_ok=True)
    for name, body in ICONS.items():
        path = os.path.join(RES, name + '.xml')
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(vector(body))
        print('生成', os.path.relpath(path))
    print(f'共 {len(ICONS)} 个图标')


if __name__ == '__main__':
    main()
