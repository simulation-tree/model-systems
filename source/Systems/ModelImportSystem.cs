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
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private readonly bool TryLoadMesh(Entity loadingMesh, IsMeshRequest request)
        {
            World world = loadingMesh.world;
            uint index = request.meshIndex;
            rint modelReference = request.modelReference;
            uint modelEntity = loadingMesh.GetReference(modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new Entity(world, modelEntity).As<Model>();
            Meshes.Mesh sourceMesh = model[index];
            Schema schema = world.Schema;
            Operation operation = new();
            operation.SelectEntity(loadingMesh);
            loadingMesh.TryGetComponent(out IsMesh component);
            ModelName modelName = sourceMesh.GetComponent<ModelName>();
            operation.AddOrSetComponent(modelName);
            operation.AddOrSetComponent(new IsMesh(component.version + 1));

            //copy each channel
            if (sourceMesh.ContainsPositions)
            {
                USpan<MeshVertexPosition> positions = sourceMesh.GetArray<MeshVertexPosition>();
                USpan<MeshVertexIndex> indices = sourceMesh.GetArray<MeshVertexIndex>();
                operation.CreateOrSetArray(indices);
                operation.CreateOrSetArray(positions);
            }

            if (sourceMesh.ContainsUVs)
            {
                USpan<MeshVertexUV> uvs = sourceMesh.GetArray<MeshVertexUV>();
                operation.CreateOrSetArray(uvs);
            }

            if (sourceMesh.ContainsNormals)
            {
                USpan<MeshVertexNormal> normals = sourceMesh.GetArray<MeshVertexNormal>();
                operation.CreateOrSetArray(normals);
            }

            if (sourceMesh.ContainsTangents)
            {
                USpan<MeshVertexTangent> tangents = sourceMesh.GetArray<MeshVertexTangent>();
                operation.CreateOrSetArray(tangents);
            }

            if (sourceMesh.ContainsBiTangents)
            {
                USpan<MeshVertexBiTangent> biTangents = sourceMesh.GetArray<MeshVertexBiTangent>();
                operation.CreateOrSetArray(biTangents);
            }

            if (sourceMesh.ContainsColors)
            {
                USpan<MeshVertexColor> colors = sourceMesh.GetArray<MeshVertexColor>();
                operation.CreateOrSetArray(colors);
            }

            operations.Push(operation);
            return true;
        }

        private bool TryLoadModel(Entity model, IsModelRequest request, Simulator simulator)
        {
            LoadData message = new(model.world, request.address);
            if (simulator.TryHandleMessage(ref message))
            {
                if (message.IsLoaded)
                {
                    Operation operation = new();
                    USpan<byte> byteData = message.Bytes;
                    ImportModel(model, operation, byteData, request.Extension);
                    message.Dispose();

                    operation.ClearSelection();
                    operation.SelectEntity(model);
                    model.TryGetComponent(out IsModel component);
                    operation.AddOrSetComponent(new IsModel(component.version + 1));

                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }

        private readonly unsafe uint ImportModel(Entity model, Operation operation, USpan<byte> bytes, FixedString extension)
        {
            World world = model.world;
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
            operation.CreateOrSetArray(meshes.AsSpan());
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
                USpan<Vector3> positions = mesh.HasVertices ? mesh.Vertices : default;
                USpan<Vector3> uvs = mesh.GetTextureCoordinates(0);
                USpan<Vector3> normals = mesh.HasNormals ? mesh.Normals : default;
                USpan<Vector3> tangents = mesh.HasTangents ? mesh.Tangents : default;
                USpan<Vector3> biTangents = mesh.HasBiTangents ? mesh.BiTangents : default;
                USpan<Vector4> colors = mesh.GetColors(0);

                if (uvs.Address == default)
                {
                    uvs = default;
                }

                if (colors.Address == default)
                {
                    colors = default;
                }

                operation.ClearSelection();

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
                    operation.SetComponent(new ModelName(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.CreateEntity();
                    operation.SetParent(model);
                    operation.CreateArray<MeshVertexIndex>();
                    operation.AddComponent(new ModelName(name));

                    //reference the created mesh
                    operation.ClearSelection();
                    operation.SelectEntity(model);
                    operation.AddReferenceTowardsPreviouslyCreatedEntity(0);
                    rint newReference = (rint)(referenceCount + meshIndex + 1);
                    modelMesh = new(newReference);

                    //select the created mesh again
                    operation.ClearSelection();
                    operation.SelectPreviouslyCreatedEntity(0);
                }

                meshes.Add(modelMesh);

                //fill in data
                if (!positions.IsEmpty)
                {
                    operation.CreateOrSetArray(positions.As<MeshVertexPosition>());

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

                    operation.CreateOrSetArray(indices.AsSpan());
                }

                if (!uvs.IsEmpty)
                {
                    using Array<MeshVertexUV> uvs2d = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        Vector3 raw = uvs[i];
                        uvs2d[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.CreateOrSetArray(uvs2d.AsSpan());
                }

                if (!normals.IsEmpty)
                {
                    operation.CreateOrSetArray(normals.As<MeshVertexNormal>());
                }

                if (!tangents.IsEmpty)
                {
                    operation.CreateOrSetArray(tangents.As<MeshVertexTangent>());
                }

                if (!biTangents.IsEmpty)
                {
                    operation.CreateOrSetArray(biTangents.As<MeshVertexBiTangent>());
                }

                if (!colors.IsEmpty)
                {
                    operation.CreateOrSetArray(colors.As<MeshVertexColor>());
                }

                //Material? material = scene.MaterialCount > 0 ? scene.Materials[0] : null;
                //if (material is not null)
                //{
                //    //todo: handle materials
                //}

                //increment mesh version
                if (meshReused)
                {
                    existingMesh.TryGetComponent(out IsMesh component);
                    operation.AddOrSetComponent(new IsMesh(component.version + 1));
                }
                else
                {
                    operation.AddComponent(new IsMesh());
                }
            }
        }
    }
}