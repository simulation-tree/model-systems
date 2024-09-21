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
using OpenAssetImporter;
using Assimp;
using Unmanaged;

namespace Models.Systems
{
    public class ModelImportSystem : SystemBase
    {
        private readonly Library library;
        private readonly ComponentQuery<IsModelRequest> modelRequestQuery;
        private readonly ComponentQuery<IsMeshRequest> meshRequestQuery;
        private readonly ComponentQuery<IsModel> modelQuery;
        private readonly UnmanagedDictionary<uint, uint> modelVersions;
        private readonly UnmanagedDictionary<uint, uint> meshVersions;
        private readonly ConcurrentQueue<Operation> operations;

        public ModelImportSystem(World world) : base(world)
        {
            library = new();
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
            var existingMesh = model[index];
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
                operation.SetArrayElements(0, indices);
                operation.ResizeArray<MeshVertexPosition>(positions.Length);
                operation.SetArrayElements(0, positions);
            }

            if (existingMesh.HasUVs)
            {
                USpan<MeshVertexUV> uvs = existingMeshEntity.GetArray<MeshVertexUV>();
                operation.ResizeArray<MeshVertexUV>(uvs.Length);
                operation.SetArrayElements(0, uvs);
            }

            if (existingMesh.HasNormals)
            {
                USpan<MeshVertexNormal> normals = existingMeshEntity.GetArray<MeshVertexNormal>();
                operation.ResizeArray<MeshVertexNormal>(normals.Length);
                operation.SetArrayElements(0, normals);
            }

            if (existingMesh.HasTangents)
            {
                USpan<MeshVertexTangent> tangents = existingMeshEntity.GetArray<MeshVertexTangent>();
                operation.ResizeArray<MeshVertexTangent>(tangents.Length);
                operation.SetArrayElements(0, tangents);
            }

            if (existingMesh.HasBiTangents)
            {
                USpan<MeshVertexBiTangent> bitangents = existingMeshEntity.GetArray<MeshVertexBiTangent>();
                operation.ResizeArray<MeshVertexBiTangent>(bitangents.Length);
                operation.SetArrayElements(0, bitangents);
            }

