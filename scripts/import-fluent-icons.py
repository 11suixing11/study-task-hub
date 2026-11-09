from __future__ import annotations

import hashlib
import json
import math
import re
from pathlib import Path
from xml.etree import ElementTree as ET

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'src/AccessProbe/Assets/Icons'
FLUENT = ASSETS / 'Fluent'
MANIFEST = json.loads((FLUENT / 'sources.json').read_text(encoding='utf-8-sig'))
CATALOG_PATH = ASSETS / 'catalog.json'
CATALOG = json.loads(CATALOG_PATH.read_text(encoding='utf-8-sig'))
PRESENTATION = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
XAML = 'http://schemas.microsoft.com/winfx/2006/xaml'
ET.register_namespace('', PRESENTATION)
ET.register_namespace('x', XAML)


def xaml(parent, kind, **attributes):
    return ET.SubElement(parent, f'{{{PRESENTATION}}}{kind}', attributes)


def short(element):
    return element.tag.split('}')[-1]


def matrix(value):
    # SVG composes the listed matrices from left to right; XAML accepts the resulting affine matrix.
    result = (1., 0., 0., 1., 0., 0.)
    for name, parameters in re.findall(r'([a-zA-Z]+)\s*\(([^)]+)\)', value):
        p = [float(item) for item in re.split(r'[\s,]+', parameters.strip())]
        if name == 'translate':
            current = (1., 0., 0., 1., p[0], p[1] if len(p) > 1 else 0.)
        elif name == 'scale':
            current = (p[0], 0., 0., p[1] if len(p) > 1 else p[0], 0., 0.)
        elif name == 'rotate' and len(p) == 1:
            angle = math.radians(p[0])
            current = (math.cos(angle), math.sin(angle), -math.sin(angle), math.cos(angle), 0., 0.)
        elif name == 'matrix' and len(p) == 6:
            current = tuple(p)
        else:
            raise ValueError(f'Unsupported SVG transform: {name}({parameters})')
        a, b, c, d, e, f = result
        g, h, i, j, k, l = current
        result = (a*g+c*h, b*g+d*h, a*i+c*j, b*i+d*j, a*k+c*l+e, b*k+d*l+f)
    assert re.sub(r'([a-zA-Z]+)\s*\(([^)]+)\)', '', value).strip() == '', value
    return ','.join(f'{number:.12g}' for number in result)


def color(value, opacity=1.):
    value = {'white': '#FFFFFF', 'black': '#000000'}.get(value, value)
    assert re.fullmatch(r'#[0-9a-fA-F]{6}', value), value
    return f'#{round(opacity * 255):02X}{value[1:]}' if opacity != 1 else value


def add_brush(parent, svg, paint):
    property_element = xaml(parent, 'GeometryDrawing.Brush')
    if not paint.startswith('url(#'):
        xaml(property_element, 'SolidColorBrush', Color=color(paint))
        return
    paint_id = paint[5:-1]
    gradient = next(element for element in svg.iter() if element.attrib.get('id') == paint_id)
    assert gradient.attrib.get('gradientUnits') == 'userSpaceOnUse', paint_id
    kind = short(gradient)
    allowed = {'id', 'gradientUnits', 'gradientTransform'}
    if kind == 'linearGradient':
        allowed |= {'x1', 'y1', 'x2', 'y2'}
        brush = xaml(property_element, 'LinearGradientBrush', MappingMode='Absolute',
                     StartPoint=f"{gradient.attrib.get('x1', '0')},{gradient.attrib.get('y1', '0')}",
                     EndPoint=f"{gradient.attrib.get('x2', '1')},{gradient.attrib.get('y2', '0')}")
    elif kind == 'radialGradient':
        allowed |= {'cx', 'cy', 'r', 'fx', 'fy'}
        brush = xaml(property_element, 'RadialGradientBrush', MappingMode='Absolute',
                     Center=f"{gradient.attrib['cx']},{gradient.attrib['cy']}",
                     GradientOrigin=f"{gradient.attrib.get('fx', gradient.attrib['cx'])},{gradient.attrib.get('fy', gradient.attrib['cy'])}",
                     RadiusX=gradient.attrib['r'], RadiusY=gradient.attrib['r'])
    else:
        raise ValueError(f'Unsupported SVG paint: {kind}')
    assert not set(gradient.attrib) - allowed, gradient.attrib
    for stop in gradient:
        assert short(stop) == 'stop' and not set(stop.attrib) - {'offset', 'stop-color', 'stop-opacity'}
        offset = stop.attrib.get('offset', '0')
        if offset.endswith('%'):
            offset = str(float(offset[:-1]) / 100)
        xaml(brush, 'GradientStop', Color=color(stop.attrib['stop-color'], float(stop.attrib.get('stop-opacity', '1'))), Offset=offset)
    if transform := gradient.attrib.get('gradientTransform'):
        transform_property = xaml(brush, f'{brush.tag.split("}")[-1]}.Transform')
        xaml(transform_property, 'MatrixTransform', Matrix=matrix(transform))


