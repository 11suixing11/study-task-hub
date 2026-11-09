from __future__ import annotations

import html
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'design/icons'
CATALOG = json.loads((ROOT / 'src/AccessProbe/Assets/Icons/catalog.json').read_text(encoding='utf-8-sig'))
ICONS = CATALOG['icons']
BY_KEY = {icon['key']: icon for icon in ICONS}
FONT = Path('C:/Windows/Fonts/msyh.ttc')
BOLD = Path('C:/Windows/Fonts/msyhbd.ttc')
INK = '#20314D'
MUTED = '#66758B'
CANVAS = Image.new('RGB', (1600, 1460), '#F3F6FC')
DRAW = ImageDraw.Draw(CANVAS)
TEXT_BOUNDS = []


def font(size: int, bold: bool = False):
    return ImageFont.truetype(str(BOLD if bold else FONT), size)


def text(value: str, xy: tuple[int, int], size: int, color: str = INK, bold: bool = False, center: bool = False):
    face = font(size, bold)
    x, y = xy
    box = DRAW.textbbox((0, 0), value, font=face)
    if center:
        x -= (box[2] - box[0]) // 2
    DRAW.text((x, y - box[1]), value, font=face, fill=color)
    bounds = (x + box[0], y, x + box[2], y + box[3] - box[1])
    assert 0 <= bounds[0] < bounds[2] <= CANVAS.width
    assert 0 <= bounds[1] < bounds[3] <= CANVAS.height
    TEXT_BOUNDS.append({'text': value, 'bounds': bounds})


def icon(key: str, xy: tuple[int, int], size: int):
    with Image.open(ASSETS / 'png' / f'{key}.png') as source:
        art = source.convert('RGBA').resize((size, size), Image.Resampling.LANCZOS)
    assert art.getbbox() is not None
    CANVAS.paste(art, xy, art)


def panel(bounds: tuple[int, int, int, int]):
    left, top, right, bottom = bounds
    DRAW.rounded_rectangle((left, top + 5, right, bottom + 5), radius=24, fill='#E8EEF9')
    DRAW.rounded_rectangle(bounds, radius=24, fill='white', outline='#DFE6F2', width=1)


icon('app', (56, 50), 146)
text('聚合平台', (232, 56), 43, bold=True)
text('macOS 风格 · Fluent 开源图标', (234, 119), 25, MUTED)
text('彩色入口，清晰操作；平台来源沿用鲜艳识别色。', (234, 166), 19, MUTED)
DRAW.rounded_rectangle((1350, 57, 1544, 101), radius=22, fill='#E2ECFF')
text(f'{len(ICONS)} 枚统一图标', (1447, 69), 18, '#006BE6', bold=True, center=True)
text('2026.11.07', (1447, 123), 16, MUTED, center=True)

text('任务分类', (56, 243), 24, bold=True)
text('Fluent 彩色矢量', (226, 249), 17, MUTED)
panel((56, 285, 1544, 444))
categories = [art for art in ICONS if art['group'] == 'category']
for index, art in enumerate(categories):
    cx = 205 + index * 298
    icon(art['key'], (cx - 34, 308), 68)
    text(art['label'], (cx, 401), 19, center=True)

text('平台来源', (56, 484), 24, bold=True)
text('八个平台与手动任务', (226, 490), 17, MUTED)
panel((56, 526, 1544, 709))
platforms = [art for art in ICONS if art['group'] == 'platform']
short_labels = {'mooc': '大学 MOOC', 'ucampus': 'U 校园'}
for index, art in enumerate(platforms):
    cx = 139 + index * 165
    icon(art['key'], (cx - 33, 551), 66)
    text(short_labels.get(art['key'], art['label']), (cx, 641), 17, center=True)
    text(art['key'], (cx, 675), 13, MUTED, center=True)

text('操作与状态', (56, 749), 24, bold=True)
text('Fluent 线条 · 随按钮配色', (248, 755), 17, MUTED)
panel((56, 791, 1544, 1249))
actions = [art for art in ICONS if art['group'] in {'action', 'state'}]
short_actions = {
    'sync': '同步', 'courses': '课程', 'add': '新增', 'sidebar': '侧栏', 'inspector': '详情面板',
    'settings': '设置', 'search': '搜索', 'open': '官方入口', 'edit': '编辑', 'more': '更多',
    'info': '任务详情', 'saved': '登录已保存', 'copy': '复制申请', 'delete': '删除',
    'returned': '被打回', 'review': '待核对', 'ignored': '已忽略',
    'save': '保存', 'connect': '连接平台', 'stop': '停止采集',
    'check-all': '全选', 'clear-selection': '全不选',
}
for index, art in enumerate(actions):
    row, column = divmod(index, 9)
    cx = 139 + column * 165
    y = 818 + row * 142
    DRAW.rounded_rectangle((cx - 30, y, cx + 30, y + 60), radius=16, fill='#F2F5FB')
    icon(art['key'], (cx - 22, y + 8), 44)
    text(short_actions[art['key']], (cx, y + 80), 17, center=True)

