"""Convert the authored product SVG into native WPF vector artwork, offline."""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "src/AccessProbe/Assets/Icons/Brand"
SOURCE = BRAND / "app.svg"
PRESENTATION = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XAML = "http://schemas.microsoft.com/winfx/2006/xaml"
ET.register_namespace("", PRESENTATION)
ET.register_namespace("x", XAML)


def element(parent, kind, **attributes):
    return ET.SubElement(parent, f"{{{PRESENTATION}}}{kind}", attributes)


def short(node):
    return node.tag.split("}")[-1]


def color(value, opacity=1.0):
    assert re.fullmatch(r"#[0-9a-fA-F]{6}", value), value
    return f"#{round(opacity * 255):02X}{value[1:]}" if opacity != 1 else value


svg = ET.parse(SOURCE).getroot()
assert svg.attrib["viewBox"] == "0 0 32 32"
assert {short(node) for node in svg.iter()} <= {"svg", "title", "defs", "path", "linearGradient", "radialGradient", "stop"}
gradients = {node.attrib["id"]: node for node in svg.iter() if short(node) in {"linearGradient", "radialGradient"}}


def brush(parent, property_name, paint, opacity=1.0):
    property_node = element(parent, property_name)
    if not paint.startswith("url(#"):
        element(property_node, "SolidColorBrush", Color=color(paint, opacity))
        return
    gradient = gradients[paint[5:-1]]
    assert gradient.attrib["gradientUnits"] == "userSpaceOnUse"
    if short(gradient) == "linearGradient":
        result = element(property_node, "LinearGradientBrush", MappingMode="Absolute",
                         StartPoint=f"{gradient.attrib['x1']},{gradient.attrib['y1']}",
                         EndPoint=f"{gradient.attrib['x2']},{gradient.attrib['y2']}")
    else:
        result = element(property_node, "RadialGradientBrush", MappingMode="Absolute",
                         Center=f"{gradient.attrib['cx']},{gradient.attrib['cy']}",
                         GradientOrigin=f"{gradient.attrib['cx']},{gradient.attrib['cy']}",
                         RadiusX=gradient.attrib["r"], RadiusY=gradient.attrib["r"])
    for stop in gradient:
        assert short(stop) == "stop"
        element(result, "GradientStop", Offset=stop.attrib["offset"],
                Color=color(stop.attrib["stop-color"], opacity * float(stop.attrib.get("stop-opacity", "1"))))


dictionary = ET.Element(f"{{{PRESENTATION}}}ResourceDictionary")
image = element(dictionary, "DrawingImage", **{f"{{{XAML}}}Key": "ProductBrand.App"})
image_property = element(image, "DrawingImage.Drawing")
artboard = element(image_property, "DrawingGroup")
element(artboard, "GeometryDrawing", Brush="Transparent", Geometry="M0,0 H32 V32 H0 Z")
paths = [node for node in svg if short(node) == "path"]
for path in paths:
    assert not set(path.attrib) - {"d", "fill", "fill-opacity", "stroke", "stroke-opacity", "stroke-width", "stroke-linecap", "stroke-linejoin"}, path.attrib
    drawing = element(artboard, "GeometryDrawing", Geometry="F1 " + path.attrib["d"])
    if path.attrib.get("fill", "none") != "none":
        brush(drawing, "GeometryDrawing.Brush", path.attrib["fill"], float(path.attrib.get("fill-opacity", "1")))
    if path.attrib.get("stroke", "none") != "none":
        property_node = element(drawing, "GeometryDrawing.Pen")
        pen = element(property_node, "Pen", Thickness=path.attrib.get("stroke-width", "1"),
                      StartLineCap="Round", EndLineCap="Round", LineJoin="Round")
        brush(pen, "Pen.Brush", path.attrib["stroke"], float(path.attrib.get("stroke-opacity", "1")))

ET.indent(dictionary, space="  ")
output = BRAND / "BrandIcon.xaml"
output.write_bytes(ET.tostring(dictionary, encoding="utf-8", xml_declaration=True) + b"\n")
print(json.dumps({"outcome": "generated", "source": SOURCE.relative_to(ROOT).as_posix(),
                  "sourceSHA256": hashlib.sha256(SOURCE.read_bytes()).hexdigest(), "layers": len(paths),
                  "nativeVectorRendering": True, "newRuntimeDependencies": 0}))
