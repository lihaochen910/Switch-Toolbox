using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Toolbox.Library;
using Toolbox.Library.Animations;
using FirstPlugin;
using Bfres.Structs;

namespace ToolboxCLI
{
    class Program
    {
        [DllImport("kernel32.dll")]
        private static extern void ExitProcess(uint uExitCode);

        // Flush console buffers, then terminate the process immediately, skipping
        // CLR/native finalization. AssimpNet's native library can raise an access
        // violation (0xC0000005) during process teardown AFTER a successful export;
        // forcing the exit here guarantees a clean exit code and avoids that crash.
        private static void ForceExit(int code)
        {
            try { Console.Out.Flush(); } catch { }
            try { Console.Error.Flush(); } catch { }
            ExitProcess((uint)code);
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Auto-flush so progress is never lost if a native teardown crash occurs.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            Runtime.ExecutableDir = AppDomain.CurrentDomain.BaseDirectory;

            // Parse args: <input_file> [output_directory] [--format fbx|dae] [--split-anims]
            string format = "fbx";
            bool splitAnims = false;
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("--format", StringComparison.OrdinalIgnoreCase) || a.Equals("-f", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                }
                else if (a.StartsWith("--format=", StringComparison.OrdinalIgnoreCase))
                {
                    format = a.Substring("--format=".Length).ToLowerInvariant();
                }
                else if (a.Equals("--split-anims", StringComparison.OrdinalIgnoreCase) || a.Equals("--separate-anims", StringComparison.OrdinalIgnoreCase))
                {
                    splitAnims = true;
                }
                else
                {
                    positional.Add(a);
                }
            }

            if (positional.Count < 1)
            {
                Console.WriteLine("Usage: ToolboxCLI <input_file.bfres|.bfres.zs> [output_directory] [--format fbx|dae|gltf|glb] [--split-anims]");
                Console.WriteLine("  --format fbx (default): models+skeletons and skeletal animations -> .fbx");
                Console.WriteLine("  --format dae          : models+skeletons -> .dae, skeletal animations -> Maya .anim");
                Console.WriteLine("  --format gltf         : models+skeletons and skeletal animations -> .gltf");
                Console.WriteLine("  --format glb          : models+skeletons and skeletal animations -> .glb (binary glTF)");
                Console.WriteLine("  --split-anims         : (fbx/gltf/glb only) export the model and every skeletal animation");
                Console.WriteLine("                          as separate files instead of merging matching animations into the model file.");
                return;
            }

            if (format != "fbx" && format != "dae" && format != "gltf" && format != "glb")
            {
                Console.Error.WriteLine($"Error: unknown --format '{format}'. Use 'fbx', 'dae', 'gltf' or 'glb'.");
                Environment.Exit(1);
            }

            string inputPath = Path.GetFullPath(positional[0]);
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Error: Input file does not exist: {inputPath}");
                Environment.Exit(1);
            }

            string outputDir;
            if (positional.Count >= 2)
            {
                outputDir = Path.GetFullPath(positional[1]);
            }
            else
            {
                string nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
                if (nameWithoutExt.EndsWith(".bfres", StringComparison.OrdinalIgnoreCase))
                {
                    nameWithoutExt = Path.GetFileNameWithoutExtension(nameWithoutExt);
                }
                outputDir = Path.Combine(Path.GetDirectoryName(inputPath), nameWithoutExt);
            }

            Console.WriteLine($"Input File: {inputPath}");
            Console.WriteLine($"Output Directory: {outputDir}");
            Console.WriteLine($"Format: {format}");

            try
            {
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // Initialize PluginRuntime caches
                PluginRuntime.bntxContainers.Clear();
                PluginRuntime.ftexContainers.Clear();

                byte[] fileData = File.ReadAllBytes(inputPath);

                // Decompress if ZS file
                bool isCompressed = inputPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) ||
                                    (fileData.Length >= 4 && (BitConverter.ToUInt32(fileData, 0) == 0x28B52FFD || BitConverter.ToUInt32(fileData, 0) == 0xFD2FB528));
                if (isCompressed)
                {
                    Console.WriteLine("Decompressing ZS compressed file using ZSTD...");
                    fileData = Zstb.SDecompress(fileData);
                }

