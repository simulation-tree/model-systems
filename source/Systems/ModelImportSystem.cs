using Collections;
using Data.Messages;
using Meshes;
using Meshes.Components;
using Models.Components;
using OpenAssetImporter;
using Simulation;
using System;
using System.Diagnostics;
using System.Numerics;
using Unmanaged;
using Worlds;

namespace Models.Systems
{
    public readonly partial struct ModelImportSystem : ISystem
    {
        private readonly Dictionary<Entity, uint> modelVersions;
        private readonly Dictionary<Entity, uint> meshVersions;
        private readonly Stack<Operation> operations;

        private ModelImportSystem(Dictionary<Entity, uint> modelVersions, Dictionary<Entity, uint> meshVersions, Stack<Operation> operations)
        {
            this.modelVersions = modelVersions;
            this.meshVersions = meshVersions;
            this.operations = operations;
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                Dictionary<Entity, uint> modelVersions = new();
                Dictionary<Entity, uint> meshVersions = new();
                Stack<Operation> operations = new();
                systemContainer.Write(new ModelImportSystem(modelVersions, meshVersions, operations));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            USpan<byte> extensionBuffer = stackalloc byte[8];
            Simulator simulator = systemContainer.simulator;
            ComponentType componentType = world.Schema.GetComponent<IsModelRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.Contains(componentType))
                {
                    USpan<uint> entities = chunk.Entities;
                    USpan<IsModelRequest> components = chunk.GetComponents<IsModelRequest>(componentType);
                    for (uint i = 0; i < entities.Length; i++)
                    {
                        ref IsModelRequest request = ref components[i];
                        Entity model = new(world, entities[i]);
                        if (request.status == IsModelRequest.Status.Submitted)
                        {
                            request.status = IsModelRequest.Status.Loading;
                            Trace.WriteLine($"Started searching data for model `{model}` with address `{request.address}`");
                        }

                        if (request.status == IsModelRequest.Status.Loading)
                        {
                            uint length = request.CopyExtensionBytes(extensionBuffer);
                            FixedString extension = new(extensionBuffer.Slice(0, length));
                            IsModelRequest dataRequest = request;
                            if (TryLoadModel(model, dataRequest, simulator))
                            {
                                Trace.WriteLine($"Model `{model}` has been loaded");

                                //todo: being done this way because reference to the request may have shifted
                                model.SetComponent(dataRequest.BecomeLoaded());
                            }
                            else
                            {
                                request.duration += delta;
                                if (request.duration >= request.timeout)
                                {
                                    Trace.TraceError($"Model `{model}` could not be loaded");
                                    request.status = IsModelRequest.Status.NotFound;
                                }
                            }
                        }
                    }
                }
            }

            PerformOperations(world);