            if (existingMesh.HasColors)
            {
                USpan<MeshVertexColor> colors = existingMeshEntity.GetArray<MeshVertexColor>();
                operation.ResizeArray<MeshVertexColor>(colors.Length);
                operation.SetArrayElements(0, colors);
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
            Scene scene = library.ImportModel(bytes);
            bool containsMeshes = world.ContainsArray<ModelMesh>(modelEntity);
            uint existingMeshCount = containsMeshes ? world.GetArrayLength<ModelMesh>(modelEntity) : 0;
            operation.SelectEntity(modelEntity);
            uint referenceCount = world.GetReferenceCount(modelEntity);
            using UnmanagedList<ModelMesh> meshes = new();
            ProcessNode(scene.RootNode, scene, ref operation);
            operation.SelectEntity(modelEntity);
            if (containsMeshes)
            {
                operation.ResizeArray<ModelMesh>(meshes.Count);
                operation.SetArrayElements(0, meshes.AsSpan());
            }
            else
            {
                operation.CreateArray<ModelMesh>(meshes.AsSpan());
            }

            return meshes.Count;

            void ProcessNode(Node node, Scene scene, ref Operation operation)
            {
                for (uint i = 0; i < node.MeshCount; i++)
                {
                    Assimp.Mesh mesh = scene.Meshes[node.MeshIndices[(int)i]];
                    ProcessMesh(mesh, scene, ref operation, meshes);
                }

                for (uint i = 0; i < node.ChildCount; i++)
                {
                    Node child = node.Children[(int)i];
                    ProcessNode(child, scene, ref operation);
                }
            }

            void ProcessMesh(Assimp.Mesh mesh, Scene scene, ref Operation operation, UnmanagedList<ModelMesh> meshes)
            {
                //uint vertexCount = mesh->MNumVertices;
                //Vector3* positions = mesh->MVertices;
                //Vector3* uvs = mesh->MTextureCoords[0];
                //Vector3* normals = mesh->MNormals;
                //Vector3* tangents = mesh->MTangents;
                //Vector3* biTangents = mesh->MBitangents;
                //Vector4* colors = mesh->MColors[0];

                uint vertexCount = (uint)mesh.VertexCount;
                uint faceCount = (uint)mesh.FaceCount;
                System.Collections.Generic.List<Vector3>? positions = mesh.HasVertices ? mesh.Vertices : null;
                System.Collections.Generic.List<Vector3>? uvs = mesh.HasTextureCoords(0) ? mesh.TextureCoordinateChannels[0] : null;
                System.Collections.Generic.List<Vector3>? normals = mesh.HasNormals ? mesh.Normals : null;
                System.Collections.Generic.List<Vector3>? tangents = mesh.HasTangentBasis ? mesh.Tangents : null;
                System.Collections.Generic.List<Vector3>? biTangents = mesh.HasTangentBasis ? mesh.BiTangents : null;
                System.Collections.Generic.List<Vector4>? colors = mesh.HasVertexColors(0) ? mesh.VertexColorChannels[0] : null;

                //todo: accuracy: should reuse based on mesh name rather than index within the list, because the amount of meshes
                //in the source asset could change, and could possibly shift around in order
                uint meshIndex = meshes.Count;
                string name = mesh.Name;
                bool meshReused = meshIndex < existingMeshCount;
                uint existingMesh = default;
                ModelMesh modelMesh;
                if (meshReused)
                {
                    //reset existing mesh
                    rint existingMeshReference = world.GetArrayElementRef<ModelMesh>(modelEntity, meshIndex).value;
                    existingMesh = world.GetReference(modelEntity, existingMeshReference);
                    operation.SelectEntity(existingMesh);
                    operation.SetComponent(new Name(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.ClearSelection();
                    operation.CreateEntity();
                    operation.SetParent(modelEntity);
                    operation.CreateArray<uint>(0);
                    operation.AddComponent(new Name(name));

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
                    using UnmanagedArray<MeshVertexPosition> tempData = new((uint)positions.Count);
                    for (uint i = 0; i < positions.Count; i++)
                    {
                        tempData[i] = positions[(int)i];
                    }

                    operation.ResizeArray<MeshVertexPosition>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());

                    using UnmanagedList<uint> indices = new();
                    for (uint i = 0; i < faceCount; i++)
                    {
                        Face face = mesh.Faces[(int)i];
                        for (uint j = 0; j < face.IndexCount; j++)
                        {
                            uint index = (uint)face.Indices[(int)j];
                            indices.Add(index);
                        }
                    }

                    operation.ResizeArray<uint>(indices.Count);
                    operation.SetArrayElements(0, indices.AsSpan());
                }

                if (uvs is not null)
                {
                    using UnmanagedArray<MeshVertexUV> tempData = new(vertexCount);
                    for (uint i = 0; i < uvs.Count; i++)
                    {
                        Vector3 raw = uvs[(int)i];
                        tempData[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.ResizeArray<MeshVertexUV>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (normals is not null)
                {
                    using UnmanagedArray<MeshVertexNormal> tempData = new((uint)normals.Count);
                    for (uint i = 0; i < normals.Count; i++)
                    {
                        tempData[i] = new(normals[(int)i]);
                    }

                    operation.ResizeArray<MeshVertexNormal>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (tangents is not null)
                {
                    using UnmanagedArray<MeshVertexTangent> tempData = new((uint)tangents.Count);
                    for (uint i = 0; i < tangents.Count; i++)
                    {
                        tempData[i] = new(tangents[(int)i]);
                    }

                    operation.ResizeArray<MeshVertexTangent>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (biTangents is not null)
                {
                    using UnmanagedArray<MeshVertexBiTangent> tempData = new((uint)biTangents.Count);
                    for (uint i = 0; i < biTangents.Count; i++)
                    {
                        tempData[i] = new(biTangents[(int)i]);
                    }

                    operation.ResizeArray<MeshVertexBiTangent>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (colors is not null)
                {
                    using UnmanagedArray<MeshVertexColor> tempData = new((uint)colors.Count);
                    for (uint i = 0; i < colors.Count; i++)
                    {
                        tempData[i] = new(colors[(int)i]);
                    }

                    operation.ResizeArray<MeshVertexColor>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                Material? material = scene.Materials[mesh.MaterialIndex];
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