text('小尺寸也保持清晰', (56, 1296), 22, bold=True)
DRAW.rounded_rectangle((400, 1286, 1544, 1396), radius=20, fill='#EAF0FA')
for index, size in enumerate((16, 20, 24, 32, 48, 64)):
    cx = 476 + index * 180
    icon('app', (cx - size // 2, 1299 + (64 - size) // 2), size)
    text(f'{size} px', (cx, 1370), 13, MUTED, center=True)
text('SVG · PNG · Windows ICO', (56, 1352), 17, MUTED)
text('Fluent：MIT 许可 / 品牌与八个平台：原创图形', (56, 1420), 14, MUTED)
text('聚合平台 · 开源图标版', (1342, 1420), 14, MUTED)

CANVAS.save(ASSETS / '图标设计总览.png', optimize=True)
(ASSETS / 'overview-layout.json').write_text(json.dumps({
    'width': CANVAS.width, 'height': CANVAS.height, 'icons': len(ICONS),
    'textBoundsWithinCanvas': True, 'text': TEXT_BOUNDS,
}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')

sections = []
for groups, title in (({'category'}, '任务分类'), ({'platform'}, '平台来源'), ({'action', 'state'}, '操作与状态')):
    cards = []
    for art in ICONS:
        if art['group'] not in groups:
            continue
        key, label = art['key'], html.escape(art['label'])
        svg = (ASSETS / 'svg' / f'{key}.svg').read_text(encoding='utf-8-sig')
        svg = svg.replace('id="tile"', f'id="tile-{key}"').replace('url(#tile)', f'url(#tile-{key})')
        cards.append(f'<article class="card"><div class="art">{svg}</div><strong>{label}</strong>'
                     f'<small>{key}</small><a href="svg/{key}.svg" download>SVG</a>'
                     f'<a href="png/{key}.png" download>PNG</a></article>')
    sections.append(f'<section><h2>{title}</h2><div class="grid">{"".join(cards)}</div></section>')

body = '''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>聚合平台 · 图标设计总览</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#f3f6fc;color:#20314d;font:15px/1.6 "Segoe UI","Microsoft YaHei",sans-serif}
main{max-width:1240px;margin:auto;padding:44px 28px 36px}header{display:flex;align-items:center;gap:28px;margin-bottom:40px}
header img{width:132px;height:132px}h1{font-size:34px;letter-spacing:-1px;margin:0 0 8px}p{color:#66758b;margin:0 0 8px}
.pill{display:inline-block;border-radius:16px;background:#e2ecff;color:#006be6;padding:4px 12px;font-size:13px}
h2{font-size:21px;margin:34px 0 14px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(128px,1fr));gap:14px}
.card{background:white;border:1px solid #e0e7f2;border-radius:20px;padding:20px 10px 14px;text-align:center}
.art{width:64px;height:64px;margin:0 auto 14px;color:#34445b}.art svg{width:100%;height:100%}
strong,small{display:block}strong{font-size:14px}small{color:#7d8a9e;font-size:12px;margin:3px 0 9px}
a{color:#006be6;text-decoration:none;margin:0 5px}a:hover{text-decoration:underline}a:focus-visible{outline:2px solid #006be6;outline-offset:4px}
.sizes{display:flex;flex-wrap:wrap;align-items:end;gap:34px;padding:24px;background:#eaf0fa;border-radius:20px}
.sizes span{text-align:center}.sizes img{display:block;margin:0 auto 10px}.downloads{margin-top:28px}footer{color:#7d8a9e;margin-top:32px;font-size:13px}
@media(max-width:600px){main{padding:24px 16px}header{gap:16px}header img{width:82px;height:82px}h1{font-size:25px}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media print{body{background:white}.card,header,section{break-inside:avoid}a{color:#34445b}}
</style></head><body><main>
<header><img src="app-1024.png" alt="三张任务卡汇聚成完成勾的聚合平台主图标"><div><h1>聚合平台</h1>
<p>macOS 风格 · Fluent 开源图标</p><p>彩色入口，清晰操作；平台来源沿用鲜艳识别色。</p><span class="pill">__ICON_COUNT__ 枚统一图标</span></div></header>
__SECTIONS__
<section><h2>小尺寸也保持清晰</h2><div class="sizes">__SIZES__</div></section>
<p class="downloads"><a href="图标设计总览.png">查看整套展示图</a> · <a href="app.ico" download>下载 Windows ICO</a> · <a href="svg/app.svg" download>下载主图标 SVG</a> · <a href="app-1024.png" download>下载 1024 px 主图标</a></p>
<footer>Fluent System Icons：MIT 许可 · 品牌与八个平台为原创图形 · <a href="Fluent来源清单.json">来源记录</a> · <a href="Fluent-LICENSE.txt">许可证</a><br>聚合平台 · macOS 正式安装版 · 2026.11.07</footer></main></body></html>
'''
sizes = ''.join(f'<span><img src="svg/app.svg" alt="{size} 像素主图标" width="{size}" height="{size}"><small>{size} px</small></span>'
                for size in (16, 20, 24, 32, 48, 64))
(ASSETS / '图标设计总览.html').write_text(body.replace('__SECTIONS__', ''.join(sections)).replace('__SIZES__', sizes).replace('__ICON_COUNT__', str(len(ICONS))), encoding='utf-8')
print(json.dumps({'outcome': 'created', 'icons': len(ICONS), 'overview': str(ASSETS / '图标设计总览.png'),
                  'size': CANVAS.size, 'html': str(ASSETS / '图标设计总览.html')}, ensure_ascii=False))
