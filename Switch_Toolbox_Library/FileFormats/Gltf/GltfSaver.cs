using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Toolbox.Library.Forms;
using Toolbox.Library.IO;
using SN = System.Numerics;

namespace Toolbox.Library
{
    using MeshBuilderType = MeshBuilder<VertexPositionNormal, VertexColor1Texture3, VertexJoints4>;
    using VertexBuilderType = VertexBuilder<VertexPositionNormal, VertexColor1Texture3, VertexJoints4>;

    //Exports models, skeletons and skeletal animations to glTF 2.0 (.gltf/.glb) using SharpGLTF.
    //Every animation passed in is baked into the same output scene as the model/skeleton, so a
    //model with multiple matching animations ends up as a single glTF file (mirrors AssimpSaver's
    //FBX merge behavior, but implemented natively instead of going through the Assimp gltf2 exporter).
    public class GltfSaver
    {
        public static bool SuppressDialogs = false;

        const float FramesPerSecond = 30f;

        STProgressBar progressBar;

        public void SaveFromModel(STGenericModel model, string FileName, List<STGenericTexture> Textures, STSkeleton skeleton = null, List<int> NodeArray = null, List<Animations.Animation> Animations = null)
        {
            SaveFromModel(model.Objects.ToList(), model.Materials.ToList(), FileName, Textures, skeleton, NodeArray, Animations);
        }

