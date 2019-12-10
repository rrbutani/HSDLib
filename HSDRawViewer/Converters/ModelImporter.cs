﻿using System;
using System.Collections.Generic;
using Assimp;
using Assimp.Configs;
using HSDRaw.Common;
using OpenTK;
using HSDRawViewer.Rendering;
using System.IO;
using HSDRaw.Tools;
using HSDRaw.GX;
using System.ComponentModel;
using HSDRawViewer.GUI;

namespace HSDRawViewer.Converters
{
    public enum ShadingType
    {
        None,
        Material,
        VertexColor
    }

    /// <summary>
    /// 
    /// </summary>
    public class ModelImportSettings
    {
        // Options
        [Category("Importing Options")]
        public bool FlipUVs { get; set; } = true;

        [Category("Importing Options")]
        public bool SmoothNormals { get; set; } = false;

        [Category("Importing Options")]
        public ShadingType ShadingType{ get; set; } = ShadingType.Material;

        [Category("Importing Options")]
        public bool ImportMaterialInfo { get; set; } = false;

        [Category("Importing Options")]
        public bool ImportTexture { get; set; } = true;
        
        //[Category("Importing Options"), Description("Attempts to keep JOBJ and DOBJ matching original model")]
        //public bool TryToUseExistingStructure { get; set; } = true;


        [Category("Vertex Attribute Options")]
        public bool ImportRigging { get; set; } = true;

        [Category("Vertex Attribute Options")]
        public bool ImportVertexAlpha { get; set; } = false;


        //[Category("Texture Options"), Description("Texture Type is inferred from filename i.e. Image_CMP.png")]
        //public bool TextureFormatFromName { get; set; } = true;

        [Category("Texture Options")]
        public GXTexFmt TextureFormat { get; set; } = GXTexFmt.RGB565;

        [Category("Texture Options")]
        public GXTlutFmt PaletteFormat { get; set; } = GXTlutFmt.RGB565;
    }