                // Load BFRES
                BFRES bfres = new BFRES();
                bfres.FileName = Path.GetFileName(inputPath);
                if (bfres.FileName.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
                {
                    bfres.FileName = Path.GetFileNameWithoutExtension(bfres.FileName);
                }
                bfres.FilePath = inputPath;
                bfres.IFileInfo = new Toolbox.Library.IFileInfo();

                Console.WriteLine("Loading BFRES data...");
                using (var ms = new MemoryStream(fileData))
                {
                    bfres.Load(ms);
                }

                // Extract all models to DAE
                var modelsFolder = bfres.Nodes.OfType<BFRESGroupNode>().FirstOrDefault(x => x.Type == BRESGroupType.Models);
                List<FMDL> modelsList = new List<FMDL>();
                if (modelsFolder != null)
                {
                    foreach (TreeNode node in modelsFolder.Nodes)
                    {
                        if (node is FMDL fmdl)
                        {
                            modelsList.Add(fmdl);
                        }
                    }
                }

                Console.WriteLine($"Found {modelsList.Count} model(s).");

                // Run headless: skip the success/progress dialogs in AssimpSaver/GltfSaver.
                AssimpSaver.SuppressDialogs = true;
                GltfSaver.SuppressDialogs = true;

                // Collect all skeletal animations up front.
                List<FSKA> allAnims = new List<FSKA>();
                var animFolderPre = bfres.Nodes.OfType<BFRESAnimFolder>().FirstOrDefault();
                if (animFolderPre != null)
                {
                    var grp = animFolderPre.Nodes.OfType<BFRESGroupNode>().FirstOrDefault(x => x.Type == BRESGroupType.SkeletalAnim);
                    if (grp != null)
                        foreach (TreeNode n in grp.Nodes)
                            if (n is FSKA f) allAnims.Add(f);
                }

                // For FBX/glTF, merge each animation into the first model whose skeleton fully
                // contains all of the animation's bones, so model + skeleton + its
                // animations end up in a single .fbx/.gltf file. Animations that match no model
                // are exported as standalone files below. Skipped entirely when --split-anims is
                // passed, so every animation is left to export as its own standalone file.
                Dictionary<FMDL, List<FSKA>> modelAnims = new Dictionary<FMDL, List<FSKA>>();
                HashSet<FSKA> mergedAnims = new HashSet<FSKA>();
                if (!splitAnims && (format == "fbx" || format == "gltf" || format == "glb"))
                {
                    foreach (var fska in allAnims)
                    {
                        foreach (var model in modelsList)
                        {
                            if (model.Skeleton == null || model.Skeleton.bones.Count == 0)
                                continue;

                            bool allPresent = true;
                            foreach (var bone in fska.Bones)
                            {
                                if (model.Skeleton.GetBone(bone.Text) == null) { allPresent = false; break; }
                            }
                            if (allPresent)
                            {
                                if (!modelAnims.ContainsKey(model))
                                    modelAnims[model] = new List<FSKA>();
                                modelAnims[model].Add(fska);
                                mergedAnims.Add(fska);
                                break;
                            }
                        }
                    }
                }

                foreach (var fmdl in modelsList)
                {
                    // Make sure the underlying bfres data is populated for export.
                    if (fmdl.ModelU != null)
                        BfresWiiU.SetModel(fmdl);
                    else
                        BfresSwitch.SetModel(fmdl);

                    STSkeleton skeleton = fmdl.Skeleton;
                    List<int> nodeArray = null;
                    if (skeleton is FSKL fskl)
                    {
                        nodeArray = fskl.Node_Array?.ToList();
                    }
                    else if (skeleton?.BoneIndices != null)
                    {
                        nodeArray = skeleton.BoneIndices.ToList();
                    }

                    if (format == "fbx")
                    {
                        string fbxPath = Path.Combine(outputDir, $"{fmdl.Text}.fbx");

                        List<Animation> mergeAnims = null;
                        if (modelAnims.TryGetValue(fmdl, out var matchList) && matchList.Count > 0)
                        {
                            mergeAnims = matchList.Cast<Animation>().ToList();
                            Console.WriteLine($"Exporting model (+skeleton +{matchList.Count} animation(s)) to FBX: {fmdl.Text} -> {fbxPath}");
                            Console.WriteLine($"  Merged animations: {string.Join(", ", matchList.Select(a => a.Text))}");
                        }
                        else
                        {
                            Console.WriteLine($"Exporting model (+skeleton) to FBX: {fmdl.Text} -> {fbxPath}");
                        }

                        AssimpSaver saver = new AssimpSaver();
                        saver.SaveFromModel(fmdl, fbxPath, bfres.GetTextures(), skeleton, nodeArray, mergeAnims);
                    }
                    else if (format == "gltf" || format == "glb")
                    {
                        string gltfPath = Path.Combine(outputDir, $"{fmdl.Text}.{format}");

                        List<Animation> mergeAnims = null;
                        if (modelAnims.TryGetValue(fmdl, out var matchList) && matchList.Count > 0)
                        {
                            mergeAnims = matchList.Cast<Animation>().ToList();
                            Console.WriteLine($"Exporting model (+skeleton +{matchList.Count} animation(s)) to {format}: {fmdl.Text} -> {gltfPath}");
                            Console.WriteLine($"  Merged animations: {string.Join(", ", matchList.Select(a => a.Text))}");
                        }
                        else
                        {
                            Console.WriteLine($"Exporting model (+skeleton) to {format}: {fmdl.Text} -> {gltfPath}");
                        }

                        GltfSaver gltfSaver = new GltfSaver();
                        gltfSaver.SaveFromModel(fmdl, gltfPath, bfres.GetTextures(), skeleton, nodeArray, mergeAnims);
                    }
                    else // dae
                    {
                        string daePath = Path.Combine(outputDir, $"{fmdl.Text}.dae");
                        Console.WriteLine($"Exporting model (+skeleton) to DAE: {fmdl.Text} -> {daePath}");

                        // Toggle vertex colors based on whether the model actually has a color attribute.
                        bool useVertexColors = false;
                        try
                        {
                            if (fmdl.Model != null)
                            {
                                useVertexColors = fmdl.Model.VertexBuffers.Any(
                                    x => x.Attributes.Any(a => a.Name.StartsWith("_c")));
                            }
                            else if (fmdl.ModelU != null)
                            {
                                foreach (var vb in fmdl.ModelU.VertexBuffers)
                                {
                                    foreach (var attr in vb.Attributes.Values)
                                    {
                                        if (attr.Name.StartsWith("_c")) { useVertexColors = true; break; }
                                    }
                                    if (useVertexColors) break;
                                }
                            }
                        }
                        catch
                        {
                            useVertexColors = true;
                        }

                        DAE.ExportSettings daeSettings = new DAE.ExportSettings();
                        daeSettings.ExportTextures = false; // textures are exported separately as PNG
                        daeSettings.ImageExtension = ".png";
                        daeSettings.UseVertexColors = useVertexColors;

                        DAE.Export(daePath, daeSettings, fmdl, bfres.GetTextures(), skeleton, nodeArray);
                    }
                }

                // Extract all textures to PNG
                var textures = bfres.GetTextures();
                Console.WriteLine($"Found {textures.Count} texture(s).");
                foreach (var texture in textures)
                {
                    string textureName = texture.Text;
                    try
                    {
                        if (texture.ArrayCount > 1)
                        {
                            Console.WriteLine($"Exporting texture array/cubemap: {textureName} ({texture.ArrayCount} slices) as PNG...");
                            for (int i = 0; i < texture.ArrayCount; i++)
                            {
                                using (Bitmap arrayBitMap = texture.GetBitmap(i, 0))
                                {
                                    if (arrayBitMap != null)
                                    {
                                        string slicePath = Path.Combine(outputDir, $"{textureName}_Slice_{i}.png");
                                        arrayBitMap.Save(slicePath, ImageFormat.Png);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Exporting texture: {textureName} as PNG...");
                            using (Bitmap bitmap = texture.GetBitmap(0, 0))
                            {
                                if (bitmap != null)
                                {
                                    string texturePath = Path.Combine(outputDir, $"{textureName}.png");
                                    bitmap.Save(texturePath, ImageFormat.Png);
                                }
                            }
                        }
                    }
                    catch (Exception texEx)
                    {
                        Console.Error.WriteLine($"Error exporting texture {textureName}: {texEx.Message}");
                    }
                }

                // Extract all skeletal animations to Maya .anim
                int animCount = 0;
                var animFolder = bfres.Nodes.OfType<BFRESAnimFolder>().FirstOrDefault();
                if (animFolder != null)
                {
                    var skeletalAnimGroup = animFolder.Nodes.OfType<BFRESGroupNode>().FirstOrDefault(x => x.Type == BRESGroupType.SkeletalAnim);
                    if (skeletalAnimGroup != null)
                    {
                        foreach (TreeNode node in skeletalAnimGroup.Nodes)
                        {
                            if (node is FSKA fska)
                            {
                                animCount++;

                                // For FBX/glTF, animations merged into a model file are not
                                // re-exported as standalone files.
                                if ((format == "fbx" || format == "gltf" || format == "glb") && mergedAnims.Contains(fska))
                                {
                                    Console.WriteLine($"Skipping standalone export of '{fska.Text}' (merged into a model {format}).");
                                    continue;
                                }

                                STSkeleton skeleton = null;

                                // Prefer a model skeleton that contains EVERY bone the
                                // animation targets (keeps the real hierarchy/rest pose).
                                foreach (var model in modelsList)
                                {
                                    if (model.Skeleton == null || model.Skeleton.bones.Count == 0)
                                        continue;

                                    bool areAllBonesPresent = true;
                                    foreach (var bone in fska.Bones)
                                    {
                                        if (model.Skeleton.GetBone(bone.Text) == null)
                                        {
                                            areAllBonesPresent = false;
                                            break;
                                        }
                                    }
                                    if (areAllBonesPresent)
                                    {
                                        skeleton = model.Skeleton;
                                        break;
                                    }
                                }

                                // No fully-matching model skeleton: build one from the
                                // animation's own bones so every channel maps to a real
                                // node. Falling back to an arbitrary model skeleton that
                                // is missing the animation's bones makes the native FBX
                                // exporter crash (access violation).
                                if (skeleton == null)
                                {
                                    skeleton = new STSkeleton();
                                    foreach (var animBone in fska.Bones)
                                    {
                                        var bone = new STBone();
                                        bone.Text = animBone.Text;
                                        bone.skeletonParent = skeleton;

                                        // Give the bone a rest pose from the animation's first
                                        // frame. Without this the bone's Transform is the default
                                        // zero matrix, which exports as a degenerate node
                                        // (scale 0 / invalid rotation) in the FBX.
                                        var pos = animBone.GetPosition(0);
                                        var sca = animBone.GetScale(0);
                                        OpenTK.Quaternion rot;
                                        if (animBone.RotType == Animation.RotationType.EULER)
                                        {
                                            float rx = animBone.XROT.HasAnimation() ? animBone.XROT.GetValue(0) : 0;
                                            float ry = animBone.YROT.HasAnimation() ? animBone.YROT.GetValue(0) : 0;
                                            float rz = animBone.ZROT.HasAnimation() ? animBone.ZROT.GetValue(0) : 0;
                                            rot = STMath.FromEulerAngles(new OpenTK.Vector3(rx, ry, rz));
                                        }
                                        else
                                        {
                                            rot = animBone.GetRotation(0);
                                            if (rot.Length > 0.0001f) rot.Normalize();
                                            else rot = OpenTK.Quaternion.Identity;
                                        }

                                        bone.Transform = OpenTK.Matrix4.CreateScale(sca) *
                                                         OpenTK.Matrix4.CreateFromQuaternion(rot) *
                                                         OpenTK.Matrix4.CreateTranslation(pos);

                                        skeleton.bones.Add(bone);
                                    }
                                }

                                string animName = fska.Text;
                                if (format == "fbx")
                                {
                                    string animPath = Path.Combine(outputDir, $"{animName}.fbx");
                                    Console.WriteLine($"Exporting skeletal animation to FBX: {animName} -> {animPath}");
                                    AssimpSaver animSaver = new AssimpSaver();
                                    animSaver.SaveAnimation(fska, skeleton, animPath);
                                }
                                else if (format == "gltf" || format == "glb")
                                {
                                    string animPath = Path.Combine(outputDir, $"{animName}.{format}");
                                    Console.WriteLine($"Exporting skeletal animation to {format}: {animName} -> {animPath}");
                                    GltfSaver animSaver = new GltfSaver();
                                    animSaver.SaveAnimation(fska, skeleton, animPath);
                                }
                                else // dae -> Maya .anim companion
                                {
                                    string animPath = Path.Combine(outputDir, $"{animName}.anim");
                                    Console.WriteLine($"Exporting skeletal animation to Maya .anim: {animName} -> {animPath}");
                                    ANIM.CreateANIM(animPath, fska, skeleton);
                                }
                            }
                        }
                    }
                }
                Console.WriteLine($"Found {animCount} skeletal animation(s).");

                Console.WriteLine("All assets exported successfully.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"An error occurred during extraction: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                ForceExit(1);
            }

            // Force a clean, immediate exit to avoid the native Assimp teardown crash.
            ForceExit(0);
        }
    }
}