        public void SaveFromModel(List<STGenericObject> Meshes, List<STGenericMaterial> Materials, string FileName, List<STGenericTexture> Textures, STSkeleton skeleton = null, List<int> NodeArray = null, List<Animations.Animation> Animations = null)
        {
            try
            {
                if (!SuppressDialogs)
                {
                    progressBar = new STProgressBar();
                    progressBar.Task = "Exporting Skeleton...";
                    progressBar.Value = 0;
                    progressBar.StartPosition = FormStartPosition.CenterScreen;
                    progressBar.Show();
                    progressBar.Refresh();
                }

                var sceneBuilder = new SceneBuilder();

                NodeBuilder[] jointNodes = null;
                (NodeBuilder Node, SN.Matrix4x4 InverseBind)[] jointBindings = null;
                Dictionary<string, NodeBuilder> boneNameToNode = null;

                bool hasSkeleton = skeleton != null && skeleton.bones.Count > 0;
                if (hasSkeleton)
                    BuildSkeletonNodes(skeleton, sceneBuilder, out jointNodes, out jointBindings, out boneNameToNode);

                if (progressBar != null)
                {
                    progressBar.Task = "Exporting Materials...";
                    progressBar.Value = 25;
                }

                var materialBuilders = BuildMaterials(Materials, Textures);

                if (progressBar != null)
                {
                    progressBar.Task = "Exporting Meshes...";
                    progressBar.Value = 50;
                }

                foreach (var obj in Meshes)
                {
                    var mesh = BuildMesh(obj, materialBuilders, skeleton, NodeArray);
                    if (mesh == null)
                        continue;

                    if (hasSkeleton)
                        sceneBuilder.AddSkinnedMesh(mesh, jointBindings);
                    else
                        sceneBuilder.AddRigidMesh(mesh, SN.Matrix4x4.Identity);
                }

                if (Animations != null && hasSkeleton)
                {
                    if (progressBar != null)
                    {
                        progressBar.Task = "Exporting Animations...";
                        progressBar.Value = 80;
                    }

                    foreach (var anim in Animations)
                        BakeAnimation(anim, boneNameToNode);
                }

                if (progressBar != null)
                {
                    progressBar.Task = "Saving File...";
                    progressBar.Value = 90;
                }

                var gltfModel = sceneBuilder.ToGltf2();
                gltfModel.Save(FileName);

                if (!SuppressDialogs)
                    MessageBox.Show($"Exported {FileName} Successfuly!");
                else
                    Console.WriteLine($"Exported {FileName} Successfuly!");
            }
            catch (Exception ex)
            {
                if (!SuppressDialogs)
                    MessageBox.Show($"Failed to export {FileName}!\n{ex.Message}");
                else
                    Console.Error.WriteLine($"Failed to export {FileName}! {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (progressBar != null)
                {
                    progressBar.Value = 100;
                    progressBar.Close();
                    progressBar.Dispose();
                    progressBar = null;
                }
            }
        }

        public void SaveAnimation(Animations.Animation anim, STSkeleton skeleton, string FileName)
        {
            try
            {
                var sceneBuilder = new SceneBuilder();

                BuildSkeletonNodes(skeleton, sceneBuilder, out _, out _, out var boneNameToNode);
                BakeAnimation(anim, boneNameToNode);

                var gltfModel = sceneBuilder.ToGltf2();
                gltfModel.Save(FileName);

                if (!SuppressDialogs)
                    MessageBox.Show($"Exported {FileName} Successfuly!");
                else
                    Console.WriteLine($"Exported {FileName} Successfuly!");
            }
            catch (Exception ex)
            {
                if (!SuppressDialogs)
                    MessageBox.Show($"Failed to export {FileName}!\n{ex.Message}");
                else
                    Console.Error.WriteLine($"Failed to export {FileName}! {ex.Message}\n{ex.StackTrace}");
            }
        }

        //Creates one NodeBuilder per skeleton bone (indices line up with skeleton.bones so vertex
        //joint indices can reference them directly), parents them to mirror the skeleton hierarchy,
        //and returns both an index-aligned array (for skin binding) and a name lookup (for animation baking).
        private void BuildSkeletonNodes(STSkeleton skeleton, SceneBuilder sceneBuilder,
            out NodeBuilder[] jointNodes, out (NodeBuilder Node, SN.Matrix4x4 InverseBind)[] jointBindings, out Dictionary<string, NodeBuilder> boneNameToNode)
        {
            jointNodes = new NodeBuilder[skeleton.bones.Count];
            boneNameToNode = new Dictionary<string, NodeBuilder>();

            for (int i = 0; i < skeleton.bones.Count; i++)
            {
                var bone = skeleton.bones[i];
                var node = new NodeBuilder(bone.Text);
                node.LocalMatrix = ToNumerics(bone.Transform);
                jointNodes[i] = node;

                if (!boneNameToNode.ContainsKey(bone.Text))
                    boneNameToNode.Add(bone.Text, node);
            }

            for (int i = 0; i < skeleton.bones.Count; i++)
            {
                var bone = skeleton.bones[i];
                if (bone.parentIndex >= 0)
                    jointNodes[bone.parentIndex].AddNode(jointNodes[i]);
                else
                    sceneBuilder.AddNode(jointNodes[i]);
            }

            jointBindings = new (NodeBuilder, SN.Matrix4x4)[skeleton.bones.Count];
            for (int i = 0; i < skeleton.bones.Count; i++)
            {
                var inverse = MatrixExenstion.CalculateInverseMatrix(skeleton.bones[i]).inverse;
                jointBindings[i] = (jointNodes[i], inverse);
            }
        }

        private List<MaterialBuilder> BuildMaterials(List<STGenericMaterial> Materials, List<STGenericTexture> Textures)
        {
            var result = new List<MaterialBuilder>();

            if (Materials == null || Materials.Count == 0)
            {
                result.Add(new MaterialBuilder("Material").WithMetallicRoughness(0f, 1f));
                return result;
            }

            foreach (var mat in Materials)
            {
                var genericMat = (STGenericMaterial)mat;
                var builder = new MaterialBuilder(genericMat.Text).WithMetallicRoughness(0f, 1f);

                var diffuse = GetTextureImage(genericMat, Textures, STGenericMatTexture.TextureType.Diffuse);
                if (diffuse != null)
                    builder = builder.WithBaseColor(diffuse, null);

                var normal = GetTextureImage(genericMat, Textures, STGenericMatTexture.TextureType.Normal);
                if (normal != null)
                    builder = builder.WithNormal(normal, 1f);

                var emission = GetTextureImage(genericMat, Textures, STGenericMatTexture.TextureType.Emission);
                if (emission != null)
                    builder = builder.WithEmissive(emission, null, 1f);

                result.Add(builder);
            }

            return result;
        }

        //Wraps the exported PNG bytes in an ImageBuilder named after the original bfres texture,
        //and pins AlternateWriteFileName so SharpGLTF writes it as "<textureName>.png" instead of
        //the default "<modelName>_<index>.png" naming scheme.
        private ImageBuilder GetTextureImage(STGenericMaterial genericMat, List<STGenericTexture> Textures, STGenericMatTexture.TextureType type)
        {
            var texMap = genericMat.TextureMaps.FirstOrDefault(t => t.Type == type);
            if (texMap == null)
                return null;

            var texture = Textures.FirstOrDefault(t => t.Text.Equals(texMap.Name));
            if (texture == null)
                return null;

            byte[] pngData;
            using (var bitmap = texture.GetBitmap())
            {
                if (bitmap == null)
                    return null;

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngData = ms.ToArray();
                }
            }

            var image = ImageBuilder.From(pngData);
            image.Name = texture.Text;
            image.AlternateWriteFileName = texture.Text + ".*";
            return image;
        }

