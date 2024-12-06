using Collections;
using Data.Components;
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
        private readonly List<Operation> operations;

        public ModelImportSystem()
        {
            modelVersions = new();
            meshVersions = new();
            operations = new();
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            USpan<byte> hint = stackalloc byte[8];
            ComponentQuery<IsModelRequest> modelRequestQuery = new(world);
            foreach (var r in modelRequestQuery)
            {
                bool sourceChanged = false;
                ref IsModelRequest request = ref r.component1;
                Entity model = new(world, r.entity);
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
                    uint hintLength = request.CopyExtensionBytes(hint);
                    if (TryLoadModel(model, hint.Slice(0, hintLength)))
                    {
                        modelVersions.AddOrSet(model, request.version);
                    }
                }
            }

            PerformOperations(world);

            ComponentQuery<IsMeshRequest> meshRequestQuery = new(world);
            foreach (var r in meshRequestQuery)
            {
                ref IsMeshRequest request = ref r.component1;
                bool sourceChanged;
                Entity mesh = new(world, r.entity);
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
                    if (TryLoadMesh(mesh, request))
                    {
                        meshVersions.AddOrSet(mesh, request.version);
                    }
                }
            }

            PerformOperations(world);
        }

        void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
        }

        void IDisposable.Dispose()
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                operation.Dispose();
            }

            operations.Dispose();
            meshVersions.Dispose();
            modelVersions.Dispose();
        }

        private readonly void PerformOperations(World world)
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                world.Perform(operation);
                operation.Dispose();
            }
        }

        private readonly bool TryLoadMesh(Entity mesh, IsMeshRequest request)
        {
            World world = mesh.GetWorld();
            uint index = request.meshIndex;
            rint modelReference = request.modelReference;
            uint modelEntity = mesh.GetReference(modelReference);

            //wait for model data to load
            if (!world.ContainsComponent<IsModel>(modelEntity))
            {
                return false;
            }

            Model model = new(world, modelEntity);
            Meshes.Mesh existingMesh = model[index];
            Entity existingMeshEntity = existingMesh;
            Operation operation = new();
            Operation.SelectedEntity selectedMesh = operation.SelectEntity(mesh);
            ref IsMesh component = ref mesh.TryGetComponent<IsMesh>(out bool contains);
            if (contains)
            {
                component.version++;
                selectedMesh.SetComponent(new IsMesh(component.version + 1));
                selectedMesh.SetComponent(new Name(existingMesh.Name));
            }
            else
            {
                //become a mesh now!
                selectedMesh.AddComponent(new IsMesh());
                selectedMesh.CreateArray<MeshVertexIndex>(0);

                if (existingMesh.HasPositions)
                {
                    selectedMesh.CreateArray<MeshVertexPosition>(0);
                }

                if (existingMesh.HasUVs)
                {
                    selectedMesh.CreateArray<MeshVertexUV>(0);
                }

                if (existingMesh.HasNormals)
                {
                    selectedMesh.CreateArray<MeshVertexNormal>(0);
                }

                if (existingMesh.HasTangents)
                {
                    selectedMesh.CreateArray<MeshVertexTangent>(0);
                }

                if (existingMesh.HasBiTangents)
                {
                    selectedMesh.CreateArray<MeshVertexBiTangent>(0);
                }

                if (existingMesh.HasColors)
                {
                    selectedMesh.CreateArray<MeshVertexColor>(0);
                }
            }

            //copy each channel
            if (existingMesh.HasPositions)
            {
                USpan<MeshVertexPosition> positions = existingMeshEntity.GetArray<MeshVertexPosition>();
                USpan<MeshVertexIndex> indices = existingMeshEntity.GetArray<MeshVertexIndex>();
                selectedMesh.ResizeArray<MeshVertexIndex>(indices.Length);
                selectedMesh.SetArrayElements(0, indices);
                selectedMesh.ResizeArray<MeshVertexPosition>(positions.Length);
                selectedMesh.SetArrayElements(0, positions);
            }

            if (existingMesh.HasUVs)
            {
                USpan<MeshVertexUV> uvs = existingMeshEntity.GetArray<MeshVertexUV>();
                selectedMesh.ResizeArray<MeshVertexUV>(uvs.Length);
                selectedMesh.SetArrayElements(0, uvs);
            }

            if (existingMesh.HasNormals)
            {
                USpan<MeshVertexNormal> normals = existingMeshEntity.GetArray<MeshVertexNormal>();
                selectedMesh.ResizeArray<MeshVertexNormal>(normals.Length);
                selectedMesh.SetArrayElements(0, normals);
            }

            if (existingMesh.HasTangents)
            {
                USpan<MeshVertexTangent> tangents = existingMeshEntity.GetArray<MeshVertexTangent>();
                selectedMesh.ResizeArray<MeshVertexTangent>(tangents.Length);
                selectedMesh.SetArrayElements(0, tangents);
            }

            if (existingMesh.HasBiTangents)
            {
                USpan<MeshVertexBiTangent> bitangents = existingMeshEntity.GetArray<MeshVertexBiTangent>();
                selectedMesh.ResizeArray<MeshVertexBiTangent>(bitangents.Length);
                selectedMesh.SetArrayElements(0, bitangents);
            }

            if (existingMesh.HasColors)
            {
                USpan<MeshVertexColor> colors = existingMeshEntity.GetArray<MeshVertexColor>();
                selectedMesh.ResizeArray<MeshVertexColor>(colors.Length);
                selectedMesh.SetArrayElements(0, colors);
            }

            operations.Add(operation);
            return true;
        }

        private bool TryLoadModel(Entity model, USpan<byte> hint)
        {
            //wait for byte data to be available
            if (!model.ContainsArray<BinaryData>())
            {
                Trace.WriteLine($"Model data not available on entity `{model}`, waiting");
                return false;
            }

            Operation operation = new();
            USpan<BinaryData> byteData = model.GetArray<BinaryData>();
            ImportModel(model, operation, byteData, hint);

            operation.ClearSelection();
            Operation.SelectedEntity selectedEntity = operation.SelectEntity(model);
            ref IsModel component = ref model.TryGetComponent<IsModel>(out bool contains);
            if (contains)
            {
                selectedEntity.SetComponent(new IsModel(component.version + 1));
            }
            else
            {
                selectedEntity.AddComponent(new IsModel());
            }

            operations.Add(operation);
            return true;
        }

        private readonly unsafe uint ImportModel(Entity model, Operation operation, USpan<BinaryData> bytes, USpan<byte> hint)
        {
            World world = model.GetWorld();
            USpan<char> hintString = stackalloc char[(int)hint.Length];
            for (uint i = 0; i < hint.Length; i++)
            {
                hintString[i] = (char)hint[i];
            }

            using Scene scene = new(bytes.As<byte>().AsSystemSpan(), hintString.AsSystemSpan(), PostProcessSteps.Triangulate);
            bool containsMeshes = model.ContainsArray<ModelMesh>();
            uint existingMeshCount = containsMeshes ? model.GetArrayLength<ModelMesh>() : 0;
            operation.SelectEntity(model);
            uint referenceCount = model.GetReferenceCount();
            using List<ModelMesh> meshes = new();
            ProcessNode(scene.RootNode, scene, operation);
            operation.SelectEntity(model);
            if (containsMeshes)
            {
                operation.ResizeArray<ModelMesh>(meshes.Count);
                operation.SetArrayElements(0, meshes.AsSpan());
            }
            else
            {
                operation.CreateArray(meshes.AsSpan());
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
                Vector3* positions = (Vector3*)mesh.Vertices.AsUSpan().Pointer;
                Vector3* uvs = (Vector3*)mesh.GetTextureCoordinates(0).AsUSpan().Pointer;
                Vector3* normals = (Vector3*)mesh.Normals.AsUSpan().Pointer;
                Vector3* tangents = (Vector3*)mesh.Tangents.AsUSpan().Pointer;
                Vector3* biTangents = (Vector3*)mesh.BiTangents.AsUSpan().Pointer;
                Vector4* colors = (Vector4*)mesh.GetColors(0).AsUSpan().Pointer;

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
                    operation.SetComponent(new Name(name));
                    modelMesh = new(existingMeshReference);
                }
                else
                {
                    //create new mesh
                    operation.CreateEntity();
                    operation.SetParent(model);
                    operation.CreateArray<MeshVertexIndex>(0);
                    operation.AddComponent(new Name(name));

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
                    using Array<MeshVertexPosition> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = positions[i];
                    }

                    operation.ResizeArray<MeshVertexPosition>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());

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

                    operation.ResizeArray<MeshVertexIndex>(indices.Count);
                    operation.SetArrayElements(0, indices.AsSpan());
                }

                if (uvs is not null)
                {
                    using Array<MeshVertexUV> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        Vector3 raw = uvs[i];
                        tempData[i] = new MeshVertexUV(new(raw.X, raw.Y));
                    }

                    operation.ResizeArray<MeshVertexUV>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (normals is not null)
                {
                    using Array<MeshVertexNormal> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(normals[i]);
                    }

                    operation.ResizeArray<MeshVertexNormal>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (tangents is not null)
                {
                    using Array<MeshVertexTangent> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(tangents[i]);
                    }

                    operation.ResizeArray<MeshVertexTangent>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (biTangents is not null)
                {
                    using Array<MeshVertexBiTangent> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(biTangents[i]);
                    }

                    operation.ResizeArray<MeshVertexBiTangent>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                if (colors is not null)
                {
                    using Array<MeshVertexColor> tempData = new(vertexCount);
                    for (uint i = 0; i < vertexCount; i++)
                    {
                        tempData[i] = new(colors[i]);
                    }

                    operation.ResizeArray<MeshVertexColor>(vertexCount);
                    operation.SetArrayElements(0, tempData.AsSpan());
                }

                //Material? material = scene.MaterialCount > 0 ? scene.Materials[0] : null;
                //if (material is not null)
                //{
                //    //todo: handle materials
                //}

                //increment mesh version
                if (meshReused)
                {
                    ref IsMesh component = ref existingMesh.TryGetComponent<IsMesh>(out bool contains);
                    if (contains)
                    {
                        component.version++;
                        operation.SetComponent(new IsMesh(component.version + 1));
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