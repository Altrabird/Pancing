"""
Pancing prop pipeline, run inside Blender (via Blender MCP or the Scripting tab).

    exec(open(r"...\\Pancing\\art\\bake_props.py").read())
    bake("Environment_PalmTree_2", "palm")      # source object -> game name
    export_all()                                 # every baked prop -> Resources/Models/*.fbx

The game has no textures: every surface is vertex-coloured and lit by
Pancing/VertexLit. bake() turns an imported Poly Pizza model into exactly that:
one mesh, world transforms applied, origin at the bottom centre, and one flat
colour per face taken from (in order) the material's image texture at the face's
UV centre, an existing colour attribute, or the material's base colour.
Baked objects go into the "Baked" collection; export_all() writes each one.
"""

import bpy
import bmesh
import os
import random
from mathutils import Vector

OUT = r"C:\Users\HARSIDI BIN JUNICK\Desktop\Pancing\unity\Pancing\Assets\Pancing\Resources\Models"


def s2l(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hexl(h):
    return tuple(s2l(int(h[i:i + 2], 16) / 255) for i in (0, 2, 4))


_px = {}


def _pixels(img):
    if img.name not in _px:
        _px[img.name] = (img.size[0], img.size[1], img.channels, list(img.pixels))
    return _px[img.name]


def _source(m):
    if not m or not m.use_nodes:
        return ('col', (0.5, 0.5, 0.5))
    bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return ('col', (0.5, 0.5, 0.5))
    inp = bsdf.inputs['Base Color']
    if inp.is_linked:
        n = inp.links[0].from_node
        if n.type == 'TEX_IMAGE' and n.image:
            return ('img', n.image)
        return ('attr', None)
    return ('col', tuple(inp.default_value[:3]))


def _baked_collection():
    c = bpy.data.collections.get("Baked")
    if c is None:
        c = bpy.data.collections.new("Baked")
        bpy.context.scene.collection.children.link(c)
    return c


def bake(src_name, new_name, attribution=""):
    """Bake the mesh object `src_name` (plus its mesh children) into prop `new_name`."""
    src = bpy.data.objects[src_name]
    objs = [src] + [c for c in src.children_recursive if c.type == 'MESH']
    objs = [o for o in objs if o.type == 'MESH']
    if not attribution:
        p = src
        while p is not None and not attribution:
            attribution = p.get('polypizza_attribution', '')
            p = p.parent

    bm_all = bmesh.new()
    face_cols = []
    for o in objs:
        me = o.data.copy()
        me.transform(o.matrix_world)
        srcs = [_source(m) for m in o.data.materials] or [('col', (0.5, 0.5, 0.5))]
        attr = me.color_attributes.active_color if me.color_attributes else None
        bm = bmesh.new()
        bm.from_mesh(me)
        uvl = bm.loops.layers.uv.active
        cl = None
        if attr is not None:
            layers = bm.loops.layers.float_color if attr.data_type == 'FLOAT_COLOR' else bm.loops.layers.color
            cl = layers.get(attr.name) if attr.domain == 'CORNER' else None
        for f in bm.faces:
            kind, v = srcs[min(f.material_index, len(srcs) - 1)]
            if kind == 'img' and uvl is not None:
                w, h, ch, px = _pixels(v)
                uv = sum((l[uvl].uv for l in f.loops), Vector((0, 0))) / len(f.loops)
                x = int((uv.x % 1.0) * (w - 1))
                y = int((uv.y % 1.0) * (h - 1))
                i = (y * w + x) * ch
                c = tuple(s2l(px[i + k]) for k in range(3))
            elif kind == 'attr' and cl is not None:
                acc = Vector((0, 0, 0))
                for l in f.loops:
                    acc += Vector(l[cl][:3])
                c = tuple(acc / len(f.loops))
            elif kind == 'col':
                c = v
            else:
                c = (0.5, 0.5, 0.5)
            face_cols.append(c)
        bm.free()
        bm_all.from_mesh(me)
        bpy.data.meshes.remove(me)

    me = bpy.data.meshes.new(new_name)
    bm_all.to_mesh(me)
    bm_all.free()
    xs = [v.co.x for v in me.vertices]
    ys = [v.co.y for v in me.vertices]
    zs = [v.co.z for v in me.vertices]
    off = Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, min(zs)))
    for v in me.vertices:
        v.co -= off
    for a in list(me.color_attributes):
        me.color_attributes.remove(a)
    ca = me.color_attributes.new('Col', 'FLOAT_COLOR', 'CORNER')
    for p in me.polygons:
        c = face_cols[p.index]
        for li in p.loop_indices:
            ca.data[li].color = (c[0], c[1], c[2], 1.0)
    me.materials.clear()

    old = bpy.data.objects.get(new_name)
    if old is not None:
        bpy.data.objects.remove(old, do_unlink=True)
    ob = bpy.data.objects.new(new_name, me)
    ob['polypizza_attribution'] = attribution
    _baked_collection().objects.link(ob)
    return ob