    /// <summary>
    /// Static class for importing 3d model information
    /// </summary>
    public class ModelImporter
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="toReplace"></param>
        public static void ReplaceModelFromFile(HSD_JOBJ toReplace)
        {
            var f = Tools.FileIO.OpenFile("Supported Formats (*.dae, *.obj)|*.dae;*.obj");

            if(f != null)
            {
                var settings = new ModelImportSettings();
                using (PropertyDialog d = new PropertyDialog("Model Import Options", settings))
                {
                    if(d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var newroot = ImportModel(f, settings);

                        toReplace._s.SetFromStruct(newroot._s);
                    }
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private class ProcessingCache
        {
            // Folder path to search for external files like textures
            public string FolderPath;
            
            // this tool automatically splits triangle lists into stripped pobjs
            public POBJ_Generator POBJGen = new POBJ_Generator();

            // cache the jobj names to their respective jobj
            public Dictionary<string, HSD_JOBJ> NameToJOBJ = new Dictionary<string, HSD_JOBJ>();

            // Single bound vertices need to be inverted by their parent bone
            public Dictionary<HSD_JOBJ, Matrix4> jobjToInverseTransform = new Dictionary<HSD_JOBJ, Matrix4>();
            public Dictionary<HSD_JOBJ, Matrix4> jobjToWorldTransform = new Dictionary<HSD_JOBJ, Matrix4>();

            // mesh nodes need to be processed after the jobjs
            public List<Node> MeshNodes = new List<Node>();

            // Indicates jobjs that need the SKELETON flag set along with inverted transform
            public List<HSD_JOBJ> EnvelopedJOBJs = new List<HSD_JOBJ>();
        }

        /// <summary>
        /// Imports supported model format into Root JOBJ
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public static HSD_JOBJ ImportModel(string filePath, ModelImportSettings settings = null)
        {
            // settings
            if(settings == null)
                settings = new ModelImportSettings();

            ProcessingCache cache = new ProcessingCache();
            cache.FolderPath = Path.GetDirectoryName(filePath);

            var processFlags = PostProcessPreset.TargetRealTimeMaximumQuality
                | PostProcessSteps.Triangulate
                | PostProcessSteps.LimitBoneWeights;

            if (settings.FlipUVs)
                processFlags |= PostProcessSteps.FlipUVs;

            System.Diagnostics.Debug.WriteLine("Importing Model...");
            // import
            AssimpContext importer = new AssimpContext();
            if(settings.SmoothNormals)
                importer.SetConfig(new NormalSmoothingAngleConfig(66.0f));
            importer.SetConfig(new VertexBoneWeightLimitConfig(3));
            var importmodel = importer.ImportFile(filePath, processFlags);

            System.Diagnostics.Debug.WriteLine("Processing Nodes...");
            // process nodes
            var rootjobj = RecursiveProcess(cache, settings, importmodel, importmodel.RootNode);

            // get root of skeleton
            rootjobj = rootjobj.Child;

            rootjobj.Flags |= JOBJ_FLAG.SKELETON_ROOT;

            // no need for excess nodes
            if (filePath.ToLower().EndsWith(".obj"))
                rootjobj.Next = null;

            // process mesh
            System.Diagnostics.Debug.WriteLine("Processing Mesh...");
            foreach (var mesh in cache.MeshNodes)
            {
                var Dobj = GetMeshes(cache, settings, importmodel, mesh);

                if (rootjobj.Dobj == null)
                    rootjobj.Dobj = Dobj;
                else
                    rootjobj.Dobj.Add(Dobj);

                System.Diagnostics.Debug.WriteLine($"Processing Mesh {rootjobj.Dobj.List.Count} {cache.MeshNodes.Count + 1}...");

                rootjobj.Flags |= JOBJ_FLAG.OPA;

                //TODO:
                //if (c.Flags.HasFlag(JOBJ_FLAG.OPA) || c.Flags.HasFlag(JOBJ_FLAG.ROOT_OPA))
                //    jobj.Flags |= JOBJ_FLAG.ROOT_OPA;

                if (settings.ShadingType == ShadingType.Material)
                {
                    rootjobj.Flags |= JOBJ_FLAG.LIGHTING;
                    foreach (var dobj in rootjobj.Dobj.List)
                        dobj.Mobj.RenderFlags |= RENDER_MODE.DIFFUSE;
                }
            }
            
            // SKELETON 
            if (cache.EnvelopedJOBJs.Count > 0)
                rootjobj.Flags |= JOBJ_FLAG.ENVELOPE_MODEL;

            foreach (var jobj in cache.EnvelopedJOBJs)
            {
                jobj.Flags |= JOBJ_FLAG.SKELETON;
                jobj.InverseWorldTransform = Matrix4ToHSDMatrix(cache.jobjToInverseTransform[jobj]);
            }

            // SAVE POBJ buffers
            System.Diagnostics.Debug.WriteLine("Saving Changes...");
            cache.POBJGen.SaveChanges();

            System.Diagnostics.Debug.WriteLine("Done!");

            // done
            return rootjobj;
        }

        private static HSD_Matrix4x3 Matrix4ToHSDMatrix(Matrix4 t)
        {
            var o = new HSD_Matrix4x3();
            o.M11 = t.M11;
            o.M12 = t.M12;
            o.M13 = t.M13;
            o.M14 = t.M14;
            o.M21 = t.M21;
            o.M22 = t.M22;
            o.M23 = t.M23;
            o.M24 = t.M24;
            o.M31 = t.M31;
            o.M32 = t.M32;
            o.M33 = t.M33;
            o.M34 = t.M34;
            return o;
        }

        /// <summary>
        /// Recursivly processing nodes and convert data into JOBJ
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="node"></param>
        /// <returns></returns>
        private static HSD_JOBJ RecursiveProcess(ProcessingCache cache, ModelImportSettings settings, Scene scene, Node node)
        {

            // node transform's translation is bugged
            Vector3D tr, s;
            Assimp.Quaternion q;
            node.Transform.Decompose(out s, out q, out tr);
            var translation = new Vector3(tr.X, tr.Y, tr.Z);
            var scale = new Vector3(s.X, s.Y, s.Z);
            var rotation = Math3D.ToEulerAngles(new OpenTK.Quaternion(q.X, q.Y, q.Z, q.W).Inverted());

            var t = Matrix4.CreateScale(scale) 
                * Matrix4.CreateFromQuaternion(new OpenTK.Quaternion(q.X, q.Y, q.Z, q.W)) 
                * Matrix4.CreateTranslation(translation);

            HSD_JOBJ jobj = new HSD_JOBJ();

            if (node.Parent != null)
            {
                t = t * cache.jobjToWorldTransform[cache.NameToJOBJ[node.Parent.Name]];
            }
            cache.NameToJOBJ.Add(node.Name, jobj);
            cache.jobjToWorldTransform.Add(jobj, t);
            cache.jobjToInverseTransform.Add(jobj, t.Inverted());

            jobj.Flags = JOBJ_FLAG.CLASSICAL_SCALING;
            jobj.TX = translation.X;
            jobj.TY = translation.Y;
            jobj.TZ = translation.Z;
            jobj.RX = rotation.X;
            jobj.RY = rotation.Y;
            jobj.RZ = rotation.Z;
            jobj.SX = scale.X;
            jobj.SY = scale.Y;
            jobj.SZ = scale.Z;

            if (node.HasMeshes)
                cache.MeshNodes.Add(node);

            foreach (var child in node.Children)
            {
                jobj.AddChild(RecursiveProcess(cache, settings, scene, child));
            }

            return jobj;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private static HSD_DOBJ GetMeshes(ProcessingCache cache, ModelImportSettings settings, Scene scene, Node node)
        {
            HSD_DOBJ root = null;
            HSD_DOBJ prev = null;

            foreach (int index in node.MeshIndices)
            {
                Mesh mesh = scene.Meshes[index];
                var material = scene.Materials[mesh.MaterialIndex];

                // Generate DOBJ
                HSD_DOBJ dobj = new HSD_DOBJ();

                if (root == null)
                    root = dobj;
                else
                    prev.Next = dobj;
                prev = dobj;

                dobj.Mobj = GenerateMaterial(cache, settings, material);

                // Assessment
                if (!mesh.HasFaces)
                    continue;

                List<GXAttribName> Attributes = new List<GXAttribName>();

                // todo: rigging
                List<HSD_JOBJ>[] jobjs = new List<HSD_JOBJ>[mesh.Vertices.Count];
                List<float>[] weights = new List<float>[mesh.Vertices.Count];
                if (mesh.HasBones)
                {
                    Attributes.Add(GXAttribName.GX_VA_PNMTXIDX);

                    foreach (var v in mesh.Bones)
                    {
                        var jobj = cache.NameToJOBJ[v.Name];

                        if (!cache.EnvelopedJOBJs.Contains(jobj))
                            cache.EnvelopedJOBJs.Add(jobj);

                        if (v.HasVertexWeights)
                            foreach (var vw in v.VertexWeights)
                            {
                                if (jobjs[vw.VertexID] == null)
                                    jobjs[vw.VertexID] = new List<HSD_JOBJ>();
                                if (weights[vw.VertexID] == null)
                                    weights[vw.VertexID] = new List<float>();
                                jobjs[vw.VertexID].Add(jobj);
                                weights[vw.VertexID].Add(vw.Weight);
                            }
                    }
                }

                if (mesh.HasVertices)
                    Attributes.Add(GXAttribName.GX_VA_POS);
                
                if (mesh.HasVertexColors(0) && settings.ShadingType == ShadingType.VertexColor)
                    Attributes.Add(GXAttribName.GX_VA_CLR0);

                //if (mesh.HasVertexColors(1) && settings.ImportVertexColors)
                //    Attributes.Add(GXAttribName.GX_VA_CLR1);

                if (mesh.HasNormals && settings.ShadingType == ShadingType.Material)
                    Attributes.Add(GXAttribName.GX_VA_NRM);

                if (mesh.HasTextureCoords(0))
                    Attributes.Add(GXAttribName.GX_VA_TEX0);

                //if (mesh.HasTextureCoords(1))
                //    Attributes.Add(GXAttribName.GX_VA_TEX1);

                var vertices = new List<GX_Vertex>();
                var jobjList = new List<HSD_JOBJ[]>(vertices.Count);
                var wList = new List<float[]>(vertices.Count);

                foreach (var face in mesh.Faces)
                {
                    PrimitiveType faceMode;
                    switch (face.IndexCount)
                    {
                        case 1:
                            faceMode = PrimitiveType.Point;
                            break;
                        case 2:
                            faceMode = PrimitiveType.Line;
                            break;
                        case 3:
                            faceMode = PrimitiveType.Triangle;
                            break;
                        default:
                            faceMode = PrimitiveType.Polygon;
                            break;
                    }

                    if (faceMode != PrimitiveType.Triangle)
                    {
                        continue;
                        //throw new NotSupportedException($"Non triangle primitive types not supported at this time {faceMode}");
                    }

                    for (int i = 0; i < face.IndexCount; i++)
                    {
                        int indicie = face.Indices[i];

                        GX_Vertex vertex = new GX_Vertex();

                        if (mesh.HasBones)
                        {
                            jobjList.Add(jobjs[indicie].ToArray());
                            wList.Add(weights[indicie].ToArray());

                            // Single Binds Get Inverted
                            var tkvert = new Vector3(mesh.Vertices[indicie].X, mesh.Vertices[indicie].Y, mesh.Vertices[indicie].Z);
                            var tknrm = new Vector3(mesh.Normals[indicie].X, mesh.Normals[indicie].Y, mesh.Normals[indicie].Z);

                            if(jobjs[indicie].Count == 1)
                            {
                                tkvert = Vector3.TransformPosition(tkvert, cache.jobjToInverseTransform[jobjs[indicie][0]]);
                                tknrm = Vector3.TransformNormal(tknrm, cache.jobjToInverseTransform[jobjs[indicie][0]]);
                            }

                            vertex.POS = new GXVector3(tkvert.X, tkvert.Y, tkvert.Z);
                            vertex.NRM = new GXVector3(tknrm.X, tknrm.Y, tknrm.Z);
                        }
                        else
                        {
                            if (mesh.HasVertices)
                                vertex.POS = new GXVector3(mesh.Vertices[indicie].X, mesh.Vertices[indicie].Y, mesh.Vertices[indicie].Z);
                            
                            if (mesh.HasNormals)
                                vertex.NRM = new GXVector3(mesh.Normals[indicie].X, mesh.Normals[indicie].Y, mesh.Normals[indicie].Z);
                        }

                        if (mesh.HasTextureCoords(0))
                            vertex.TEX0 = new GXVector2(
                                mesh.TextureCoordinateChannels[0][indicie].X,
                                mesh.TextureCoordinateChannels[0][indicie].Y);

                        if (mesh.HasTextureCoords(1))
                            vertex.TEX1 = new GXVector2(
                                mesh.TextureCoordinateChannels[1][indicie].X,
                                mesh.TextureCoordinateChannels[1][indicie].Y);

                        if (mesh.HasVertexColors(0))
                            vertex.CLR0 = new GXColor4(
                                mesh.VertexColorChannels[0][indicie].R,
                                mesh.VertexColorChannels[0][indicie].G,
                                mesh.VertexColorChannels[0][indicie].B,
                                settings.ImportVertexAlpha ? mesh.VertexColorChannels[0][indicie].A : 1);

                        if (mesh.HasVertexColors(1))
                            vertex.CLR0 = new GXColor4(
                                mesh.VertexColorChannels[1][indicie].R,
                                mesh.VertexColorChannels[1][indicie].G,
                                mesh.VertexColorChannels[1][indicie].B,
                                settings.ImportVertexAlpha ? mesh.VertexColorChannels[1][indicie].A : 1);

                        vertices.Add(vertex);
                    }

                }

                if (mesh.HasBones)
                {
                    dobj.Pobj = cache.POBJGen.CreatePOBJsFromTriangleList(vertices, Attributes.ToArray(), jobjList, wList);
                }
                else
                    dobj.Pobj = cache.POBJGen.CreatePOBJsFromTriangleList(vertices, Attributes.ToArray(), null);

            }

            return root;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="material"></param>
        /// <returns></returns>
        private static HSD_MOBJ GenerateMaterial(ProcessingCache cache, ModelImportSettings settings, Material material)
        {
            var Mobj = new HSD_MOBJ();
            Mobj.Material = new HSD_Material();
            Mobj.Material.AmbientColorRGBA = 0x7F7F7FFF;
            Mobj.Material.DiffuseColorRGBA = 0xFFFFFFFF;
            Mobj.Material.SpecularColorRGBA = 0xFFFFFFFF;
            Mobj.Material.Shininess = 1;
            Mobj.Material.Alpha = 1;
            Mobj.RenderFlags = RENDER_MODE.ALPHA_COMPAT | RENDER_MODE.DIFFSE_VTX;

            // Properties
            if (settings.ImportMaterialInfo)
            {
                if (material.HasShininess)
                    Mobj.Material.Shininess = material.Shininess;

                if (material.HasColorAmbient)
                {
                    Mobj.Material.AMB_A = ColorFloatToByte(material.ColorAmbient.A);
                    Mobj.Material.AMB_R = ColorFloatToByte(material.ColorAmbient.R);
                    Mobj.Material.AMB_G = ColorFloatToByte(material.ColorAmbient.G);
                    Mobj.Material.AMB_B = ColorFloatToByte(material.ColorAmbient.B);
                }
                if (material.HasColorDiffuse)
                {
                    Mobj.Material.DIF_A = ColorFloatToByte(material.ColorDiffuse.A);
                    Mobj.Material.DIF_R = ColorFloatToByte(material.ColorDiffuse.R);
                    Mobj.Material.DIF_G = ColorFloatToByte(material.ColorDiffuse.G);
                    Mobj.Material.DIF_B = ColorFloatToByte(material.ColorDiffuse.B);
                }
                if (material.HasColorSpecular)
                {
                    Mobj.Material.SPC_A = ColorFloatToByte(material.ColorSpecular.A);
                    Mobj.Material.SPC_R = ColorFloatToByte(material.ColorSpecular.R);
                    Mobj.Material.SPC_G = ColorFloatToByte(material.ColorSpecular.G);
                    Mobj.Material.SPC_B = ColorFloatToByte(material.ColorSpecular.B);
                }
            }

            // Textures
            if(settings.ImportTexture)
            {
                if (material.HasTextureDiffuse)
                {
                    var texturePath = Path.Combine(cache.FolderPath, material.TextureDiffuse.FilePath);

                    if (File.Exists(texturePath))
                    {
                        Mobj.RenderFlags |= RENDER_MODE.TEX0;

                        var tobj = TOBJConverter.ImportTOBJFromFile(texturePath, settings.TextureFormat, settings.PaletteFormat);
                        tobj.Flags = TOBJ_FLAGS.LIGHTMAP_DIFFUSE | TOBJ_FLAGS.COORD_UV;

                        if(settings.ShadingType == ShadingType.VertexColor || settings.ShadingType == ShadingType.Material)
                        {
                            tobj.Flags |= TOBJ_FLAGS.COLORMAP_MODULATE;
                        }
                        else
                        {
                            tobj.Flags |= TOBJ_FLAGS.COLORMAP_REPLACE;
                        }

                        tobj.GXTexGenSrc = 4;
                        tobj.TexMapID = GXTexMapID.GX_TEXMAP0;

                        tobj.WrapS = ToGXWrapMode(material.TextureDiffuse.WrapModeU);
                        tobj.WrapT = ToGXWrapMode(material.TextureDiffuse.WrapModeV);

                        Mobj.Textures = tobj;
                    }
                }
            }

            return Mobj;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="mode"></param>
        /// <returns></returns>
        private static GXWrapMode ToGXWrapMode(TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Clamp:
                    return GXWrapMode.CLAMP;
                case TextureWrapMode.Mirror:
                    return GXWrapMode.MIRROR;
                default:
                    return GXWrapMode.REPEAT;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        private static byte ColorFloatToByte(float val)
        {
            return (byte)(val > 1.0f ? 255 : val * 256);
        }

        /// <summary>
        /// Converts from Assimps <see cref="Matrix4x4"/> to OpenTK's <see cref="Matrix4"/>
        /// </summary>
        /// <param name="mat"></param>
        /// <returns></returns>
        public static Matrix4 FromMatrix(Matrix4x4 mat)
        {
            Matrix4 m = new Matrix4();
            m.M11 = mat.A1;
            m.M12 = mat.A2;
            m.M13 = mat.A3;
            m.M14 = mat.A4;
            m.M21 = mat.B1;
            m.M22 = mat.B2;
            m.M23 = mat.B3;
            m.M24 = mat.B4;
            m.M31 = mat.C1;
            m.M32 = mat.C2;
            m.M33 = mat.C3;
            m.M34 = mat.C4;
            m.M41 = mat.D1;
            m.M42 = mat.D2;
            m.M43 = mat.D3;
            m.M44 = mat.D4;
            return m;
        }

    }
}