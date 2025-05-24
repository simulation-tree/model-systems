using Collections.Generic;
using Data.Messages;
using Meshes;
using Meshes.Components;
using Models.Components;
using OpenAssetImporter;
using Simulation;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Unmanaged;
using Worlds;

namespace Models.Systems
{
    [SkipLocalsInit]
    public partial class ModelImportSystem : ISystem, IDisposable
    {
        private readonly Dictionary<uint, uint> modelVersions;
        private readonly Dictionary<uint, uint> meshVersions;
        private readonly Operation operation;
        private readonly int modelRequestType;
        private readonly int meshRequestType;
        private readonly int modelType;
        private readonly int meshType;
        private readonly int modelNameType;
        private readonly int modelMeshArrayType;

        public ModelImportSystem(Simulator simulator)
        {
            modelVersions = new(4);
            meshVersions = new(4);
            operation = new();

            Schema schema = simulator.world.Schema;
            modelRequestType = schema.GetComponentType<IsModelRequest>();
            meshRequestType = schema.GetComponentType<IsMeshRequest>();
            modelType = schema.GetComponentType<IsModel>();
            meshType = schema.GetComponentType<IsMesh>();
            modelNameType = schema.GetComponentType<ModelName>();
            modelMeshArrayType = schema.GetArrayType<ModelMesh>();
        }

        public void Dispose()
        {
            operation.Dispose();
            meshVersions.Dispose();
            modelVersions.Dispose();
        }

