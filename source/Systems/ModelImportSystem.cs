using Assimp;
using Data.Components;
using Meshes;
using Meshes.Components;
using Models.Components;
using OpenAssetImporter;
using Simulation;
using Simulation.Functions;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Unmanaged;
using Unmanaged.Collections;

namespace Models.Systems
{
    public readonly struct ModelImportSystem : ISystem
    {
        private readonly Library library;
        private readonly ComponentQuery<IsModelRequest> modelRequestQuery;
        private readonly ComponentQuery<IsMeshRequest> meshRequestQuery;
        private readonly ComponentQuery<IsModel> modelQuery;
        private readonly UnmanagedDictionary<Entity, uint> modelVersions;
        private readonly UnmanagedDictionary<Entity, uint> meshVersions;
        private readonly UnmanagedList<Operation> operations;

        readonly unsafe InitializeFunction ISystem.Initialize => new(&Initialize);
        readonly unsafe IterateFunction ISystem.Update => new(&Update);
        readonly unsafe FinalizeFunction ISystem.Finalize => new(&Finalize);

        [UnmanagedCallersOnly]
        private static void Initialize(SystemContainer container, World world)
        {
        }

        [UnmanagedCallersOnly]
        private static void Update(SystemContainer container, World world, TimeSpan delta)
        {
            ref ModelImportSystem system = ref container.Read<ModelImportSystem>();
            system.Update(world);
        }

        [UnmanagedCallersOnly]
        private static void Finalize(SystemContainer container, World world)
        {
            if (container.World == world)
            {
                ref ModelImportSystem system = ref container.Read<ModelImportSystem>();
                system.CleanUp();
            }
        }

        public ModelImportSystem()
        {
            library = new();
            modelQuery = new();
            meshRequestQuery = new();
            modelRequestQuery = new();
            modelVersions = new();
            meshVersions = new();
            operations = new();
        }

        private void CleanUp()
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                operation.Dispose();
            }

            operations.Dispose();
            meshVersions.Dispose();
            modelVersions.Dispose();
            modelRequestQuery.Dispose();
            meshRequestQuery.Dispose();
            modelQuery.Dispose();
            library.Dispose();
        }

        private void Update(World world)
        {
            UpdateModels(world);
            PerformOperations(world);
            UpdateMeshes(world);
            PerformOperations(world);
        }

        private void UpdateModels(World world)
        {
            modelRequestQuery.Update(world);
            foreach (var x in modelRequestQuery)
            {
                IsModelRequest request = x.Component1;
                bool sourceChanged = false;
                Entity model = new(world, x.entity);
                if (!modelVersions.ContainsKey(model))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = modelVersions[model] != request.version;
                }

                if (sourceChanged)
                {
                    //ThreadPool.QueueUserWorkItem(UpdateMeshReferencesOnModelEntity, modelEntity, false);
                    if (TryFinishModelRequest(model))
                    {
                        modelVersions.AddOrSet(model, request.version);
                    }
                }
            }
        }

        private void UpdateMeshes(World world)
        {
            meshRequestQuery.Update(world);
            foreach (var x in meshRequestQuery)
            {
                IsMeshRequest request = x.Component1;
                bool sourceChanged = false;
                //uint meshEntity = x.entity;
                Entity mesh = new(world, x.entity);
                if (!meshVersions.ContainsKey(mesh))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = meshVersions[mesh] != request.version;
                }

                if (sourceChanged)
                {
                    //ThreadPool.QueueUserWorkItem(UpdateMesh, (meshEntity, request), false);
                    if (TryFinishMeshRequest((mesh, request)))
                    {
                        meshVersions.AddOrSet(mesh, request.version);
                    }
                }
            }
        }

        private void PerformOperations(World world)
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private bool TryFinishMeshRequest((Entity mesh, IsMeshRequest request) input)
        {
            Entity mesh = input.mesh;
            World world = mesh.GetWorld();
            uint index = input.request.meshIndex;
            rint modelReference = input.request.modelReference;
            uint modelEntity = mesh.GetReference(modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new(world, modelEntity);
            var existingMesh = model[index];
            Entity existingMeshEntity = existingMesh.entity;
            Operation operation = new();
            operation.SelectEntity(mesh);

            if (mesh.TryGetComponent(out IsMesh component))
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

            operations.Add(operation);
            return true;
        }

        private bool TryFinishModelRequest(Entity model)
        {
            //wait for byte data to be available
            if (!model.ContainsArray<byte>())
            {
                Debug.WriteLine($"Model data not available on entity `{model}`, waiting");
                return false;
            }

            Operation operation = new();
            USpan<byte> byteData = model.GetArray<byte>();
            ImportModel(model, ref operation, byteData);

            operation.ClearSelection();
            operation.SelectEntity(model);
            if (model.TryGetComponent(out IsModel component))
            {
                component.version++;
                operation.SetComponent(component);
            }
            else
            {
                operation.AddComponent(new IsModel());
            }

            operations.Add(operation);
            return true;
        }

        private unsafe uint ImportModel(Entity model, ref Operation operation, USpan<byte> bytes)
        {
            World world = model.GetWorld();
            Scene scene = library.ImportModel(bytes);
            bool containsMeshes = model.ContainsArray<ModelMesh>();
            uint existingMeshCount = containsMeshes ? model.GetArrayLength<ModelMesh>() : 0;
            operation.SelectEntity(model);
            uint referenceCount = model.GetReferenceCount();
            using UnmanagedList<ModelMesh> meshes = new();
            ProcessNode(scene.RootNode, scene, ref operation);
            operation.SelectEntity(model);
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
                Entity existingMesh = default;
                ModelMesh modelMesh;
                if (meshReused)
                {
                    //reset existing mesh
                    rint existingMeshReference = model.GetArrayElementRef<ModelMesh>(meshIndex).value;
                    existingMesh = new(world, model.GetReference(existingMeshReference));
                    operation.SelectEntity(existingMesh);
                    operation.SetComponent(new Name(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.ClearSelection();
                    operation.CreateEntity();
                    operation.SetParent(model);
                    operation.CreateArray<uint>(0);
                    operation.AddComponent(new Name(name));

                    //reference the created mesh
                    operation.ClearSelection();
                    operation.SelectEntity(model);
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
                    for (uint f = 0; f < faceCount; f++)
                    {
                        Face face = mesh.Faces[(int)f];
                        for (uint i = 0; i < face.IndexCount; i++)
                        {
                            uint index = (uint)face.Indices[(int)i];
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
                    if (existingMesh.TryGetComponent(out IsMesh component))
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