            ComponentType meshComponent = world.Schema.GetComponent<IsMeshRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.Contains(meshComponent))
                {
                    USpan<uint> entities = chunk.Entities;
                    USpan<IsMeshRequest> components = chunk.GetComponents<IsMeshRequest>(meshComponent);
                    for (uint i = 0; i < entities.Length; i++)
                    {
                        ref IsMeshRequest request = ref components[i];
                        Entity mesh = new(world, entities[i]);
                        if (!request.loaded)
                        {
                            if (TryLoadMesh(mesh, request))
                            {
                                request.loaded = true;
                                meshVersions.AddOrSet(mesh, request.version);
                            }
                        }
                        else
                        {
                            bool versionChanged;
                            if (!meshVersions.ContainsKey(mesh))
                            {
                                versionChanged = true;
                            }
                            else
                            {
                                versionChanged = meshVersions[mesh] != request.version;
                            }

                            if (versionChanged)
                            {
                                if (TryLoadMesh(mesh, request))
                                {
                                    request.loaded = true;
                                    meshVersions.AddOrSet(mesh, request.version);
                                }
                            }
                        }
                    }
                }
            }

            PerformOperations(world);
        }

        void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                while (operations.TryPop(out Operation operation))
                {
                    operation.Dispose();
                }

                operations.Dispose();
                meshVersions.Dispose();
                modelVersions.Dispose();
            }
        }

        private readonly void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private readonly bool TryLoadMesh(Entity mesh, IsMeshRequest request)
        {
            World world = mesh.world;
            uint index = request.meshIndex;
            rint modelReference = request.modelReference;
            uint modelEntity = mesh.GetReference(modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new Entity(world, modelEntity).As<Model>();
            Meshes.Mesh sourceMesh = model[index];
            Schema schema = world.Schema;
            Operation operation = new();
            Operation.SelectedEntity selectedMesh = operation.SelectEntity(mesh);
            if (mesh.TryGetComponent(out IsMesh component))
            {
                selectedMesh.SetComponent(component.IncrementVersion(), schema);
                ModelName modelName = sourceMesh.GetComponent<ModelName>();
                if (mesh.ContainsComponent<ModelName>())
                {
                    selectedMesh.SetComponent(modelName, schema);
                }
                else
                {
                    selectedMesh.AddComponent(modelName, schema);
                }
            }
            else
            {
                //become a mesh now!
                selectedMesh.AddComponent(new IsMesh(), schema);
                selectedMesh.CreateArray<MeshVertexIndex>(0, schema);

                if (sourceMesh.ContainsPositions)
                {
                    selectedMesh.CreateArray<MeshVertexPosition>(0, schema);
                }

                if (sourceMesh.ContainsUVs)
                {
                    selectedMesh.CreateArray<MeshVertexUV>(0, schema);
                }

                if (sourceMesh.ContainsNormals)
                {
                    selectedMesh.CreateArray<MeshVertexNormal>(0, schema);
                }

                if (sourceMesh.ContainsTangents)
                {
                    selectedMesh.CreateArray<MeshVertexTangent>(0, schema);
                }

                if (sourceMesh.ContainsBiTangents)
                {
                    selectedMesh.CreateArray<MeshVertexBiTangent>(0, schema);
                }

                if (sourceMesh.ContainsColors)
                {
                    selectedMesh.CreateArray<MeshVertexColor>(0, schema);
                }
            }

            //copy each channel
            if (sourceMesh.ContainsPositions)
            {
                USpan<MeshVertexPosition> positions = sourceMesh.GetArray<MeshVertexPosition>();
                USpan<MeshVertexIndex> indices = sourceMesh.GetArray<MeshVertexIndex>();
                selectedMesh.ResizeArray<MeshVertexIndex>(indices.Length, schema);
                selectedMesh.SetArrayElements(0, indices, schema);
                selectedMesh.ResizeArray<MeshVertexPosition>(positions.Length, schema);
                selectedMesh.SetArrayElements(0, positions, schema);
            }

            if (sourceMesh.ContainsUVs)
            {
                USpan<MeshVertexUV> uvs = sourceMesh.GetArray<MeshVertexUV>();
                selectedMesh.ResizeArray<MeshVertexUV>(uvs.Length, schema);
                selectedMesh.SetArrayElements(0, uvs, schema);
            }

            if (sourceMesh.ContainsNormals)
            {
                USpan<MeshVertexNormal> normals = sourceMesh.GetArray<MeshVertexNormal>();
                selectedMesh.ResizeArray<MeshVertexNormal>(normals.Length, schema);
                selectedMesh.SetArrayElements(0, normals, schema);
            }

            if (sourceMesh.ContainsTangents)
            {
                USpan<MeshVertexTangent> tangents = sourceMesh.GetArray<MeshVertexTangent>();
                selectedMesh.ResizeArray<MeshVertexTangent>(tangents.Length, schema);
                selectedMesh.SetArrayElements(0, tangents, schema);
            }

            if (sourceMesh.ContainsBiTangents)
            {
                USpan<MeshVertexBiTangent> bitangents = sourceMesh.GetArray<MeshVertexBiTangent>();
                selectedMesh.ResizeArray<MeshVertexBiTangent>(bitangents.Length, schema);
                selectedMesh.SetArrayElements(0, bitangents, schema);
            }

            if (sourceMesh.ContainsColors)
            {
                USpan<MeshVertexColor> colors = sourceMesh.GetArray<MeshVertexColor>();
                selectedMesh.ResizeArray<MeshVertexColor>(colors.Length, schema);
                selectedMesh.SetArrayElements(0, colors, schema);
            }

            operations.Push(operation);
            return true;
        }

        private bool TryLoadModel(Entity model, IsModelRequest request, Simulator simulator)
        {
            HandleDataRequest message = new(model, request.address);
            if (simulator.TryHandleMessage(ref message))
            {
                if (message.loaded)
                {
                    Schema schema = model.world.Schema;
                    Operation operation = new();
                    USpan<byte> byteData = message.Bytes;
                    ImportModel(model, operation, byteData, request.Extension);

                    operation.ClearSelection();
                    Operation.SelectedEntity selectedEntity = operation.SelectEntity(model);
                    ref IsModel component = ref model.TryGetComponent<IsModel>(out bool contains);
                    if (contains)
                    {
                        selectedEntity.SetComponent(new IsModel(component.version + 1), schema);
                    }
                    else
                    {
                        selectedEntity.AddComponent(new IsModel(), schema);
                    }

                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }

        private readonly unsafe uint ImportModel(Entity model, Operation operation, USpan<byte> bytes, FixedString extension)
        {
            World world = model.world;
            Schema schema = world.Schema;
            USpan<char> extensionSpan = stackalloc char[extension.Length];
            extension.CopyTo(extensionSpan);
            using Scene scene = new(bytes.As<byte>(), extensionSpan, PostProcessSteps.Triangulate);
            bool containsMeshes = model.ContainsArray<ModelMesh>();
            uint existingMeshCount = containsMeshes ? model.GetArrayLength<ModelMesh>() : 0;
            operation.SelectEntity(model);
            uint referenceCount = model.References;
            using List<ModelMesh> meshes = new();
            ProcessNode(scene.RootNode, scene, operation);
            operation.SelectEntity(model);
            if (containsMeshes)
            {
                operation.ResizeArray<ModelMesh>(meshes.Count, schema);
                operation.SetArrayElements(0, meshes.AsSpan(), schema);
            }
            else
            {
                operation.CreateArray(meshes.AsSpan(), schema);
            }

            return meshes.Count;

            void ProcessNode(Node node, Scene scene, Operation operation)
            {
                for (int i = 0; i < node.Meshes.Length; i++)
                {
                    OpenAssetImporter.Mesh mesh = scene.Meshes[node.Meshes[i]];
                    ProcessMesh(mesh, scene, operation, meshes);
                }

                for (int i = 0; i < node.Children.Length; i++)
                {
                    Node child = node.Children[i];
                    ProcessNode(child, scene, operation);
                }
            }

            void ProcessMesh(OpenAssetImporter.Mesh mesh, Scene scene, Operation operation, List<ModelMesh> meshes)
            {
                uint vertexCount = (uint)mesh.VertexCount;
                uint faceCount = (uint)mesh.FaceCount;
                Vector3* positions = GetPointer<Vector3>(mesh.Vertices);
                Vector3* uvs = GetPointer<Vector3>(mesh.GetTextureCoordinates(0));
                Vector3* normals = GetPointer<Vector3>(mesh.Normals);
                Vector3* tangents = GetPointer<Vector3>(mesh.Tangents);
                Vector3* biTangents = GetPointer<Vector3>(mesh.BiTangents);
                Vector4* colors = GetPointer<Vector4>(mesh.GetColors(0));

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
                    rint existingMeshReference = model.GetArrayElement<ModelMesh>(meshIndex).value;
                    existingMesh = new(world, model.GetReference(existingMeshReference));
                    operation.SelectEntity(existingMesh);
                    operation.SetComponent(new ModelName(name), schema);
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.CreateEntity();
                    operation.SetParent(model);
                    operation.CreateArray<MeshVertexIndex>(0, schema);
                    operation.AddComponent(new ModelName(name), schema);

                    //reference the created mesh
                    operation.ClearSelection();
                    operation.SelectEntity(model);
                    operation.AddReferenceTowardsPreviouslyCreatedEntity(0);
                    rint newReference = (rint)(referenceCount + meshIndex + 1);
                    modelMesh = new(newReference);

                    //select the created mesh again
                    operation.ClearSelection();
                    operation.SelectPreviouslyCreatedEntity(0);

                    if (positions is not null)
                    {
                        operation.CreateArray<MeshVertexPosition>(0, schema);
                    }

                    if (uvs is not null)
                    {
                        operation.CreateArray<MeshVertexUV>(0, schema);
                    }

                    if (normals is not null)
                    {
                        operation.CreateArray<MeshVertexNormal>(0, schema);
                    }

                    if (tangents is not null)
                    {
                        operation.CreateArray<MeshVertexTangent>(0, schema);
                    }

                    if (biTangents is not null)
                    {
                        operation.CreateArray<MeshVertexBiTangent>(0, schema);
                    }

                    if (colors is not null)
                    {
                        operation.CreateArray<MeshVertexColor>(0, schema);
                    }
                }

                meshes.Add(modelMesh);

                //fill in data
                if (positions is not null)
                {
                    using Array<MeshVertexPosition> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = positions[i];
                    }

                    operation.ResizeArray<MeshVertexPosition>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);

                    using List<MeshVertexIndex> indices = new();
                    for (uint f = 0; f < faceCount; f++)
                    {
                        Face face = mesh.Faces[(int)f];
                        for (uint i = 0; i < face.Indices.Length; i++)
                        {
                            uint index = (uint)face.Indices[(int)i];
                            indices.Add(index);
                        }
                    }

                    operation.ResizeArray<MeshVertexIndex>(indices.Count, schema);
                    operation.SetArrayElements(0, indices.AsSpan(), schema);
                }

                if (uvs is not null)
                {
                    using Array<MeshVertexUV> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        Vector3 raw = uvs[i];
                        tempData[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.ResizeArray<MeshVertexUV>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);
                }

                if (normals is not null)
                {
                    using Array<MeshVertexNormal> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(normals[i]);
                    }

                    operation.ResizeArray<MeshVertexNormal>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);
                }

                if (tangents is not null)
                {
                    using Array<MeshVertexTangent> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(tangents[i]);
                    }

                    operation.ResizeArray<MeshVertexTangent>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);
                }

                if (biTangents is not null)
                {
                    using Array<MeshVertexBiTangent> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(biTangents[i]);
                    }

                    operation.ResizeArray<MeshVertexBiTangent>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);
                }

                if (colors is not null)
                {
                    using Array<MeshVertexColor> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(colors[i]);
                    }

                    operation.ResizeArray<MeshVertexColor>(vertexCount, schema);
                    operation.SetArrayElements(0, tempData.AsSpan(), schema);
                }

                //Material? material = scene.MaterialCount > 0 ? scene.Materials[0] : null;
                //if (material is not null)
                //{
                //    //todo: handle materials
                //}

                //increment mesh version
                if (meshReused)
                {
                    if (existingMesh.TryGetComponent(out IsMesh component))
                    {
                        operation.SetComponent(component.IncrementVersion(), schema);
                    }
                    else
                    {
                        operation.AddComponent(new IsMesh(), schema);
                    }
                }
                else
                {
                    operation.AddComponent(new IsMesh(), schema);
                }
            }
        }

        private unsafe static T* GetPointer<T>(USpan<T> span) where T : unmanaged
        {
            return span.Pointer;
        }
    }
}