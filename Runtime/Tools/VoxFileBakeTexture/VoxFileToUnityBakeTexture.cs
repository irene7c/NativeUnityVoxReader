using System.Collections.Generic;
using UnityEngine;
using Miventech.NativeUnityVoxReader.Data;
using Miventech.NativeUnityVoxReader.Tools.VoxFileBakeTexture.Data;
using System;

namespace Miventech.NativeUnityVoxReader.Tools.VoxFileBakeTexture
{
    public static class VoxFileToUnityBakeTexture
    {
         


        public static VoxModelResult[] Convert(VoxFile FileData, Color32[] palette, VoxFileToUnityBakeTextureSetting settings = default)
        {
            var result = new VoxModelResult[FileData.models.Count];
            int index = 0;
            foreach (var voxModel in FileData.models)
            {
                result[index] = ConvertModel(voxModel, palette, settings);
                index++;
            }
            return result;
        }
        
        public static VoxModelResult ConvertModel(VoxModel model, Color32[] palette, VoxFileToUnityBakeTextureSetting settings = default){


            VoxModelResult result = new VoxModelResult(null,null,null);
             // 1. Generate local geometry
            List<QuadInfo> quads = new List<QuadInfo>();
            GenerateGreedyQuads(model, palette, quads, settings);

            if (quads.Count == 0) return null;

            // 2. Create Texture Atlas
            // Create temporary textures for each quad
            Texture2D[] tempTextures = new Texture2D[quads.Count];
            for (int i = 0; i < quads.Count; i++)
            {
                QuadInfo q = quads[i];
                // Create texture of the quad size
                Texture2D t = new Texture2D(q.width, q.height, TextureFormat.RGBA32, false);
                t.filterMode = FilterMode.Point;
                
                t.SetPixels32(q.colors);
                t.Apply();
                tempTextures[i] = t;
            }

            // 512x512 Atlas as base, but allow growth up to maxAtlasSize if needed.
            Texture2D atlas = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            atlas.filterMode = FilterMode.Point;
            
            // Pack textures. PackTextures returns the UV Rects in the atlas.
            // padding=0 for exact pixel art, or 1-2 to avoid bleeding. We'll use 0.
            Rect[] uvRects = atlas.PackTextures(tempTextures, 0, settings.maxAtlasSize, false);

            // Assign Material with the "baked" texture
            Material mat = new Material(GetDefaultShader());
            result.texture = atlas; 
            mat.mainTexture = atlas;
            mat.mainTexture.filterMode = FilterMode.Point; // Important for voxel look
            
            // Adjust properties to be Matte (Smoothness = 0)
            // BRP uses _Glossiness, URP/HDRP use _Smoothness
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.0f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.0f);
            
            result.material = mat;

            // 3. Generate Final Mesh by mapping UVs to the atlas
            Mesh mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            List<Vector3> vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            List<Vector2> uvs = new List<Vector2>();

            for (int i = 0; i < quads.Count; i++)
            {
                AddQuadToMesh(quads[i], uvRects[i], vertices, triangles, uvs);
            }
            for (int i = 0; i < vertices.Count; i++)
            {
                // Optional: Scale to 1.0 or user scale
                vertices[i] *= settings.Scale;
                // Re-center mesh local position
                // so the center adjustment should be (size.x, size.z, size.y)
                vertices[i] -= new Vector3(model.size.x * settings.Scale * 0.5f, model.size.z * settings.Scale * 0.5f, model.size.y * settings.Scale * 0.5f);
            }
            
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            
            result.mesh = mesh;

            // Cleanup temporary textures
            // In editor use DestroyImmediate
            foreach (var t in tempTextures)
            {
                if (t != null)
                {
                    if (Application.isEditor) GameObject.DestroyImmediate(t);
                    else GameObject.Destroy(t);
                }
            }

            return result;
        }

        private static Shader GetDefaultShader()
        {
            // Detect the current Render Pipeline
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
            {
                string pipelineType = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().ToString();
                
                if (pipelineType.Contains("Universal"))
                {
                    return Shader.Find("Universal Render Pipeline/Lit");
                }
                if (pipelineType.Contains("HDRenderPipeline") || pipelineType.Contains("HighDefinition"))
                {
                    return Shader.Find("HDRP/Lit");
                }
            }
            // Built-in Render Pipeline
            return Shader.Find("Standard");
        }

