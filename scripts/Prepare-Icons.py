"""Clean generated monochrome masks, normalize keylines, export DPI assets and a review sheet.

Requires Pillow, numpy and OpenCV. Original generated masters are preserved unchanged.
"""
from collections import deque
from pathlib import Path
import json

import numpy as np
import cv2
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ROOT / "design" / "icon-sources"
OUTPUT = ROOT / "src" / "CodexMonitor.App" / "Assets" / "Icons"
REVIEW = ROOT / "artifacts" / "icon-review"


def components(mask):
    height, width = mask.shape
    visited = bytearray(width * height)
    flat = mask.ravel()
    groups = []
    for start in np.flatnonzero(flat).tolist():
        if visited[start]:
            continue
        group, queue = [], deque([start])
        visited[start] = 1
        while queue:
            index = queue.popleft()
            group.append(index)
            x = index % width
            for neighbor in (index - width, index + width, index - 1 if x else -1, index + 1 if x + 1 < width else -1):
                if 0 <= neighbor < width * height and not visited[neighbor] and flat[neighbor]:
                    visited[neighbor] = 1
                    queue.append(neighbor)
        groups.append(group)
    return sorted(groups, key=len, reverse=True)


def clean(source):
    rgba = np.asarray(Image.open(source).convert("RGBA"))
    # The generator's haze has low alpha. Separate the solid contour before resampling;
    # do not preserve a textured matte or bake a background color into the icon.
    seed = Image.fromarray(np.uint8(rgba[:, :, 3] >= 200) * 255).filter(ImageFilter.MedianFilter(3))
    mask = np.asarray(seed) > 0
    groups = components(mask)
    if not groups:
        raise ValueError(f"No solid icon contour: {source}")
    minimum = max(32, len(groups[0]) * 0.002)
    kept = [group for group in groups if len(group) >= minimum]
    final = np.zeros(mask.size, dtype=np.uint8)
    for group in kept:
        final[group] = 255
    matte = Image.fromarray(final.reshape(mask.shape))
    bounds = matte.getbbox()
    contour = matte.crop(bounds)
    if source.stem.startswith("Disconnect."):
        contour = contour.rotate(-35, Image.Resampling.BICUBIC, expand=True)
        contour = Image.fromarray(np.uint8(np.asarray(contour) >= 128) * 255)
    alpha = normalize(contour)
    before = stroke(np.asarray(alpha) >= 128)
    # Use the same apparent 1-DIP regular stroke at 16 DIP; preserve connected
    # parts and holes, so a narrow gap or a small intentional dot cannot vanish.
    for _ in range(2):
        binary = np.uint8(np.asarray(alpha) >= 128)
        current = stroke(binary)
        radius = round(abs(64 - current) / 2)
        if radius < 2:
            break
        for trial in range(radius, 0, -1):
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (trial * 2 + 1, trial * 2 + 1))
            candidate = cv2.dilate(binary, kernel) if current < 64 else cv2.erode(binary, kernel)
            if topology(candidate) == topology(binary):
                alpha = normalize(Image.fromarray(candidate * 255))
                break
    result = Image.new("RGBA", (1024, 1024), (255, 255, 255, 0))
    result.putalpha(alpha)
    report = {"source": source.name, "sourceSize": list(rgba.shape[1::-1]), "sourceBounds": list(bounds), "solidComponents": len(kept),
              "removedLowAlphaPixels": int(np.sum((rgba[:, :, 3] > 0) & (rgba[:, :, 3] < 200))), "discardedComponents": len(groups) - len(kept),
              "strokeBefore": round(before, 2), "strokeAfter": round(stroke(np.asarray(alpha) >= 128), 2),
              "keyline": "20/24", "borderAlpha": int(max(np.asarray(alpha)[0].max(), np.asarray(alpha)[-1].max(), np.asarray(alpha)[:, 0].max(), np.asarray(alpha)[:, -1].max()))}
    if report["borderAlpha"] != 0:
        raise ValueError(f"Icon touches its canvas edge: {source}")
    return result, report