resources = ET.Element(f'{{{PRESENTATION}}}ResourceDictionary')
regular_count = 0
color_count = 0
by_key = {item['key']: item for item in CATALOG['icons']}
for asset in MANIFEST['assets']:
    source = ROOT / asset['path']
    assert hashlib.sha256(source.read_bytes()).hexdigest() == asset['sha256'], source
    svg = ET.parse(source).getroot()
    assert svg.attrib['viewBox'] == '0 0 24 24'
    assert {short(element) for element in svg.iter()} <= {'svg', 'path', 'defs', 'linearGradient', 'radialGradient', 'stop'}
    icon = by_key[asset['key']]
    paths = [element for element in svg if short(element) == 'path']
    assert paths, source
    if asset['style'] == 'regular':
        parts = []
        for path in paths:
            assert not set(path.attrib) - {'d', 'fill', 'fill-rule', 'clip-rule', 'opacity', 'fill-opacity'}, path.attrib
            assert path.attrib.get('fill', svg.attrib.get('fill')) not in {'none', None}
            pen = SVGPathPen(None)
            parse_path(path.attrib['d'], TransformPen(pen, (32/24, 0, 0, 32/24, 0, 0)))
            parts.append({'path': pen.getCommands(), 'fill': True,
                          'fillRule': path.attrib.get('fill-rule', 'nonzero'),
                          'opacity': float(path.attrib.get('opacity', '1')) * float(path.attrib.get('fill-opacity', '1'))})
        icon['parts'] = parts
        icon['provider'] = 'Microsoft Fluent System Icons'
        icon['regularSvg'] = source.relative_to(ASSETS).as_posix()
        regular_count += 1
    else:
        key = f"FluentColor.{asset['key']}"
        image = xaml(resources, 'DrawingImage', **{f'{{{XAML}}}Key': key})
        image_property = xaml(image, 'DrawingImage.Drawing')
        artboard = xaml(image_property, 'DrawingGroup')
        xaml(artboard, 'GeometryDrawing', Brush='Transparent', Geometry='M0,0 H32 V32 H0 Z')
        artwork = xaml(artboard, 'DrawingGroup')
        scale = xaml(artwork, 'DrawingGroup.Transform')
        xaml(scale, 'ScaleTransform', ScaleX='1.33333333333333', ScaleY='1.33333333333333')
        for path in paths:
            assert not set(path.attrib) - {'d', 'fill', 'fill-rule', 'clip-rule', 'opacity', 'fill-opacity'}, path.attrib
            layer = xaml(artwork, 'DrawingGroup', Opacity=str(float(path.attrib.get('opacity', '1')) * float(path.attrib.get('fill-opacity', '1'))))
            geometry = ('F0 ' if path.attrib.get('fill-rule') == 'evenodd' else 'F1 ') + path.attrib['d']
            drawing = xaml(layer, 'GeometryDrawing', Geometry=geometry)
            add_brush(drawing, svg, path.attrib['fill'])
        icon['colorResource'] = key
        icon['colorSvg'] = source.relative_to(ASSETS).as_posix()
        color_count += 1

assert regular_count == 28 and color_count == 7
ET.indent(resources, space='  ')
(FLUENT / 'FluentIcons.xaml').write_bytes(ET.tostring(resources, encoding='utf-8', xml_declaration=True) + b'\n')
CATALOG['schema'] = 2
CATALOG['fluentRevision'] = MANIFEST['revision']
CATALOG_PATH.write_text(json.dumps(CATALOG, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'outcome': 'imported', 'regular': regular_count, 'color': color_count,
                  'nativeVectorRendering': True, 'newRuntimeDependencies': 0}, ensure_ascii=False))
