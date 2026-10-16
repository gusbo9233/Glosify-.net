"""Texture helpers for the Kwame and Mei builds: UV rasterising, blur and generated images."""
import numpy as np
import bpy


def clamp(value): return max(0., min(1., value))
def smooth(a, b, t):
    t = clamp((t-a)/(b-a)); return t*t*(3-2*t)
def lerp(a, b, t): return a + (b-a)*clamp(t)


def blur(values, radius):
    """Separable box blur over a 2D array, clamped at the edges (as build-rain.py)."""
    if radius < 1: return values
    for axis in (0, 1):
        total = np.cumsum(np.pad(values, [(radius+1, radius) if i == axis else (0, 0) for i in range(2)], mode='edge'), axis=axis)
        values = (np.take(total, range(2*radius+1, total.shape[axis]), axis=axis)
                  - np.take(total, range(0, total.shape[axis]-2*radius-1), axis=axis))/(2*radius+1)
    return values


def rasterize(mesh, values, width, height, *, keep=lambda poly: True, soften=5):
    """Interpolate per-vertex values across the mesh's UV triangles into a (height, width) mask.
    Returns (mask, cover); softening only spreads within painted UV islands so seams stay hidden."""
    uv = mesh.uv_layers.active.data
    mask = np.zeros((height, width), np.float32); cover = np.zeros((height, width), np.float32)
    for poly in mesh.polygons:
        if not keep(poly): continue
        for k in range(1, len(poly.vertices)-1):
            corners = [0, k, k+1]
            p = np.array([(uv[poly.loop_indices[c]].uv[0]*width, uv[poly.loop_indices[c]].uv[1]*height) for c in corners])
            m = np.array([values[poly.vertices[c]] for c in corners])
            x0, y0 = np.maximum(np.floor(p.min(0)).astype(int), 0)
            x1, y1 = np.minimum(np.ceil(p.max(0)).astype(int), (width-1, height-1))
            area = (p[1, 1]-p[2, 1])*(p[0, 0]-p[2, 0]) + (p[2, 0]-p[1, 0])*(p[0, 1]-p[2, 1])
            if x1 < x0 or y1 < y0 or abs(area) < 1e-9: continue
            gx, gy = np.meshgrid(np.arange(x0, x1+1)+.5, np.arange(y0, y1+1)+.5)
            a = ((p[1, 1]-p[2, 1])*(gx-p[2, 0]) + (p[2, 0]-p[1, 0])*(gy-p[2, 1]))/area
            b = ((p[2, 1]-p[0, 1])*(gx-p[2, 0]) + (p[0, 0]-p[2, 0])*(gy-p[2, 1]))/area
            inside = (a >= -.02) & (b >= -.02) & (1-a-b >= -.02)
            region = mask[y0:y1+1, x0:x1+1]
            region[inside] = np.maximum(region[inside], (a*m[0] + b*m[1] + (1-a-b)*m[2])[inside])
            cover[y0:y1+1, x0:x1+1][inside] = 1
    if soften:
        mask = np.clip(blur(mask, soften)/np.maximum(blur(cover, soften), 1e-3), 0, 1)*cover
    return mask, cover


def image_pixels(image):
    width, height = image.size
    return np.array(image.pixels[:], np.float32).reshape(height, width, 4)


def new_image(name, rgb, *, alpha=None, data=False):
    height, width, _ = rgb.shape
    pixels = np.ones((height, width, 4), np.float32); pixels[:, :, :3] = np.clip(rgb, 0, 1)
    if alpha is not None: pixels[:, :, 3] = alpha
    image = bpy.data.images.new(name, width, height, alpha=alpha is not None)
    if data: image.colorspace_settings.name = 'Non-Color'
    image.pixels.foreach_set(pixels.ravel()); image.pack()
    return image


def normal_map(height_field, strength=2., wrap=True):
    mode = 'wrap' if wrap else 'edge'
    padded = np.pad(height_field, 1, mode=mode)
    dx = padded[1:-1, 2:]-padded[1:-1, :-2]; dy = padded[2:, 1:-1]-padded[:-2, 1:-1]
    normals = np.stack((-dx*strength, -dy*strength, np.ones_like(dx)), axis=2)
    normals /= np.linalg.norm(normals, axis=2)[:, :, None]
    return normals*.5+.5


def splat(size, points, radius, weights=None):
    """Accumulate soft round dots (periodic in both axes) into a (height, width) field."""
    height, width = size
    field = np.zeros(size, np.float32)
    r = int(np.ceil(radius*2))
    offsets = [(dx, dy, np.exp(-(dx*dx+dy*dy)/(2*radius*radius))) for dx in range(-r, r+1) for dy in range(-r, r+1)]
    xs = np.floor(points[:, 0]).astype(int); ys = np.floor(points[:, 1]).astype(int)
    w = np.ones(len(points), np.float32) if weights is None else weights.astype(np.float32)
    for dx, dy, k in offsets:
        if k < .02: continue
        np.add.at(field, ((ys+dy) % height, (xs+dx) % width), w*k)
    return field