def normalize(contour):
    contour = contour.crop(contour.getbbox())
    scale = (1024 * 20 / 24) / max(contour.size)
    contour = contour.resize((round(contour.width * scale), round(contour.height * scale)), Image.Resampling.LANCZOS)
    alpha = Image.new("L", (1024, 1024))
    alpha.paste(contour, ((1024 - contour.width) // 2, (1024 - contour.height) // 2))
    return alpha


def stroke(mask):
    distance = cv2.distanceTransform(np.uint8(mask), cv2.DIST_L2, cv2.DIST_MASK_PRECISE)
    ridge = (distance >= cv2.dilate(distance, np.ones((3, 3), np.uint8)) - 0.01) & (distance > 2)
    return float(np.median(distance[ridge])) * 2


def topology(mask):
    count = cv2.connectedComponents(np.uint8(mask))[0]
    _, hierarchy = cv2.findContours(np.uint8(mask), cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)
    holes = int(np.sum(hierarchy[0, :, 3] >= 0)) if hierarchy is not None else 0
    return count, holes


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    REVIEW.mkdir(parents=True, exist_ok=True)
    sources = sorted(SOURCES.glob("*.generated.png"))
    if not sources:
        raise ValueError("No generated icon masters")
    sheet = Image.new("RGB", (1160, 120 * len(sources)), "#eeeeee")
    draw = ImageDraw.Draw(sheet)
    reports = []
    for row, source in enumerate(sources):
        name = source.name.removesuffix(".generated.png")
        result, report = clean(source)
        result.save(SOURCES / f"{name}.png")
        for scale, size in [(100, 16), (125, 20), (150, 24), (200, 32), (250, 40), (300, 48), (400, 64)]:
            suffix = "" if scale == 100 else f".scale-{scale}"
            result.resize((size, size), Image.Resampling.LANCZOS).save(OUTPUT / f"{name}{suffix}.png")
        y = row * 120
        draw.text((12, y + 12), name, fill="black")
        for column, background in enumerate(["#f8f8f8", "#202020"]):
            draw.rectangle((220 + column * 460, y, 680 + column * 460, y + 119), fill=background)
            foreground = (35, 35, 35, 255) if column == 0 else (235, 235, 235, 255)
            x = 238 + column * 460
            for size in (16, 20, 24, 32, 48, 64):
                icon = Image.new("RGBA", (size, size), foreground)
                icon.putalpha(result.getchannel("A").resize((size, size), Image.Resampling.LANCZOS))
                sheet.paste(icon, (x, y + (120 - size) // 2), icon)
                x += size + 24
        reports.append({"name": name, **report})
    sheet.save(REVIEW / "icons-light-dark.png")
    (REVIEW / "verification.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
    if any(not 56 <= item["strokeAfter"] <= 72 for item in reports):
        raise ValueError("An icon's apparent regular stroke is outside the accepted range")
    brand_source = ROOT / "design" / "app-icon" / "AppIcon.generated.png"
    if brand_source.exists():
        brand = Image.open(brand_source).convert("RGBA")
        pixels = np.array(brand)
        faint = (pixels[:, :, 3] > 0) & (pixels[:, :, 3] < 8)
        pixels[faint, 3] = 0  # Preserve the colored material and intentional shadow; remove negligible matte noise only.
        brand = Image.fromarray(pixels)
        brand.save(OUTPUT.parent / "AppIcon.png")
        brand.save(OUTPUT.parent / "AppIcon.ico", sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])
        (REVIEW / "app-icon-verification.json").write_text(json.dumps({"source": str(brand_source.relative_to(ROOT)), "removedFaintPixels": int(faint.sum()), "preservedColor": True, "icoSizes": [16,24,32,48,64,128,256]}), encoding="utf-8")
    print(json.dumps({"icons": len(reports), "dpiAssets": len(reports) * 7, "review": str(REVIEW / "icons-light-dark.png"), "transparentBorders": all(x["borderAlpha"] == 0 for x in reports)}))


if __name__ == "__main__":
    main()
