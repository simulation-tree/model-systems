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
    public partial class ModelImportSystem : SystemBase, IListener<DataUpdate>
    {
        private readonly World world;
        private readonly Dictionary<uint, uint> modelVersions;
        private readonly Dictionary<uint, uint> meshVersions;
        private readonly Operation operation;
        private readonly int modelRequestType;
        private readonly int meshRequestType;
        private readonly int modelType;
        private readonly int meshType;
        private readonly int modelNameType;
        private readonly int modelMeshArrayType;

        public ModelImportSystem(Simulator simulator, World world) : base(simulator)
        {
            this.world = world;
            modelVersions = new(4);
            meshVersions = new(4);
            operation = new(world);

            Schema schema = world.Schema;
            modelRequestType = schema.GetComponentType<IsModelRequest>();
            meshRequestType = schema.GetComponentType<IsMeshRequest>();
            modelType = schema.GetComponentType<IsModel>();
            meshType = schema.GetComponentType<IsMesh>();
            modelNameType = schema.GetComponentType<ModelName>();
            modelMeshArrayType = schema.GetArrayType<ModelMesh>();
        }

        public override void Dispose()
        {
            operation.Dispose();
            meshVersions.Dispose();
            modelVersions.Dispose();
        }

        void IListener<DataUpdate>.Receive(ref DataUpdate message)
        {
            Span<byte> extensionBuffer = stackalloc byte[8];
            ReadOnlySpan<Chunk> chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.componentTypes.Contains(modelRequestType))
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
                            if (TryLoadModel(model, request))
                            {
                                Trace.WriteLine($"Model `{model}` has been loaded");
                                request.status = IsModelRequest.Status.Loaded;
                            }
                            else
                            {
                                request.duration += message.deltaTime;
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

            if (operation.TryPerform())
            {
                operation.Reset();
            }

            chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
                if (chunk.componentTypes.Contains(meshRequestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsMeshRequest> components = chunk.GetComponents<IsMeshRequest>(meshRequestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsMeshRequest request = ref components[i];
                        uint mesh = entities[i];
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

            if (operation.TryPerform())
            {
                operation.Reset();
            }
        }

        private bool TryLoadMesh(uint meshEntity, IsMeshRequest request)
        {
            int index = request.meshIndex;
            rint modelReference = request.modelReference;
            uint modelEntity = world.GetReference(meshEntity, modelReference);

            //wait for model data to load
            if (!world.ContainsComponent(modelEntity, modelType))
            {
                return false;
            }

            Model model = Entity.Get<Model>(world, modelEntity);
            Meshes.Mesh sourceMesh = model[index];
            IsMesh sourceMeshComponent = sourceMesh.GetComponent<IsMesh>(meshType);
            operation.SetSelectedEntity(meshEntity);
            world.TryGetComponent(meshEntity, meshType, out IsMesh mesh);
            ModelName modelName = sourceMesh.GetComponent<ModelName>(modelNameType);
            operation.AddOrSetComponent(modelName, modelNameType);
            mesh.version++;
            mesh.channels = sourceMeshComponent.channels;
            mesh.vertexCount = sourceMeshComponent.vertexCount;
            mesh.indexCount = sourceMeshComponent.indexCount;
            operation.AddOrSetComponent(mesh, meshType);

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

        private bool TryLoadModel(uint modelEntity, IsModelRequest request)
        {
            LoadData message = new(request.address);
            simulator.Broadcast(ref message);
            if (message.TryConsume(out ByteReader data))
            {
                ImportModel(modelEntity, data, request.extension);
                data.Dispose();

                operation.SetSelectedEntity(modelEntity);
                world.TryGetComponent(modelEntity, modelType, out IsModel component);
                component.version++;
                operation.AddOrSetComponent(component, modelType);
                return true;
            }

            return false;
        }

        private unsafe int ImportModel(uint modelEntity, ByteReader bytes, ASCIIText8 extension)
        {
            Span<char> extensionSpan = stackalloc char[extension.Length];
            extension.CopyTo(extensionSpan);
            using Scene scene = new(bytes.GetBytes(), extensionSpan, PostProcessSteps.Triangulate);
            bool containsMeshes = world.ContainsArray(modelEntity, modelMeshArrayType);
            int existingMeshCount = containsMeshes ? world.GetArrayLength(modelEntity, modelMeshArrayType) : 0;
            operation.SetSelectedEntity(modelEntity);
            int referenceCount = world.GetReferenceCount(modelEntity);
            using List<ModelMesh> meshes = new();
            
            ProcessNode(scene.RootNode, scene);

            operation.SetSelectedEntity(modelEntity);
            operation.CreateOrSetArray(meshes.AsSpan(), modelMeshArrayType);
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

            void ProcessMesh(OpenAssetImporter.Mesh loadedMesh, Scene scene, List<ModelMesh> meshes)
            {
                int vertexCount = loadedMesh.VertexCount;
                int faceCount = loadedMesh.FaceCount;
                ReadOnlySpan<Vector3> positions = loadedMesh.HasVertices ? loadedMesh.Vertices : default;
                ReadOnlySpan<Vector3> uvs = loadedMesh.GetTextureCoordinates(0);
                ReadOnlySpan<Vector3> normals = loadedMesh.HasNormals ? loadedMesh.Normals : default;
                ReadOnlySpan<Vector3> tangents = loadedMesh.HasTangents ? loadedMesh.Tangents : default;
                ReadOnlySpan<Vector3> biTangents = loadedMesh.HasBiTangents ? loadedMesh.BiTangents : default;
                ReadOnlySpan<Vector4> colors = loadedMesh.GetColors(0);
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
                string name = loadedMesh.Name;
                bool meshReused = meshIndex < existingMeshCount;
                Entity existingMesh;
                ModelMesh modelMesh;
                if (meshReused)
                {
                    //reset existing mesh
                    rint existingMeshReference = world.GetArrayElement<ModelMesh>(modelEntity, modelMeshArrayType, meshIndex).value;
                    existingMesh = new(world, world.GetReference(modelEntity, existingMeshReference));
                    operation.SetSelectedEntity(existingMesh);
                    operation.SetComponent(new ModelName(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    existingMesh = default;

                    //create new mesh
                    operation.ClearSelection();
                    operation.CreateSingleEntityAndSelect();
                    operation.SetParent(modelEntity);
                    operation.CreateArray<MeshVertexIndex>();
                    operation.AddComponent(new ModelName(name), modelNameType);

                    //reference the created mesh
                    operation.SetSelectedEntity(modelEntity);
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
                    Face face = loadedMesh.Faces[f];
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
                if (meshReused)
                {
                    existingMesh.TryGetComponent(meshType, out IsMesh mesh);
                    mesh.version++;
                    mesh.channels = channels;
                    mesh.vertexCount = vertexCount;
                    mesh.indexCount = indices.Count;
                    operation.AddOrSetComponent(mesh, meshType);
                }
                else
                {
                    operation.AddComponent(new IsMesh(0, channels, vertexCount, indices.Count), meshType);
                }
            }
        }
    }
}