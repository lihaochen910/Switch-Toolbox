"""
glTF/GLB -> FBX 批量导出工具 (Blender 4.5)

用法：在 Blender 的 Scripting 工作区里打开这个脚本，点 Run Script。
弹出文件选择框，选一个 .gltf 或 .glb 文件，右侧参数面板里有个
"动画分开导出" 开关：

  - 不勾选（默认）：模型 + 骨架 + 所有动画合并导出成一个 fbx
                    （每个动画作为 fbx 里的一个 Take。注意：Blender
                    导出多个 Take 时固定会在名字前加 "骨架物体名|"
                    前缀，这是导出器本身的限制，没有公开选项可以关掉，
                    想要 Unity 里 Clip 名字干净就用下面的分开导出模式）
  - 勾选         ：先导出一个不带动画的模型 fbx，然后每个动画
                    单独导出一个 fbx（同样带模型+骨架，只烘焙这一个
                    动画），文件名和 fbx 内部的 Take 名都是干净的原始
                    动画（Action）名字，不带任何前缀或数字后缀

确定后会再弹一个文件夹选择框，选好保存位置后，脚本会在该位置下
创建一个和 gltf/glb 同名的子目录，把 fbx 和 glTF/GLB 用到的贴图
都导出到这个子目录里。

不会清空当前场景。只会导出这次 gltf/glb 实际导入进来的模型/骨架/
动画（glTF 导入时放进 "glTF_not_exported" collection 里的辅助物体
不会被导出到 fbx 里）。导出完成后，这次导入产生的所有数据都会从
场景里移除，不影响你原本场景里的其它内容。
"""

import bpy
import os
import re
import shutil
import sys
from bpy_extras.io_utils import ImportHelper

_state = {}

# Windows reserves these names (case-insensitively, regardless of extension)
# for device files - writing "nul.png" or "com1.fbx" fails with a WinError
# even though the identical name is a perfectly normal file on Linux/macOS.
_RESERVED_NAMES = {
    'CON', 'PRN', 'AUX', 'NUL',
    *(f'COM{i}' for i in range(1, 10)),
    *(f'LPT{i}' for i in range(1, 10)),
}

# Windows MAX_PATH (260 incl. null terminator) unless the app opts into long
# paths, which Blender's own file I/O doesn't reliably support - so warn
# instead of silently failing partway through a batch export.
_WIN_MAX_PATH = 259

# Data-block collections tracked before/after import so we know exactly what
# this run added, both to scope FBX export to "just this glTF" and to remove
# only that afterwards.
_TRACKED = ('objects', 'actions', 'images', 'meshes', 'armatures',
            'materials', 'node_groups', 'cameras', 'lights', 'collections')


def _snapshot():
    return {name: set(getattr(bpy.data, name)) for name in _TRACKED}


def _diff(before):
    after = _snapshot()
    return {name: list(after[name] - before[name]) for name in _TRACKED}


def _remove_added(added):
    # Dependency-safe order: objects first (unlinks them from collections and
    # drops their references to mesh/armature/action data), then the data
    # those objects used, then things materials/collections reference.
    for obj in added['objects']:
        bpy.data.objects.remove(obj, do_unlink=True)
    for name in ('meshes', 'armatures', 'cameras', 'lights', 'materials', 'node_groups', 'images', 'actions'):
        for block in added[name]:
            try:
                getattr(bpy.data, name).remove(block)
            except (RuntimeError, ReferenceError):
                pass
    for coll in added['collections']:
        try:
            bpy.data.collections.remove(coll)
        except (RuntimeError, ReferenceError):
            pass


def _sanitize(name):
    cleaned = re.sub(r'[<>:"/\\|?*]', "_", name).strip() or "unnamed"
    stem = cleaned.split('.', 1)[0].upper()
    if stem in _RESERVED_NAMES:
        cleaned = "_" + cleaned
    return cleaned


def _warn_path_length(path, warnings):
    if sys.platform == 'win32' and len(os.path.abspath(path)) > _WIN_MAX_PATH:
        msg = f"路径长度超过 Windows MAX_PATH 限制(260)，可能导致写入失败: {path}"
        print(f"[gltf2fbx] 警告: {msg}")
        warnings.append(msg)


def _clear_nla_overrides(armature):
    # A muted/solo'd NLA track forces the *whole* NLA stack to evaluate as that
    # one track during baking too, silently overwriting every other action's
    # keyframes with it. Always clear this before baking anything.
    ad = armature.animation_data
    if not ad:
        return
    for track in ad.nla_tracks:
        track.mute = False
        track.is_solo = False


