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
using AssimpFace = Silk.NET.Assimp.Face;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpOverloads = Silk.NET.Assimp.AssimpOverloads;
using Unmanaged;

namespace Models.Systems
{
    public class ModelImportSystem : SystemBase
    {
        private readonly Assimp library;
        private readonly ComponentQuery<IsModelRequest> modelRequestQuery;
        private readonly ComponentQuery<IsMeshRequest> meshRequestQuery;
        private readonly ComponentQuery<IsModel> modelQuery;
        private readonly UnmanagedDictionary<uint, uint> modelVersions;
        private readonly UnmanagedDictionary<uint, uint> meshVersions;
        private readonly ConcurrentQueue<Operation> operations;

        public ModelImportSystem(World world) : base(world)
        {
            library = Assimp.GetApi();
            modelQuery = new();
            meshRequestQuery = new();
            modelRequestQuery = new();
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
            modelRequestQuery.Update(world);
            foreach (var x in modelRequestQuery)
            {
                IsModelRequest request = x.Component1;
                bool sourceChanged = false;
                uint modelEntity = x.entity;
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
                        modelVersions.AddOrSet(modelEntity, request.version);
                    }
                }
            }
        }

        private void UpdateMeshes()
        {
            meshRequestQuery.Update(world);
            foreach (var x in meshRequestQuery)
            {
                IsMeshRequest request = x.Component1;
                bool sourceChanged = false;
                uint meshEntity = x.entity;
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
                        meshVersions.AddOrSet(meshEntity, request.version);
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

        private bool TryFinishMeshRequest((uint entity, IsMeshRequest request) input)
        {
            uint meshEntity = input.entity;
            uint index = input.request.meshIndex;
            rint modelReference = input.request.modelReference;
            uint modelEntity = world.GetReference(meshEntity, modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new(world, modelEntity);
            Mesh existingMesh = model[index];
            Entity existingMeshEntity = existingMesh.entity;
            Operation operation = new();
            operation.SelectEntity(meshEntity);

            if (world.TryGetComponent(meshEntity, out IsMesh component))
            {
                component.version++;
                operation.SetComponent(component);
                operation.SetComponent(new Name(existingMesh.Name));
            }
            else
            {
                //become a mesh now!
                operation.AddComponent(new IsMesh());
                operation.CreateArray<uint>(0);

                if (existingMesh.HasPositions)
                {
                    operation.CreateArray<MeshVertexPosition>(0);
                }

                if (existingMesh.HasUVs)
                {
                    operation.CreateArray<MeshVertexUV>(0);
                }

                if (existingMesh.HasNormals)
                {
                    operation.CreateArray<MeshVertexNormal>(0);
                }

                if (existingMesh.HasTangents)
                {
                    operation.CreateArray<MeshVertexTangent>(0);
                }

                if (existingMesh.HasBiTangents)
                {
                    operation.CreateArray<MeshVertexBiTangent>(0);
                }

                if (existingMesh.HasColors)
                {
                    operation.CreateArray<MeshVertexColor>(0);
                }
            }

            //copy each channel
            if (existingMesh.HasPositions)
            {
                USpan<MeshVertexPosition> positions = existingMeshEntity.GetArray<MeshVertexPosition>();
                USpan<uint> indices = existingMeshEntity.GetArray<uint>();
                operation.ResizeArray<uint>(indices.Length);
                operation.SetArrayElement(0, indices);
                operation.ResizeArray<MeshVertexPosition>(positions.Length);
                operation.SetArrayElement(0, positions);
            }

            if (existingMesh.HasUVs)
            {
                USpan<MeshVertexUV> uvs = existingMeshEntity.GetArray<MeshVertexUV>();
                operation.ResizeArray<MeshVertexUV>(uvs.Length);
                operation.SetArrayElement(0, uvs);
            }

            if (existingMesh.HasNormals)
            {
                USpan<MeshVertexNormal> normals = existingMeshEntity.GetArray<MeshVertexNormal>();
                operation.ResizeArray<MeshVertexNormal>(normals.Length);
                operation.SetArrayElement(0, normals);
            }

            if (existingMesh.HasTangents)
            {
                USpan<MeshVertexTangent> tangents = existingMeshEntity.GetArray<MeshVertexTangent>();
                operation.ResizeArray<MeshVertexTangent>(tangents.Length);
                operation.SetArrayElement(0, tangents);
            }

            if (existingMesh.HasBiTangents)
            {
                USpan<MeshVertexBiTangent> bitangents = existingMeshEntity.GetArray<MeshVertexBiTangent>();
                operation.ResizeArray<MeshVertexBiTangent>(bitangents.Length);
                operation.SetArrayElement(0, bitangents);
            }

            if (existingMesh.HasColors)
            {
                USpan<MeshVertexColor> colors = existingMeshEntity.GetArray<MeshVertexColor>();
                operation.ResizeArray<MeshVertexColor>(colors.Length);
                operation.SetArrayElement(0, colors);
            }

            operations.Enqueue(operation);
            return true;
        }

        private bool TryFinishModelRequest(uint modelEntity)
        {
            //wait for byte data to be available
            if (!world.ContainsArray<byte>(modelEntity))
            {
                Console.WriteLine($"Model data not available on entity {modelEntity}, waiting");
                return false;
            }

            Operation operation = new();
            USpan<byte> byteData = world.GetArray<byte>(modelEntity);
            ImportModel(modelEntity, ref operation, byteData);

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

        private unsafe uint ImportModel(uint modelEntity, ref Operation operation, USpan<byte> bytes)
        {
            uint pFlags = (uint)AssimpPostProcessSteps.Triangulate;
            USpan<byte> pHint = [];
            USpan<AssimpPropertyStore> pProps = [];
            AssimpScene* scene = AssimpOverloads.ImportFileFromMemoryWithProperties(library, bytes.pointer, bytes.Length, pFlags, pHint.AsSystemSpan(), pProps.AsSystemSpan());
            if (scene is null || scene->MFlags == 1 || scene->MRootNode is null)
            {
                throw new Exception(library.GetErrorStringS());
            }

            bool containsMeshes = world.ContainsArray<ModelMesh>(modelEntity);
            uint existingMeshCount = containsMeshes ? world.GetArrayLength<ModelMesh>(modelEntity) : 0;
            operation.SelectEntity(modelEntity);
            uint referenceCount = world.GetReferenceCount(modelEntity);
            using UnmanagedList<ModelMesh> meshes = new();
            ProcessNode(scene->MRootNode, scene, ref operation);
            operation.SelectEntity(modelEntity);
            if (containsMeshes)
            {
                operation.ResizeArray<ModelMesh>(meshes.Count);
                operation.SetArrayElement(0, meshes.AsSpan());
            }
            else
            {
                operation.CreateArray<ModelMesh>(meshes.AsSpan());
            }

            return meshes.Count;

            void ProcessNode(AssimpNode* node, AssimpScene* scene, ref Operation operation)
            {
                for (uint i = 0; i < node->MNumMeshes; i++)
                {
                    AssimpMesh* mesh = scene->MMeshes[node->MMeshes[i]];
                    ProcessMesh(mesh, scene, ref operation, meshes);
                }

                for (uint i = 0; i < node->MNumChildren; i++)
                {
                    ProcessNode(node->MChildren[i], scene, ref operation);
                }
            }

            void ProcessMesh(AssimpMesh* mesh, AssimpScene* scene, ref Operation operation, UnmanagedList<ModelMesh> meshes)
            {
                uint vertexCount = mesh->MNumVertices;
                Vector3* positions = mesh->MVertices;
                Vector3* uvs = mesh->MTextureCoords[0];
                Vector3* normals = mesh->MNormals;
                Vector3* tangents = mesh->MTangents;
                Vector3* biTangents = mesh->MBitangents;
                Vector4* colors = mesh->MColors[0];

                //todo: accuracy: should reuse based on mesh name rather than index within the list, because the amount of meshes
                //in the source asset could change, and could possibly shift around in order
                uint meshIndex = meshes.Count;
                var name = mesh->MName;
                bool meshReused = meshIndex < existingMeshCount;
                uint existingMesh = default;
                ModelMesh modelMesh;
                if (meshReused)
                {
                    //reset existing mesh
                    rint existingMeshReference = world.GetArrayElementRef<ModelMesh>(modelEntity, meshIndex).value;
                    existingMesh = world.GetReference(modelEntity, existingMeshReference);
                    operation.SelectEntity(existingMesh);
                    operation.SetComponent(new Name(name.AsString));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.ClearSelection();
                    operation.CreateEntity();
                    operation.SetParent(modelEntity);
                    operation.CreateArray<uint>(0);
                    operation.AddComponent(new Name(name.AsString));

                    //reference the created mesh
                    operation.ClearSelection();
                    operation.SelectEntity(modelEntity);
                    operation.AddReferenceTowardsPreviouslyCreatedEntity(0);
                    rint newReference = (rint)(referenceCount + meshIndex + 1);
                    modelMesh = new(newReference);

                    //select the created mesh again
                    operation.SelectPreviouslyCreatedEntity(0);

                    if (positions is not null)
                    {
                        operation.CreateArray<MeshVertexPosition>(0);
                    }

                    if (uvs is not null)
                    {
                        operation.CreateArray<MeshVertexUV>(0);
                    }

                    if (normals is not null)
                    {
                        operation.CreateArray<MeshVertexNormal>(0);
                    }

                    if (tangents is not null)
                    {
                        operation.CreateArray<MeshVertexTangent>(0);
                    }

                    if (biTangents is not null)
                    {
                        operation.CreateArray<MeshVertexBiTangent>(0);
                    }

                    if (colors is not null)
                    {
                        operation.CreateArray<MeshVertexColor>(0);
                    }
                }

                meshes.Add(modelMesh);

                //fill in data
                if (positions is not null)
                {
                    operation.ResizeArray<MeshVertexPosition>(vertexCount);
                    operation.SetArrayElement(0, new USpan<MeshVertexPosition>(positions, vertexCount));
                    uint faceCount = mesh->MNumFaces;
                    using UnmanagedList<uint> indices = new();
                    for (uint i = 0; i < faceCount; i++)
                    {
                        AssimpFace face = mesh->MFaces[i];
                        for (uint j = 0; j < face.MNumIndices; j++)
                        {
                            uint index = face.MIndices[j];
                            indices.Add(index);
                        }
                    }

                    operation.ResizeArray<uint>(indices.Count);
                    operation.SetArrayElement(0, indices.AsSpan());
                }

                if (uvs is not null)
                {
                    using UnmanagedArray<MeshVertexUV> uvSpan = new(vertexCount);
                    USpan<Vector3> vector3s = new(uvs, mesh->MNumVertices);
                    for (uint i = 0; i < vector3s.Length; i++)
                    {
                        Vector3 raw = vector3s[i];
                        uvSpan[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.ResizeArray<MeshVertexUV>(vertexCount);
                    operation.SetArrayElement(0, uvSpan.AsSpan());
                }

                if (normals is not null)
                {
                    operation.ResizeArray<MeshVertexNormal>(vertexCount);
                    operation.SetArrayElement(0, new USpan<MeshVertexNormal>(normals, vertexCount));
                }

                if (tangents is not null)
                {
                    operation.ResizeArray<MeshVertexTangent>(vertexCount);
                    operation.SetArrayElement(0, new USpan<MeshVertexTangent>(tangents, vertexCount));
                }

                if (biTangents is not null)
                {
                    operation.ResizeArray<MeshVertexBiTangent>(vertexCount);
                    operation.SetArrayElement(0, new USpan<MeshVertexBiTangent>(biTangents, vertexCount));
                }

                if (colors is not null)
                {
                    operation.ResizeArray<MeshVertexColor>(vertexCount);
                    operation.SetArrayElement(0, new USpan<MeshVertexColor>(colors, vertexCount));
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
}