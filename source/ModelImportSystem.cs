using Data.Components;
using Meshes;
using Meshes.Components;
using Models.Components;
using Models.Events;
using Silk.NET.Assimp;
using Simulation;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Unmanaged.Collections;
using Mesh = Silk.NET.Assimp.Mesh; //todo: replace with a minimized wrapper that doesnt depend on net core

namespace Models.Systems
{
    public class ModelImportSystem : SystemBase
    {
        private readonly Assimp library;
        private readonly Query<IsModel> modelQuery;

        public ModelImportSystem(World world) : base(world)
        {
            library = Assimp.GetApi();
            modelQuery = new(world);
            Subscribe<ModelUpdate>(Update);
        }

        public override void Dispose()
        {
            modelQuery.Dispose();
            library.Dispose();
            base.Dispose();
        }

        private void Update(ModelUpdate e)
        {
            UpdateModels();
        }

        private void UpdateModels()
        {
            modelQuery.Fill();
            foreach (Query<IsModel>.Result result in modelQuery)
            {
                ref IsModel model = ref result.Component1;
                if (model.changed)
                {
                    model.changed = false;
                    UpdateModel(result.entity);
                }
            }
        }

        private void UpdateModel(eint entity)
        {
            if (!world.ContainsList<ModelMesh>(entity))
            {
                world.CreateList<ModelMesh>(entity);
            }

            UnmanagedList<ModelMesh> meshes = world.GetList<ModelMesh>(entity);
            UnmanagedList<byte> byteData = world.GetList<byte>(entity);
            uint meshesImported = ImportModel(entity, meshes, byteData.AsSpan());

            //destroy meshes that werent used (remaining)
            if (meshesImported < meshes.Count)
            {
                uint remainingMeshes = meshes.Count - meshesImported;
                for (uint i = 0; i < remainingMeshes; i++)
                {
                    eint meshEntity = meshes[meshesImported + i].value;
                    world.DestroyEntity(meshEntity);
                }
            }
        }