def recolor(name, top, bottom, jitter=0.1, seed=7):
    """Overwrite a baked prop with a vertical gradient (hex colours, top/bottom)."""
    rnd = random.Random(seed)
    me = bpy.data.objects[name].data
    a = me.color_attributes['Col']
    zs = [v.co.z for v in me.vertices]
    z0, z1 = min(zs), max(zs)
    T, B = hexl(top), hexl(bottom)
    for p in me.polygons:
        t = (p.center.z - z0) / max(z1 - z0, 1e-6)
        j = 1 + rnd.uniform(-jitter, jitter)
        c = tuple(min(1, (B[k] + (T[k] - B[k]) * t) * j) for k in range(3))
        for li in p.loop_indices:
            a.data[li].color = (*c, 1)


def tint(name, mul):
    """Multiply a baked prop's colours by an RGB factor (linear)."""
    a = bpy.data.objects[name].data.color_attributes['Col']
    for d in a.data:
        c = d.color
        d.color = (c[0] * mul[0], c[1] * mul[1], c[2] * mul[2], 1)


def export(name):
    o = bpy.data.objects[name]
    bpy.ops.object.select_all(action='DESELECT')
    loc = o.location.copy()
    o.location = (0, 0, 0)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=p, use_selection=True, object_types={'MESH'},
                             apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                             bake_space_transform=True, mesh_smooth_type='FACE', colors_type='SRGB',
                             use_mesh_modifiers=True, add_leaf_bones=False, bake_anim=False)
    o.location = loc
    bpy.ops.object.select_all(action='DESELECT')
    return p


def export_all():
    return [export(o.name) for o in _baked_collection().objects if o.type == 'MESH']


def clear_imports():
    """Delete everything that is not in the Baked collection (cameras/lights kept)."""
    baked = set(_baked_collection().objects)
    for o in list(bpy.data.objects):
        if o not in baked and o.type not in ('CAMERA', 'LIGHT'):
            bpy.data.objects.remove(o, do_unlink=True)


def slope_color(name, flat_hex, steep_hex, low_hex, seed=3, jitter=0.12):
    """Terrain-style colouring: flat faces get flat_hex (grass/moss), steep faces
    steep_hex (rock), darkening toward low_hex near the base."""
    rnd = random.Random(seed)
    me = bpy.data.objects[name].data
    a = me.color_attributes['Col']
    z1 = max(v.co.z for v in me.vertices)
    F, S, L = hexl(flat_hex), hexl(steep_hex), hexl(low_hex)
    for p in me.polygons:
        t = min(1, max(0, (max(0.0, p.normal.z) - 0.35) / 0.45))
        base = tuple(S[k] + (F[k] - S[k]) * t for k in range(3))
        h = p.center.z / max(z1, 1e-6)
        base = tuple(L[k] + (base[k] - L[k]) * min(1, h * 1.8 + 0.2) for k in range(3))
        j = 1 + rnd.uniform(-jitter, jitter)
        c = tuple(min(1, x * j) for x in base)
        for li in p.loop_indices:
            a.data[li].color = (*c, 1)


def show_only(names, gap=3.0):
    """Hide every baked prop except `names`, lay those out in a row and frame them."""
    x = 0
    bpy.ops.object.select_all(action='DESELECT')
    for o in _baked_collection().objects:
        vis = o.name in names
        o.hide_set(not vis)
        if vis:
            o.location = (x + o.dimensions.x / 2, 0, 0)
            x += o.dimensions.x + gap
            o.select_set(True)
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            sp = area.spaces[0]
            sp.shading.type = 'SOLID'
            sp.shading.color_type = 'VERTEX'
            with bpy.context.temp_override(area=area, region=area.regions[-1]):
                bpy.ops.view3d.view_selected()
    bpy.ops.object.select_all(action='DESELECT')
