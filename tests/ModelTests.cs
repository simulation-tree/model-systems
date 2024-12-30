using Data;
using Data.Components;
using Data.Systems;
using Meshes;
using Meshes.Components;
using Models.Components;
using Models.Systems;
using Simulation.Tests;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Worlds;

namespace Models.Tests
{
    public class ModelTests : SimulationTests
    {
        static ModelTests()
        {
            TypeLayout.Register<IsDataRequest>();
            TypeLayout.Register<IsDataSource>();
            TypeLayout.Register<IsData>();
            TypeLayout.Register<BinaryData>();
            TypeLayout.Register<Name>();
            TypeLayout.Register<IsMesh>();
            TypeLayout.Register<IsMeshRequest>();
            TypeLayout.Register<IsModel>();
            TypeLayout.Register<IsModelRequest>();
            TypeLayout.Register<ModelMesh>();
            TypeLayout.Register<MeshVertexPosition>();
            TypeLayout.Register<MeshVertexNormal>();
            TypeLayout.Register<MeshVertexUV>();
            TypeLayout.Register<MeshVertexColor>();
            TypeLayout.Register<MeshVertexTangent>();
            TypeLayout.Register<MeshVertexBiTangent>();
            TypeLayout.Register<MeshVertexIndex>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            world.Schema.RegisterComponent<IsDataRequest>();
            world.Schema.RegisterComponent<IsDataSource>();
            world.Schema.RegisterComponent<IsData>();
            world.Schema.RegisterComponent<Name>();
            world.Schema.RegisterComponent<IsMesh>();
            world.Schema.RegisterComponent<IsMeshRequest>();
            world.Schema.RegisterComponent<IsModel>();
            world.Schema.RegisterComponent<IsModelRequest>();
            world.Schema.RegisterArrayElement<BinaryData>();
            world.Schema.RegisterArrayElement<ModelMesh>();
            world.Schema.RegisterArrayElement<MeshVertexPosition>();
            world.Schema.RegisterArrayElement<MeshVertexNormal>();
            world.Schema.RegisterArrayElement<MeshVertexUV>();
            world.Schema.RegisterArrayElement<MeshVertexColor>();
            world.Schema.RegisterArrayElement<MeshVertexTangent>();
            world.Schema.RegisterArrayElement<MeshVertexBiTangent>();
            world.Schema.RegisterArrayElement<MeshVertexIndex>();
            simulator.AddSystem<DataImportSystem>();
            simulator.AddSystem<ModelImportSystem>();
        }

        [Test, CancelAfter(1700)]
        public async Task ImportSimpleCube(CancellationToken cancellation)
        {
            DataSource entity = new(world, "cube.fbx", CubeFBX.bytes);
            Model model = new(world, "cube.fbx");

            await model.UntilCompliant(Simulate, cancellation);

            Assert.That(model.MeshCount, Is.EqualTo(1));
            Mesh mesh = model[0];
            Assert.That(mesh.GetVertexCount(), Is.EqualTo(24));
            (Vector3 min, Vector3 max) bounds = mesh.GetBounds();
            Assert.That(bounds.min, Is.EqualTo(new Vector3(-1, -1, -1)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(1, 1, 1)));
        }

        [Test, CancelAfter(1000)]
        public async Task ImportThroughMeshRequest(CancellationToken cancellation)
        {
            DataSource entity = new(world, "cube.fbx", CubeFBX.bytes);
            Model cubeModel = new(world, "cube.fbx");
            Mesh cubeMesh = new(world, cubeModel);

            await cubeMesh.UntilCompliant(Simulate, cancellation);

            Assert.That(cubeMesh.GetVertexCount(), Is.EqualTo(24));
            (Vector3 min, Vector3 max) bounds = cubeMesh.GetBounds();
            Assert.That(bounds.min, Is.EqualTo(new Vector3(-1, -1, -1)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(1, 1, 1)));
        }
    }
}