        /// <summary>
        /// Updates the model so that its list of <see cref="ModelMesh"/> elements point to the latest mesh data.
        /// </summary>
        private unsafe uint ImportModel(eint entity, UnmanagedList<ModelMesh> meshes, Span<byte> bytes)
        {
            uint meshIndex = 0;
            fixed (byte* ptr = bytes)
            {
                uint pLength = (uint)bytes.Length;
                uint pFlags = (uint)PostProcessSteps.Triangulate;
                ReadOnlySpan<byte> pHint = [];
                ReadOnlySpan<PropertyStore> pProps = [];
                Scene* scene = library.ImportFileFromMemoryWithProperties(ptr, pLength, pFlags, pHint, pProps);
                if (scene is null || scene->MFlags == 1 || scene->MRootNode is null)
                {
                    throw new Exception(library.GetErrorStringS());
                }

                ProcessNode(entity, scene->MRootNode, scene, ref meshIndex);
                return meshIndex;

                void ProcessNode(eint parentEntity, Node* node, Scene* scene, ref uint meshIndex)
                {
                    for (int i = 0; i < node->MNumMeshes; i++)
                    {
                        Mesh* mesh = scene->MMeshes[node->MMeshes[i]];
                        ProcessMesh(parentEntity, mesh, scene, meshIndex);
                        meshIndex++;
                    }

                    for (int i = 0; i < node->MNumChildren; i++)
                    {
                        ProcessNode(parentEntity, node->MChildren[i], scene, ref meshIndex);
                    }
                }

                void ProcessMesh(eint parentEntity, Mesh* mesh, Scene* scene, uint meshIndex)
                {
                    eint meshEntity;
                    bool meshReused = meshIndex < meshes.Count;
                    if (meshReused)
                    {
                        meshEntity = meshes[meshIndex].value;
                        ClearMesh(meshEntity);
                    }
                    else
                    {
                        meshEntity = world.CreateEntity(parentEntity);
                        meshes.Add(new(meshEntity));
                    }

                    //update name of entity
                    AssimpString name = mesh->MName;
                    if (!world.ContainsComponent<Name>(meshEntity))
                    {
                        world.AddComponent<Name>(meshEntity, default);
                    }

                    ref Name entityName = ref world.GetComponentRef<Name>(meshEntity);
                    entityName.value.Clear();
                    entityName.value.Append(name.AsString);

                    //update collections
                    uint vertexCount = mesh->MNumVertices;
                    Vector3* positions = mesh->MVertices;
                    if (positions is not null)
                    {
                        fixed (MeshVertexPosition* pVertex = GetOrCreateCollectionAsSpan<MeshVertexPosition>(meshEntity, vertexCount))
                        {
                            Unsafe.CopyBlock(pVertex, positions, (uint)(vertexCount * sizeof(MeshVertexPosition)));
                        }
                    }

                    Vector3* uvs = mesh->MTextureCoords[0];
                    if (uvs is not null)
                    {
                        Span<MeshVertexUV> uvSpan = GetOrCreateCollectionAsSpan<MeshVertexUV>(meshEntity, vertexCount);
                        Span<Vector3> vector3s = new(uvs, (int)mesh->MNumVertices);
                        for (int i = 0; i < vector3s.Length; i++)
                        {
                            uvSpan[i] = new MeshVertexUV(new(vector3s[i].X, vector3s[i].Y));
                        }
                    }

                    Vector3* normals = mesh->MNormals;
                    if (normals is not null)
                    {
                        fixed (MeshVertexNormal* pNormal = GetOrCreateCollectionAsSpan<MeshVertexNormal>(meshEntity, vertexCount))
                        {
                            Unsafe.CopyBlock(pNormal, normals, (uint)(vertexCount * sizeof(MeshVertexNormal)));
                        }
                    }

                    Vector3* tangents = mesh->MTangents;
                    if (tangents is not null)
                    {
                        fixed (MeshVertexTangent* pTangent = GetOrCreateCollectionAsSpan<MeshVertexTangent>(meshEntity, vertexCount))
                        {
                            Unsafe.CopyBlock(pTangent, tangents, (uint)(mesh->MNumVertices * sizeof(MeshVertexTangent)));
                        }
                    }

                    Vector3* bitangents = mesh->MBitangents;
                    if (bitangents is not null)
                    {
                        fixed (MeshVertexBitangent* pBitangent = GetOrCreateCollectionAsSpan<MeshVertexBitangent>(meshEntity, vertexCount))
                        {
                            Unsafe.CopyBlock(pBitangent, bitangents, (uint)(mesh->MNumVertices * sizeof(MeshVertexBitangent)));
                        }
                    }

                    Vector4* colors = mesh->MColors[0];
                    if (colors is not null)
                    {
                        fixed (MeshVertexColor* pColor = GetOrCreateCollectionAsSpan<MeshVertexColor>(meshEntity, vertexCount))
                        {
                            Unsafe.CopyBlock(pColor, colors, (uint)(mesh->MNumVertices * sizeof(MeshVertexColor)));
                        }
                    }

                    if (positions is not null)
                    {
                        uint faceCount = mesh->MNumFaces;
                        if (!world.ContainsList<uint>(meshEntity))
                        {
                            world.CreateList<uint>(meshEntity);
                        }

                        world.ClearList<uint>(meshEntity);
                        for (int i = 0; i < faceCount; i++)
                        {
                            Face face = mesh->MFaces[i];
                            for (int j = 0; j < face.MNumIndices; j++)
                            {
                                uint index = face.MIndices[j];
                                world.AddToCollection(meshEntity, index);
                            }
                        }
                    }

                    Material* material = scene->MMaterials[mesh->MMaterialIndex];
                    if (material is not null)
                    {
                        //todo: handle materials
                    }

                    //increment mesh version
                    if (!world.ContainsComponent<IsMesh>(meshEntity))
                    {
                        world.AddComponent<IsMesh>(meshEntity, default);
                    }

                    ref IsMesh component = ref world.GetComponentRef<IsMesh>(meshEntity);
                    component.version++;
                }
            }
        }

        private Span<T> GetOrCreateCollectionAsSpan<T>(eint meshEntity, uint count) where T : unmanaged
        {
            if (world.ContainsList<T>(meshEntity))
            {
                UnmanagedList<T> list = world.GetList<T>(meshEntity);
                list.Clear(count);
                list.AddDefault(count);
                return list.AsSpan();
            }
            else
            {
                UnmanagedList<T> list = world.CreateCollection<T>(meshEntity, count);
                list.AddDefault(count);
                return list.AsSpan();
            }
        }

        private unsafe void ClearMesh(eint meshEntity)
        {
            if (world.ContainsList<uint>(meshEntity))
            {
                world.ClearList<uint>(meshEntity);
            }

            if (world.ContainsList<MeshVertexPosition>(meshEntity))
            {
                world.ClearList<MeshVertexPosition>(meshEntity);
            }

            if (world.ContainsList<MeshVertexUV>(meshEntity))
            {
                world.ClearList<MeshVertexUV>(meshEntity);
            }

            if (world.ContainsList<MeshVertexNormal>(meshEntity))
            {
                world.ClearList<MeshVertexNormal>(meshEntity);
            }

            if (world.ContainsList<MeshVertexTangent>(meshEntity))
            {
                world.ClearList<MeshVertexTangent>(meshEntity);
            }

            if (world.ContainsList<MeshVertexBitangent>(meshEntity))
            {
                world.ClearList<MeshVertexBitangent>(meshEntity);
            }

            if (world.ContainsList<MeshVertexColor>(meshEntity))
            {
                world.ClearList<MeshVertexColor>(meshEntity);
            }
        }
    }
}