def _select_only(objs):
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objs:
        obj.select_set(True)
    armature = next((o for o in objs if o.type == 'ARMATURE'), None)
    if objs:
        bpy.context.view_layer.objects.active = armature or objs[0]


def _export_fbx(path, objs, bake_anim, use_all_actions, use_nla_strips, take_name=None):
    _select_only(objs)

    # When baking just the current/active action (not all actions / NLA
    # strips), Blender names the resulting FBX Take after the *scene*, not
    # the action - renaming the scene here is the only way to get a plain,
    # unprefixed Take name out of the stock exporter. (The "all actions"
    # bake path ignores this and always names each Take
    # "{ArmatureObjectName}|{ActionName}" instead - not overridable via any
    # public export option.)
    scene = bpy.context.scene
    orig_scene_name = scene.name
    if take_name and not use_all_actions and not use_nla_strips:
        scene.name = _sanitize(take_name)

    try:
        bpy.ops.export_scene.fbx(
            filepath=path,
            use_selection=True,
            bake_anim=bake_anim,
            bake_anim_use_all_bones=True,
            bake_anim_use_all_actions=use_all_actions,
            bake_anim_use_nla_strips=use_nla_strips,
            bake_anim_force_startend_keying=True,
            add_leaf_bones=False,
            # Default 'AUTO' falls back to an absolute filesystem path
            # whenever the .blend hasn't been saved (this script never saves
            # one), which breaks texture lookup on any other machine/OS.
            # 'STRIP' keeps just the filename, matching what _save_textures
            # copies next to the fbx.
            path_mode='STRIP',
        )
    finally:
        scene.name = orig_scene_name


_IMAGE_EXTS = ('.png', '.jpg', '.jpeg', '.tga', '.bmp', '.tiff', '.dds')


def _dedupe_image_names(images):
    # export_scene.fbx(path_mode='STRIP') embeds a packed image's texture
    # reference using its (extension-less) name. If two images collide only
    # by case ("Tex" vs "tex"), renaming the datablocks here - before export
    # - keeps that embedded reference in sync with what _save_textures later
    # writes to disk, instead of one file silently overwriting the other on
    # a case-insensitive filesystem (Windows/macOS default; not Linux).
    seen = set()
    for img in images:
        if img.name in ("Render Result", "Viewer Node") or img.source == 'GENERATED':
            continue
        base, ext = os.path.splitext(_sanitize(img.name))
        candidate = base
        n = 1
        while candidate.lower() in seen:
            candidate = f"{base}_{n}"
            n += 1
        seen.add(candidate.lower())
        if candidate + ext != img.name:
            img.name = candidate + ext


def _save_textures(images, output_dir, warnings):
    saved = []
    used_names = set()  # case-folded destination filenames already written this run

    def _unique_dest(base, ext):
        candidate = base
        n = 1
        while (candidate + ext).lower() in used_names:
            candidate = f"{base}_{n}"
            n += 1
        used_names.add((candidate + ext).lower())
        dest = os.path.join(output_dir, candidate + ext)
        _warn_path_length(dest, warnings)
        return dest

    for img in images:
        if img.name in ("Render Result", "Viewer Node") or img.source == 'GENERATED':
            continue
        try:
            if img.packed_file:
                fmt = img.file_format or 'PNG'
                ext = ".jpg" if fmt == 'JPEG' else ".png"
                # img.name may have a Blender dedup suffix like "Emm.001" - only
                # strip a *real* image extension, otherwise ".001" gets mistaken
                # for one and the original name is lost.
                name = _sanitize(img.name)
                base, cur_ext = os.path.splitext(name)
                if cur_ext.lower() not in _IMAGE_EXTS:
                    base = name
                dest = _unique_dest(base, ext)

                orig_filepath, orig_fmt = img.filepath_raw, img.file_format
                img.filepath_raw = dest
                img.file_format = fmt
                img.save()
                img.filepath_raw, img.file_format = orig_filepath, orig_fmt
                saved.append(dest)
            else:
                src = bpy.path.abspath(img.filepath)
                if os.path.isfile(src):
                    base, ext = os.path.splitext(os.path.basename(src))
                    dest = _unique_dest(_sanitize(base), ext)
                    shutil.copy2(src, dest)
                    saved.append(dest)
        except Exception as e:
            print(f"[gltf2fbx] 贴图导出失败 '{img.name}': {e}")
    return saved


