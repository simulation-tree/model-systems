using Data.Components;
using Meshes;
using Meshes.Components;
using Models.Components;
using Models.Events;
using Simulation;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using Unmanaged.Collections;

//todo: replace with a minimized wrapper that doesnt depend on net core, or have system.text.json attached... (like wtff??? why?)
using AssimpMesh = Silk.NET.Assimp.Mesh;
using AssimpScene = Silk.NET.Assimp.Scene;
using AssimpPostProcessSteps = Silk.NET.Assimp.PostProcessSteps;
using AssimpPropertyStore = Silk.NET.Assimp.PropertyStore;
using AssimpNode = Silk.NET.Assimp.Node;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpOverloads = Silk.NET.Assimp.AssimpOverloads;
using Unmanaged;

namespace Models.Systems
{
    public class ModelImportSystem : SystemBase
    {
        private readonly Assimp library;
        private readonly Query<IsModelRequest> modelRequestQuery;
        private readonly Query<IsMeshRequest> meshRequestQuery;
        private readonly Query<IsModel> modelQuery;
        private readonly UnmanagedDictionary<eint, uint> modelVersions;
        private readonly UnmanagedDictionary<eint, uint> meshVersions;
        private readonly ConcurrentQueue<Operation> operations;

        public ModelImportSystem(World world) : base(world)
        {
            library = Assimp.GetApi();
            modelQuery = new(world);
            meshRequestQuery = new(world);
            modelRequestQuery = new(world);
            modelVersions = new();
            meshVersions = new();
            operations = new();
            Subscribe<ModelUpdate>(Update);
        }

        public override void Dispose()
        {
            while (operations.TryDequeue(out Operation operation))
            {
                operation.Dispose();
            }

            meshVersions.Dispose();
            modelVersions.Dispose();
            modelRequestQuery.Dispose();
            meshRequestQuery.Dispose();
            modelQuery.Dispose();
            library.Dispose();
            base.Dispose();
        }

        private void Update(ModelUpdate e)
        {
            UpdateModels();
            PerformOperations();
            UpdateMeshes();
            PerformOperations();
        }

        private void UpdateModels()
        {
            modelRequestQuery.Update();
            foreach (var x in modelRequestQuery)
            {
                IsModelRequest request = x.Component1;
                bool sourceChanged = false;
                eint modelEntity = x.entity;
                if (!modelVersions.ContainsKey(modelEntity))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = modelVersions[modelEntity] != request.version;
                }

                if (sourceChanged)
                {
                    //ThreadPool.QueueUserWorkItem(UpdateMeshReferencesOnModelEntity, modelEntity, false);
                    if (TryFinishModelRequest(modelEntity))
                    {
                        modelVersions[modelEntity] = request.version;
                    }
                }
            }
        }