        void ISystem.Update(Simulator simulator, double deltaTime)
        {
            World world = simulator.world;
            Span<byte> extensionBuffer = stackalloc byte[8];
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.ContainsComponent(modelRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsModelRequest> components = chunk.GetComponents<IsModelRequest>(modelRequestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsModelRequest request = ref components[i];
                        uint model = entities[i];
                        if (request.status == IsModelRequest.Status.Submitted)
                        {
                            request.status = IsModelRequest.Status.Loading;
                            Trace.WriteLine($"Started searching data for model `{model}` with address `{request.address}`");
                        }

                        if (request.status == IsModelRequest.Status.Loading)
                        {
                            int length = request.CopyExtensionBytes(extensionBuffer);
                            ASCIIText256 extension = new(extensionBuffer.Slice(0, length));
                            IsModelRequest dataRequest = request;
                            if (TryLoadModel(world, model, dataRequest, simulator))
                            {
                                Trace.WriteLine($"Model `{model}` has been loaded");
                                request.status = IsModelRequest.Status.Loaded;
                            }
                            else
                            {
                                request.duration += deltaTime;
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

            if (operation.Count > 0)
            {
                operation.Perform(world);
                operation.Reset();
            }

            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.ContainsComponent(meshRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsMeshRequest> components = chunk.GetComponents<IsMeshRequest>(meshRequestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsMeshRequest request = ref components[i];
                        uint mesh = entities[i];
                        if (!request.loaded)
                        {
                            if (TryLoadMesh(world, mesh, request))
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
                                if (TryLoadMesh(world, mesh, request))
                                {
                                    request.loaded = true;
                                    meshVersions.AddOrSet(mesh, request.version);
                                }
                            }
                        }
                    }
                }
            }

            if (operation.Count > 0)
            {
                operation.Perform(world);
                operation.Reset();
            }
        }

        private bool TryLoadMesh(World world, uint loadingMesh, IsMeshRequest request)
        {
            int index = request.meshIndex;
            rint modelReference = request.modelReference;
            uint modelEntity = world.GetReference(loadingMesh, modelReference);

            //wait for model data to load
            if (!world.ContainsComponent(modelEntity, modelType))
            {
                return false;
            }

            Model model = Entity.Get<Model>(world, modelEntity);
            Meshes.Mesh sourceMesh = model[index];
            IsMesh sourceMeshComponent = sourceMesh.GetComponent<IsMesh>(meshType);
            operation.SetSelectedEntity(loadingMesh);
            world.TryGetComponent(loadingMesh, meshType, out IsMesh component);
            ModelName modelName = sourceMesh.GetComponent<ModelName>(modelNameType);
            operation.AddOrSetComponent(modelName);
            component.version++;
            component.channels = sourceMeshComponent.channels;
            component.vertexCount = sourceMeshComponent.vertexCount;
            component.indexCount = sourceMeshComponent.indexCount;
            operation.AddOrSetComponent(component);

            //copy each channel
            if (sourceMesh.ContainsPositions)
            {
                Span<MeshVertexPosition> positions = sourceMesh.GetArray<MeshVertexPosition>();
                Span<MeshVertexIndex> indices = sourceMesh.GetArray<MeshVertexIndex>();
                operation.CreateOrSetArray(indices);
                operation.CreateOrSetArray(positions);
            }

            if (sourceMesh.ContainsUVs)
            {
                Span<MeshVertexUV> uvs = sourceMesh.GetArray<MeshVertexUV>();
                operation.CreateOrSetArray(uvs);
            }

            if (sourceMesh.ContainsNormals)
            {
                Span<MeshVertexNormal> normals = sourceMesh.GetArray<MeshVertexNormal>();
                operation.CreateOrSetArray(normals);
            }

            if (sourceMesh.ContainsTangents)
            {
                Span<MeshVertexTangent> tangents = sourceMesh.GetArray<MeshVertexTangent>();
                operation.CreateOrSetArray(tangents);
            }

            if (sourceMesh.ContainsBiTangents)
            {
                Span<MeshVertexBiTangent> biTangents = sourceMesh.GetArray<MeshVertexBiTangent>();
                operation.CreateOrSetArray(biTangents);
            }

            if (sourceMesh.ContainsColors)
            {
                Span<MeshVertexColor> colors = sourceMesh.GetArray<MeshVertexColor>();
                operation.CreateOrSetArray(colors);
            }

            return true;
        }

        private bool TryLoadModel(World world, uint model, IsModelRequest request, Simulator simulator)
        {
            LoadData message = new(world, request.address);
            simulator.Broadcast(ref message);
            if (message.TryConsume(out ByteReader data))
            {
                ImportModel(world, model, data, request.extension);
                data.Dispose();

                operation.SetSelectedEntity(model);
                world.TryGetComponent(model, modelType, out IsModel component);
                operation.AddOrSetComponent(component.IncrementVersion());
                return true;
            }

            return false;
        }

        private unsafe int ImportModel(World world, uint entity, ByteReader bytes, ASCIIText8 extension)
        {
            Entity model = new(world, entity);
            Span<char> extensionSpan = stackalloc char[extension.Length];
            extension.CopyTo(extensionSpan);
            using Scene scene = new(bytes.GetBytes(), extensionSpan, PostProcessSteps.Triangulate);
            bool containsMeshes = model.ContainsArray<ModelMesh>();
            int existingMeshCount = containsMeshes ? model.GetArrayLength<ModelMesh>() : 0;
            operation.SetSelectedEntity(model);
            int referenceCount = model.ReferenceCount;
            using List<ModelMesh> meshes = new();
            ProcessNode(scene.RootNode, scene);
            operation.SetSelectedEntity(model);
            operation.CreateOrSetArray(meshes.AsSpan());
            return meshes.Count;

            void ProcessNode(Node node, Scene scene)
            {
                for (int i = 0; i < node.Meshes.Length; i++)
                {
                    OpenAssetImporter.Mesh mesh = scene.Meshes[node.Meshes[i]];
                    ProcessMesh(mesh, scene, meshes);
                }

                for (int i = 0; i < node.Children.Length; i++)
                {
                    Node child = node.Children[i];
                    ProcessNode(child, scene);
                }
            }

            void ProcessMesh(OpenAssetImporter.Mesh mesh, Scene scene, List<ModelMesh> meshes)
            {
                int vertexCount = mesh.VertexCount;
                int faceCount = mesh.FaceCount;
                ReadOnlySpan<Vector3> positions = mesh.HasVertices ? mesh.Vertices : default;
                ReadOnlySpan<Vector3> uvs = mesh.GetTextureCoordinates(0);
                ReadOnlySpan<Vector3> normals = mesh.HasNormals ? mesh.Normals : default;
                ReadOnlySpan<Vector3> tangents = mesh.HasTangents ? mesh.Tangents : default;
                ReadOnlySpan<Vector3> biTangents = mesh.HasBiTangents ? mesh.BiTangents : default;
                ReadOnlySpan<Vector4> colors = mesh.GetColors(0);
                MeshChannelMask channels = default;

                if (uvs.GetPointer() == default)
                {
                    uvs = default;
                }

                if (colors.GetPointer() == default)
                {
                    colors = default;
                }

                //todo: accuracy: should reuse based on mesh name rather than index within the list, because the amount of meshes
                //in the source asset could change, and could possibly shift around in order
                int meshIndex = meshes.Count;
                string name = mesh.Name;
                bool meshReused = meshIndex < existingMeshCount;
                Entity existingMesh = default;
                ModelMesh modelMesh;
                if (meshReused)
                {
                    //reset existing mesh
                    rint existingMeshReference = model.GetArrayElement<ModelMesh>(modelMeshArrayType, meshIndex).value;
                    existingMesh = new(world, model.GetReference(existingMeshReference));
                    operation.SetSelectedEntity(existingMesh);
                    operation.SetComponent(new ModelName(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.ClearSelection();
                    operation.CreateEntityAndSelect();
                    operation.SetParent(model);
                    operation.CreateArray<MeshVertexIndex>();
                    operation.AddComponent(new ModelName(name));

                    //reference the created mesh
                    operation.SetSelectedEntity(model);
                    operation.AddReferenceTowardsPreviouslyCreatedEntity(0);
                    rint newReference = (rint)(referenceCount + meshIndex + 1);
                    modelMesh = new(newReference);

                    //select the created mesh again
                    operation.ClearSelection();
                    operation.SelectPreviouslyCreatedEntity(0);
                }

                meshes.Add(modelMesh);

                using List<MeshVertexIndex> indices = new(faceCount * 3);
                for (int f = 0; f < faceCount; f++)
                {
                    Face face = mesh.Faces[f];
                    for (int i = 0; i < face.Indices.Length; i++)
                    {
                        uint index = (uint)face.Indices[i];
                        indices.Add(index);
                    }
                }

                operation.CreateOrSetArray(indices.AsSpan());

                //fill in data
                if (!positions.IsEmpty)
                {
                    operation.CreateOrSetArray(positions.As<Vector3, MeshVertexPosition>());
                    channels |= MeshChannelMask.Positions;
                }

                if (!uvs.IsEmpty)
                {
                    using Array<MeshVertexUV> uvs2d = new(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                    {
                        Vector3 raw = uvs[i];
                        uvs2d[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.CreateOrSetArray(uvs2d.AsSpan());
                    channels |= MeshChannelMask.UVs;
                }

                if (!normals.IsEmpty)
                {
                    operation.CreateOrSetArray(normals.As<Vector3, MeshVertexNormal>());
                    channels |= MeshChannelMask.Normals;
                }

                if (!tangents.IsEmpty)
                {
                    operation.CreateOrSetArray(tangents.As<Vector3, MeshVertexTangent>());
                    channels |= MeshChannelMask.Tangents;
                }

                if (!biTangents.IsEmpty)
                {
                    operation.CreateOrSetArray(biTangents.As<Vector3, MeshVertexBiTangent>());
                    channels |= MeshChannelMask.BiTangents;
                }

                if (!colors.IsEmpty)
                {
                    operation.CreateOrSetArray(colors.As<Vector4, MeshVertexColor>());
                    channels |= MeshChannelMask.Colors;
                }

                //Material? material = scene.MaterialCount > 0 ? scene.Materials[0] : null;
                //if (material is not null)
                //{
                //    //todo: handle materials
                //}

                //increment mesh version
                if (existingMesh != default)
                {
                    existingMesh.TryGetComponent(meshType, out IsMesh component);
                    component.version++;
                    component.channels = channels;
                    component.vertexCount = vertexCount;
                    component.indexCount = indices.Count;
                    operation.AddOrSetComponent(component);
                }
                else
                {
                    operation.AddComponent(new IsMesh(0, channels, vertexCount, indices.Count));
                }
            }
        }
    }
}