class GLTF2FBX_OT_import(bpy.types.Operator, ImportHelper):
    bl_idname = "gltf2fbx.import_source"
    bl_label = "选择 glTF/GLB 文件"
    bl_options = {'REGISTER'}

    filename_ext = ".gltf"
    filter_glob: bpy.props.StringProperty(default="*.gltf;*.glb", options={'HIDDEN'})

    split_animations: bpy.props.BoolProperty(
        name="动画分开导出",
        description="勾选：模型和每个动画各导出一个fbx文件。不勾选：所有动画合并进模型fbx里（多个Take）",
        default=False,
    )

    def draw(self, context):
        self.layout.prop(self, "split_animations")

    def execute(self, context):
        before = _snapshot()
        bpy.ops.import_scene.gltf(filepath=self.filepath)
        added = _diff(before)

        new_objs = added['objects']
        if not new_objs:
            self.report({'ERROR'}, "导入失败，没有生成任何物体")
            return {'CANCELLED'}

        for arm in (o for o in new_objs if o.type == 'ARMATURE'):
            _clear_nla_overrides(arm)

        # glTF's own "glTF_not_exported" collection holds helper objects
        # (light/camera proxies etc.) the importer creates that aren't part
        # of the actual model - keep them out of the fbx.
        not_exported = bpy.data.collections.get("glTF_not_exported")

        def _excluded(obj):
            return not_exported is not None and not_exported in obj.users_collection

        _state.clear()
        _state.update(
            source_path=self.filepath,
            split_animations=self.split_animations,
            added=added,
            objects=[o for o in new_objs if o.type in {'ARMATURE', 'MESH'} and not _excluded(o)],
            armatures=[o for o in new_objs if o.type == 'ARMATURE' and not _excluded(o)],
            actions=added['actions'],
            images=added['images'],
        )

        bpy.ops.gltf2fbx.choose_output('INVOKE_DEFAULT')
        return {'FINISHED'}


class GLTF2FBX_OT_choose_output(bpy.types.Operator):
    bl_idname = "gltf2fbx.choose_output"
    bl_label = "选择保存位置"
    bl_options = {'REGISTER'}

    # Only a `directory` prop (no filepath/filename) makes the file browser
    # open in folder-select mode.
    directory: bpy.props.StringProperty(subtype='DIR_PATH')

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        if not _state:
            self.report({'ERROR'}, "没有找到已导入的数据，请重新运行脚本")
            return {'CANCELLED'}

        source_path = _state['source_path']
        split_animations = _state['split_animations']
        export_objs = _state['objects']
        armatures = _state['armatures']
        actions = _state['actions']
        images = _state['images']
        added = _state['added']

        base_name = _sanitize(os.path.splitext(os.path.basename(source_path))[0])
        output_dir = os.path.join(self.directory, base_name)
        os.makedirs(output_dir, exist_ok=True)

        # Must run before any _export_fbx call below - it renames the actual
        # image datablocks so the texture reference embedded in the FBX
        # agrees with the filename _save_textures writes to disk.
        _dedupe_image_names(images)

        warnings = []

        if split_animations and actions:
            base_path = os.path.join(output_dir, base_name + ".fbx")
            _warn_path_length(base_path, warnings)
            _export_fbx(base_path, export_objs, bake_anim=False, use_all_actions=False, use_nla_strips=False)

            for action in actions:
                for arm in armatures:
                    if arm.animation_data is None:
                        arm.animation_data_create()
                    arm.animation_data.action = action
                anim_path = os.path.join(output_dir, _sanitize(action.name) + ".fbx")
                _warn_path_length(anim_path, warnings)
                _export_fbx(anim_path, export_objs, bake_anim=True, use_all_actions=False,
                            use_nla_strips=False, take_name=action.name)
        else:
            base_path = os.path.join(output_dir, base_name + ".fbx")
            _warn_path_length(base_path, warnings)
            _export_fbx(base_path, export_objs,
                        bake_anim=bool(actions), use_all_actions=True, use_nla_strips=True)

        saved_textures = _save_textures(images, output_dir, warnings)

        # Remove everything this glTF/GLB import added, leaving the rest of
        # the scene exactly as it was before the script ran.
        _remove_added(added)

        msg = f"导出完成: {output_dir}  (贴图 {len(saved_textures)} 张)"
        if warnings:
            msg += f"  [{len(warnings)} 个路径过长警告，见控制台]"
            self.report({'WARNING'}, msg)
        else:
            self.report({'INFO'}, msg)
        print(f"[gltf2fbx] {msg}")

        _state.clear()
        return {'FINISHED'}


classes = (GLTF2FBX_OT_import, GLTF2FBX_OT_choose_output)


def register():
    for cls in classes:
        try:
            bpy.utils.register_class(cls)
        except ValueError:
            bpy.utils.unregister_class(cls)
            bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
    bpy.ops.gltf2fbx.import_source('INVOKE_DEFAULT')