        private void UpdateMeshes()
        {
            meshRequestQuery.Update();
            foreach (var x in meshRequestQuery)
            {
                IsMeshRequest request = x.Component1;
                bool sourceChanged = false;
                eint meshEntity = x.entity;
                if (!meshVersions.ContainsKey(meshEntity))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = meshVersions[meshEntity] != request.version;
                }

                if (sourceChanged)
                {
                    //ThreadPool.QueueUserWorkItem(UpdateMesh, (meshEntity, request), false);
                    if (TryFinishMeshRequest((meshEntity, request)))
                    {
                        meshVersions[meshEntity] = request.version;
                    }
                }
            }
        }

        private void PerformOperations()
        {
            while (operations.TryDequeue(out Operation operation))
            {
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private bool TryFinishMeshRequest((eint entity, IsMeshRequest request) input)
        {
            eint meshEntity = input.entity;
            uint index = input.request.meshIndex;
            rint modelReference = input.request.modelReference;
            eint modelEntity = world.GetReference(meshEntity, modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new(world, modelEntity);
            Mesh existingMesh = model[index];
            Entity existingMeshEntity = existingMesh;
            Operation operation = new();
            operation.SelectEntity(meshEntity);

            if (world.TryGetComponent(meshEntity, out IsMesh component))
            {
                //reset existing mesh that is requesting data again
                ResetMesh(ref operation, meshEntity);
                component.version++;
                operation.SetComponent(component);
                operation.SetComponent(new Name(existingMesh.Name));
            }
            else
            {
                //become a mesh now!
                operation.AddComponent(new IsMesh());
                operation.CreateList<uint>();

                if (existingMesh.HasPositions)
                {
                    operation.CreateList<MeshVertexPosition>();
                }

                if (existingMesh.HasUVs)
                {
                    operation.CreateList<MeshVertexUV>();
                }

                if (existingMesh.HasNormals)
                {
                    operation.CreateList<MeshVertexNormal>();
                }

                if (existingMesh.HasTangents)
                {
                    operation.CreateList<MeshVertexTangent>();
                }

                if (existingMesh.HasBiTangents)
                {
                    operation.CreateList<MeshVertexBiTangent>();
                }

                if (existingMesh.HasColors)
                {
                    operation.CreateList<MeshVertexColor>();
                }
            }

            //copy each channel
            if (existingMesh.HasPositions)
            {
                UnmanagedList<MeshVertexPosition> positions = world.GetList<MeshVertexPosition>(existingMeshEntity);
                UnmanagedList<uint> indices = world.GetList<uint>(existingMeshEntity);
                operation.AppendToList<MeshVertexPosition>(positions.AsSpan());
                operation.AppendToList<uint>(indices.AsSpan());
            }

            if (existingMesh.HasUVs)
            {
                UnmanagedList<MeshVertexUV> uvs = world.GetList<MeshVertexUV>(existingMeshEntity);
                operation.AppendToList<MeshVertexUV>(uvs.AsSpan());
            }

            if (existingMesh.HasNormals)
            {
                UnmanagedList<MeshVertexNormal> normals = world.GetList<MeshVertexNormal>(existingMeshEntity);
                operation.AppendToList<MeshVertexNormal>(normals.AsSpan());
            }

            if (existingMesh.HasTangents)
            {
                UnmanagedList<MeshVertexTangent> tangents = world.GetList<MeshVertexTangent>(existingMeshEntity);
                operation.AppendToList<MeshVertexTangent>(tangents.AsSpan());
            }

            if (existingMesh.HasBiTangents)
            {
                UnmanagedList<MeshVertexBiTangent> bitangents = world.GetList<MeshVertexBiTangent>(existingMeshEntity);
                operation.AppendToList<MeshVertexBiTangent>(bitangents.AsSpan());
            }

            if (existingMesh.HasColors)
            {
                UnmanagedList<MeshVertexColor> colors = world.GetList<MeshVertexColor>(existingMeshEntity);
                operation.AppendToList<MeshVertexColor>(colors.AsSpan());
            }

            operations.Enqueue(operation);
            return true;
        }

        private bool TryFinishModelRequest(eint modelEntity)
        {
            //wait for byte data to be available
            if (!world.ContainsList<byte>(modelEntity))
            {
                return false;
            }

            Operation operation = new();
            UnmanagedList<byte> byteData = world.GetList<byte>(modelEntity);
            ImportModel(modelEntity, ref operation, byteData.AsSpan());

            operation.ClearSelection();
            operation.SelectEntity(modelEntity);
            if (world.TryGetComponent(modelEntity, out IsModel component))
            {
                component.version++;
                operation.SetComponent(component);
            }
            else
            {
                operation.AddComponent(new IsModel());
            }

            operations.Enqueue(operation);
            return true;
        }

        private unsafe uint ImportModel(eint modelEntity, ref Operation operation, Span<byte> bytes)
        {
            uint meshIndex = 0;
            fixed (byte* ptr = bytes)
            {
                uint pLength = (uint)bytes.Length;
                uint pFlags = (uint)AssimpPostProcessSteps.Triangulate;
                ReadOnlySpan<byte> pHint = [];
                ReadOnlySpan<AssimpPropertyStore> pProps = [];
                AssimpScene* scene = AssimpOverloads.ImportFileFromMemoryWithProperties(library, ptr, pLength, pFlags, pHint, pProps);
                if (scene is null || scene->MFlags == 1 || scene->MRootNode is null)
                {
                    throw new Exception(library.GetErrorStringS());
                }

                uint existingMeshCount = world.GetListLength<ModelMesh>(modelEntity, out bool contains);
                operation.SelectEntity(modelEntity);
                if (!contains)
                {
                    operation.CreateList<ModelMesh>();
                }

                uint referenceCount = world.GetReferenceCount(modelEntity);
                ProcessNode(scene->MRootNode, scene, ref operation, ref meshIndex);
                return meshIndex;

                void ProcessNode(AssimpNode* node, AssimpScene* scene, ref Operation operation, ref uint meshIndex)
                {
                    for (int i = 0; i < node->MNumMeshes; i++)
                    {
                        AssimpMesh* mesh = scene->MMeshes[node->MMeshes[i]];
                        ProcessMesh(mesh, scene, ref operation, meshIndex);
                        meshIndex++;
                    }

                    for (int i = 0; i < node->MNumChildren; i++)
                    {
                        ProcessNode(node->MChildren[i], scene, ref operation, ref meshIndex);
                    }
                }

                void ProcessMesh(AssimpMesh* mesh, AssimpScene* scene, ref Operation operation, uint meshIndex)
                {
                    uint vertexCount = mesh->MNumVertices;
                    Vector3* positions = mesh->MVertices;
                    Vector3* uvs = mesh->MTextureCoords[0];
                    Vector3* normals = mesh->MNormals;
                    Vector3* tangents = mesh->MTangents;
                    Vector3* bitangents = mesh->MBitangents;
                    Vector4* colors = mesh->MColors[0];

                    //todo: accuracy: should reuse based on mesh name rather than index within the list, because the amount of meshes
                    //in the source asset could change, and could possibly shift around in order
                    var name = mesh->MName;
                    bool meshReused = meshIndex < existingMeshCount;
                    eint existingMesh = default;
                    if (meshReused)
                    {
                        //reset existing mesh
                        rint existingMeshReference = world.GetListElement<ModelMesh>(modelEntity, meshIndex).value;
                        existingMesh = world.GetReference(modelEntity, existingMeshReference);
                        operation.SelectEntity(existingMesh);
                        ResetMesh(ref operation, existingMesh);
                        operation.SetComponent(new Name(name.AsString));
                    }
                    else
                    {
                        //create new mesh
                        operation.ClearSelection();
                        operation.CreateEntity();
                        operation.SetParent(modelEntity);
                        operation.CreateList<uint>();
                        operation.AddComponent(new Name(name.AsString));

                        //reference the created mesh
                        operation.ClearSelection();
                        operation.SelectEntity(modelEntity);
                        operation.AddReference(0);
                        operation.AppendToList(new ModelMesh((rint)(referenceCount + meshIndex + 1)));

                        //select the created mesh again
                        operation.SelectEntity(0);

                        if (positions is not null)
                        {
                            operation.CreateList<MeshVertexPosition>();
                        }

                        if (uvs is not null)
                        {
                            operation.CreateList<MeshVertexUV>();
                        }

                        if (normals is not null)
                        {
                            operation.CreateList<MeshVertexNormal>();
                        }

                        if (tangents is not null)
                        {
                            operation.CreateList<MeshVertexTangent>();
                        }

                        if (bitangents is not null)
                        {
                            operation.CreateList<MeshVertexBiTangent>();
                        }

                        if (colors is not null)
                        {
                            operation.CreateList<MeshVertexColor>();
                        }
                    }

                    //fill in data
                    if (positions is not null)
                    {
                        operation.AppendToList<MeshVertexPosition>(positions, vertexCount);
                        uint faceCount = mesh->MNumFaces;
                        using UnmanagedList<uint> indices = new();
                        for (int i = 0; i < faceCount; i++)
                        {
                            var face = mesh->MFaces[i];
                            for (int j = 0; j < face.MNumIndices; j++)
                            {
                                uint index = face.MIndices[j];
                                indices.Add(index);
                            }
                        }

                        operation.AppendToList<uint>(indices.AsSpan());
                    }

                    if (uvs is not null)
                    {
                        using UnmanagedArray<MeshVertexUV> uvSpan = new(vertexCount);
                        Span<Vector3> vector3s = new(uvs, (int)mesh->MNumVertices);
                        for (uint i = 0; i < vector3s.Length; i++)
                        {
                            Vector3 raw = vector3s[(int)i];
                            uvSpan[i] = new MeshVertexUV(new(raw.X, raw.Y));
                        }

                        operation.AppendToList<MeshVertexUV>(uvSpan.AsSpan());
                    }

                    if (normals is not null)
                    {
                        operation.AppendToList<MeshVertexNormal>(normals, vertexCount);
                    }

                    if (tangents is not null)
                    {
                        operation.AppendToList<MeshVertexTangent>(tangents, vertexCount);
                    }

                    if (bitangents is not null)
                    {
                        operation.AppendToList<MeshVertexBiTangent>(bitangents, vertexCount);
                    }

                    if (colors is not null)
                    {
                        operation.AppendToList<MeshVertexColor>(colors, vertexCount);
                    }

                    var material = scene->MMaterials[mesh->MMaterialIndex];
                    if (material is not null)
                    {
                        //todo: handle materials
                    }

                    //increment mesh version
                    if (meshReused)
                    {
                        if (world.TryGetComponent(existingMesh, out IsMesh component))
                        {
                            component.version++;
                            operation.SetComponent(component);
                        }
                        else
                        {
                            operation.AddComponent(new IsMesh());
                        }
                    }
                    else
                    {
                        operation.AddComponent(new IsMesh());
                    }
                }
            }
        }

        /// <summary>
        /// Operations to reset the existing mesh entity to defaults.
        /// </summary>
        private unsafe void ResetMesh(ref Operation operation, eint meshEntity)
        {
            if (world.GetListLength<MeshVertexPosition>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexPosition>();
            }

            if (world.GetListLength<MeshVertexUV>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexUV>();
            }

            if (world.GetListLength<MeshVertexNormal>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexNormal>();
            }

            if (world.GetListLength<MeshVertexTangent>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexTangent>();
            }

            if (world.GetListLength<MeshVertexBiTangent>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexBiTangent>();
            }

            if (world.GetListLength<MeshVertexColor>(meshEntity) > 0)
            {
                operation.ClearList<MeshVertexColor>();
            }
        }
    }
}