        private static void GenerateGreedyQuads(VoxModel model, Color32[] palette, List<QuadInfo> quads, VoxFileToUnityBakeTextureSetting settings = default)
        {
            Vector3Int size = model.size;
            int[,,] volume = new int[size.x, size.y, size.z];
            
            foreach (var v in model.voxels)
            {
                if(v.x < size.x && v.y < size.y && v.z < size.z)
                    volume[v.x, v.y, v.z] = v.colorIndex;
            }

            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3;
                int v = (d + 2) % 3;
                int[] x = new int[3];
                int[] q = new int[3];
                q[d] = 1;

                for (int faceDir = -1; faceDir <= 1; faceDir += 2)
                {
                    int[] mask = new int[size[u] * size[v]];

                    for (x[d] = 0; x[d] < size[d]; x[d]++)
                    {
                        int n = 0;
                        for (x[v] = 0; x[v] < size[v]; x[v]++)
                        {
                            for (x[u] = 0; x[u] < size[u]; x[u]++)
                            {
                                int cCurrent = volume[x[0], x[1], x[2]];
                                int cNeighbor = 0;
                                int nx = x[0] + (d == 0 ? faceDir : 0);
                                int ny = x[1] + (d == 1 ? faceDir : 0);
                                int nz = x[2] + (d == 2 ? faceDir : 0);

                                if (nx >= 0 && nx < size.x && 
                                    ny >= 0 && ny < size.y && 
                                    nz >= 0 && nz < size.z)
                                {
                                    cNeighbor = volume[nx, ny, nz];
                                }
                                
                                bool visible = (cCurrent != 0 && cNeighbor == 0);
                                // IMPORTANT: Save the color in the mask, but for merging
                                // we only care if it's != 0 to ignore color changes
                                mask[n++] = visible ? cCurrent : 0;
                            }
                        }

                        n = 0;
                        for (int j = 0; j < size[v]; j++)
                        {
                            for (int i = 0; i < size[u]; i++)
                            {
                                int c = mask[n];
                                if (c != 0) // If visible
                                {
                                    int width = 1;
                                    // Expand width WHILE visible (mask != 0), ignoring color changes
                                    while (i + width < size[u] && mask[n + width] != 0 && width < settings.maxQuadSize) 
                                    {
                                        width++;
                                    }

                                    int height = 1;
                                    bool done = false;
                                    while (j + height < size[v] && height < settings.maxQuadSize)
                                    {
                                        for (int k = 0; k < width; k++)
                                        {
                                            // Check if the next row is visible across the entire width
                                            if (mask[n + k + height * size[u]] == 0)
                                            {
                                                done = true;
                                                break;
                                            }
                                        }
                                        if (done) break;
                                        height++;
                                    }

                                    int[] pos = new int[3];
                                    pos[u] = i; 
                                    pos[v] = j; 
                                    pos[d] = x[d];

                                    // Extract individual colors from this block
                                    Color32[] quadColors = new Color32[width * height];
                                    
                                    // Iterate through the quad area to get colors from the original volume
                                    for (int ly = 0; ly < height; ly++)
                                    {
                                        for (int lx = 0; lx < width; lx++)
                                        {
                                            int[] voxelPos = new int[3];
                                            voxelPos[u] = pos[u] + lx;
                                            voxelPos[v] = pos[v] + ly;
                                            voxelPos[d] = pos[d];
                                            
                                            // Get color index from volume
                                            int colorIdx = volume[voxelPos[0], voxelPos[1], voxelPos[2]];
                                            
                                            // Convert to Color32
                                            Color32 colorPixel = Color.magenta;
                                            if (colorIdx - 1 < palette.Length && colorIdx - 1 >= 0) 
                                                colorPixel = palette[colorIdx - 1];
                                                
                                            
                                            quadColors[lx + ly * width] = colorPixel;
                                            
                                            
                                            mask[(j + ly) * size[u] + (i + lx)] = 0;
                                        }
                                    }

                                    int[] visualPos = new int[] { pos[0], pos[1], pos[2] };
                                    int depthOffset = (faceDir == 1) ? 1 : 0;
                                    visualPos[d] += depthOffset;

                                    AddQuadInfo(visualPos, u, v, d, width, height, faceDir, quadColors, quads);

                                    
                                    int skip = width - 1;
                                    i += skip;
                                    n += skip;
                                }
                                n++;
                            }
                        }
                    }
                }
            }
        }

        private static void AddQuadInfo(int[] pos, int axisU, int axisV, int axisD, int width, int height, int faceDir, Color32[] colors, List<QuadInfo> quads, VoxFileToUnityBakeTextureSetting settings = default)
        {
            QuadInfo q = new QuadInfo();
            q.colors = colors;
            q.width = width;
            q.height = height;
            q.faceDir = faceDir;

            // Calculate vertices in Unity World Space
            // v0: 0,0
            int[] p0 = new int[]{ pos[0], pos[1], pos[2] };
            // v1: w,0
            int[] p1 = new int[]{ pos[0], pos[1], pos[2] };
            p1[axisU] += width;
            // v2: 0,h
            int[] p2 = new int[]{ pos[0], pos[1], pos[2] };
            p2[axisV] += height;
            // v3: w,h
            int[] p3 = new int[]{ pos[0], pos[1], pos[2] };
            p3[axisU] += width;
            p3[axisV] += height;

            // Vox(x,y,z) -> Unity(x,z,y)
            q.v0 = new Vector3(p0[0], p0[2], p0[1]);
            q.v1 = new Vector3(p1[0], p1[2], p1[1]);
            q.v2 = new Vector3(p2[0], p2[2], p2[1]);
            q.v3 = new Vector3(p3[0], p3[2], p3[1]);

            quads.Add(q);
        }

        private static void AddQuadToMesh(QuadInfo q, Rect uvRect, List<Vector3> verts, List<int> tris, List<Vector2> uvs)
        {
            int baseIndex = verts.Count;
            verts.Add(q.v0);
            verts.Add(q.v1);
            verts.Add(q.v2);
            verts.Add(q.v3);

            // UVs: Map Quad corners (0,0 -> 1,1) to Atlas Rect
            // v0 (0,0) -> uvRect.min
            // v1 (w,0) -> uvRect.xMax, uvRect.yMin
            // v2 (0,h) -> uvRect.xMin, uvRect.yMax
            // v3 (w,h) -> uvRect.max
            
            // Note: In AddQuadInfo v0=(0,0), v1=(w,0), v2=(0,h), v3=(w,h) relative to quad origin.
            
            uvs.Add(new Vector2(uvRect.xMin, uvRect.yMin)); // v0
            uvs.Add(new Vector2(uvRect.xMax, uvRect.yMin)); // v1
            uvs.Add(new Vector2(uvRect.xMin, uvRect.yMax)); // v2 (Watch out for v2/v3 order in triangles)
            uvs.Add(new Vector2(uvRect.xMax, uvRect.yMax)); // v3

            // Winding order (Triangles)
            if (q.faceDir == 1)
            {
                // Positive normal
                tris.Add(baseIndex);     // 0
                tris.Add(baseIndex + 2); // 2
                tris.Add(baseIndex + 1); // 1
                
                tris.Add(baseIndex + 1); // 1
                tris.Add(baseIndex + 2); // 2
                tris.Add(baseIndex + 3); // 3
            }
            else
            {
                // Negative normal
                tris.Add(baseIndex);     // 0
                tris.Add(baseIndex + 1); // 1
                tris.Add(baseIndex + 2); // 2

                tris.Add(baseIndex + 1); // 1
                tris.Add(baseIndex + 3); // 3
                tris.Add(baseIndex + 2); // 2
            }
        }
        
        // Remove unused method
        /* private void AddCubeOptimized... */
        
        private static void AddFace(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Color32 color, List<Vector3> verts, List<int> tris, List<Color32> cols)
        {
            int baseIndex = verts.Count;

            verts.Add(v0);
            verts.Add(v1);
            verts.Add(v2);
            verts.Add(v3);

            cols.Add(color);
            cols.Add(color);
            cols.Add(color);
            cols.Add(color);

            // First triangle
            tris.Add(baseIndex);
            tris.Add(baseIndex + 1);
            tris.Add(baseIndex + 2);

            // Second triangle
            tris.Add(baseIndex);
            tris.Add(baseIndex + 2);
            tris.Add(baseIndex + 3);
        }
        
    }
}