        private MeshBuilderType BuildMesh(STGenericObject genericObj, List<MaterialBuilder> materialBuilders, STSkeleton skeleton, List<int> NodeArray)
        {
            if (genericObj.vertices == null || genericObj.vertices.Count == 0)
                return null;

            var faces = GetFaces(genericObj);
            if (faces.Count < 3)
                return null;

            var material = (genericObj.MaterialIndex >= 0 && genericObj.MaterialIndex < materialBuilders.Count)
                ? materialBuilders[genericObj.MaterialIndex] : materialBuilders[0];

            var mesh = new MeshBuilderType(genericObj.Text);
            var prim = mesh.UsePrimitive(material, 3);

            bool hasSkeleton = skeleton != null && skeleton.bones.Count > 0;

            var builtVertices = new VertexBuilderType[genericObj.vertices.Count];
            for (int i = 0; i < genericObj.vertices.Count; i++)
            {
                var v = genericObj.vertices[i];

                var geo = new VertexPositionNormal(new SN.Vector3(v.pos.X, v.pos.Y, v.pos.Z), new SN.Vector3(v.nrm.X, v.nrm.Y, v.nrm.Z));
                var mat = new VertexColor1Texture3(new SN.Vector4(v.col.X, v.col.Y, v.col.Z, v.col.W),
                    new SN.Vector2(v.uv0.X, v.uv0.Y), new SN.Vector2(v.uv1.X, v.uv1.Y), new SN.Vector2(v.uv2.X, v.uv2.Y));

                VertexJoints4 skin;
                if (hasSkeleton && genericObj.VertexSkinCount > 0 && v.boneIds.Count > 0)
                {
                    var bindings = new List<(int, float)>();
                    int influenceCount = Math.Min(v.boneIds.Count, genericObj.VertexSkinCount);
                    for (int j = 0; j < influenceCount; j++)
                    {
                        int boneIndex = NodeArray != null ? NodeArray[v.boneIds[j]] : v.boneIds[j];
                        float weight = v.boneWeights.Count > j ? v.boneWeights[j] : (v.boneWeights.Count == 0 ? 1f : 0f);
                        if (weight > 0)
                            bindings.Add((boneIndex, weight));
                    }
                    if (bindings.Count == 0)
                        bindings.Add((0, 1f));

                    skin = new VertexJoints4(bindings.ToArray());
                }
                else if (hasSkeleton)
                    skin = new VertexJoints4(0);
                else
                    skin = new VertexJoints4(0);

                builtVertices[i] = new VertexBuilderType(geo, mat, skin);
            }

            for (int f = 0; f + 2 < faces.Count; f += 3)
            {
                int a = faces[f];
                int b = faces[f + 1];
                int c = faces[f + 2];
                if (a >= builtVertices.Length || b >= builtVertices.Length || c >= builtVertices.Length)
                    continue;

                prim.AddTriangle(builtVertices[a], builtVertices[b], builtVertices[c]);
            }

            return mesh;
        }

        private List<int> GetFaces(STGenericObject genericObj)
        {
            var faces = new List<int>();

            if (genericObj.PolygonGroups.Count != 0)
            {
                foreach (var group in genericObj.PolygonGroups)
                    faces.AddRange(group.faces);
            }
            else if (genericObj.lodMeshes.Count != 0)
                faces.AddRange(genericObj.lodMeshes[genericObj.DisplayLODIndex].faces);

            return faces;
        }

        //Bakes every frame of the animation into translation/rotation/scale keyframe dictionaries
        //on the matching skeleton NodeBuilder, under an animation track named after the animation.
        //Channels for bones the target skeleton doesn't have are skipped (mirrors AssimpSaver).
        private void BakeAnimation(Animations.Animation anim, Dictionary<string, NodeBuilder> boneNameToNode)
        {
            string trackName = anim.Text;

            foreach (var boneNode in anim.Bones)
            {
                if (!boneNameToNode.TryGetValue(boneNode.Text, out var node))
                {
                    Console.WriteLine($"Skipping animation channel for bone '{boneNode.Text}' (not present in the target skeleton).");
                    continue;
                }

                var posKeys = new Dictionary<float, SN.Vector3>();
                var rotKeys = new Dictionary<float, SN.Quaternion>();
                var scaleKeys = new Dictionary<float, SN.Vector3>();

                for (int frame = 0; frame <= anim.FrameCount; frame++)
                {
                    float time = frame / FramesPerSecond;

                    var pos = boneNode.GetPosition(frame);
                    posKeys[time] = ToNumerics(pos);

                    OpenTK.Quaternion rot;
                    if (boneNode.RotType == Animations.Animation.RotationType.EULER)
                    {
                        float rx = boneNode.XROT.HasAnimation() ? boneNode.XROT.GetValue(frame) : 0;
                        float ry = boneNode.YROT.HasAnimation() ? boneNode.YROT.GetValue(frame) : 0;
                        float rz = boneNode.ZROT.HasAnimation() ? boneNode.ZROT.GetValue(frame) : 0;
                        rot = STMath.FromEulerAngles(new OpenTK.Vector3(rx, ry, rz));
                    }
                    else
                        rot = boneNode.GetRotation(frame);

                    if (rot.Length > 0.0001f)
                        rot.Normalize();
                    else
                        rot = OpenTK.Quaternion.Identity;
                    rotKeys[time] = ToNumerics(rot);

                    var scale = boneNode.GetScale(frame);
                    scaleKeys[time] = ToNumerics(scale);
                }

                node.WithLocalTranslation(trackName, posKeys);
                node.WithLocalRotation(trackName, rotKeys);
                node.WithLocalScale(trackName, scaleKeys);
            }
        }

        private static SN.Vector3 ToNumerics(OpenTK.Vector3 v) => new SN.Vector3(v.X, v.Y, v.Z);

        private static SN.Quaternion ToNumerics(OpenTK.Quaternion q) => new SN.Quaternion(q.X, q.Y, q.Z, q.W);

        private static SN.Matrix4x4 ToNumerics(OpenTK.Matrix4 m) => new SN.Matrix4x4(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);
    }
}
