using Data;
using Data.Components;
using Data.Systems;
using Meshes;
using Meshes.Components;
using Models.Components;
using Models.Systems;
using Simulation.Components;
using Simulation.Tests;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Worlds;

namespace Models.Tests
{
    public class ModelTests : SimulationTests
    {
        protected override void SetUp()
        {
            base.SetUp();
            ComponentType.Register<IsDataRequest>();
            ComponentType.Register<IsDataSource>();
            ComponentType.Register<IsData>();
            ComponentType.Register<IsProgram>();
            ArrayType.Register<BinaryData>();
            ComponentType.Register<Name>();
            ComponentType.Register<IsMesh>();
            ComponentType.Register<IsMeshRequest>();
            ComponentType.Register<IsModel>();
            ComponentType.Register<IsModelRequest>();
            ArrayType.Register<ModelMesh>();
            ArrayType.Register<MeshVertexPosition>();
            ArrayType.Register<MeshVertexNormal>();
            ArrayType.Register<MeshVertexUV>();
            ArrayType.Register<MeshVertexColor>();
            ArrayType.Register<MeshVertexTangent>();
            ArrayType.Register<MeshVertexBiTangent>();
            ArrayType.Register<MeshVertexIndex>();
            Simulator.AddSystem(new DataImportSystem());
            Simulator.AddSystem(new ModelImportSystem());
        }

        [Test, CancelAfter(1700)]
        public async Task ImportSimpleCube(CancellationToken cancellation)
        {
            DataSource entity = new(World, "cube.fbx", CubeFBX.bytes);
            Model model = new(World, "cube.fbx");

            await model.UntilCompliant(Simulate, cancellation);

            Assert.That(model.MeshCount, Is.EqualTo(1));
            Mesh mesh = model[0];
            Assert.That(mesh.VertexCount, Is.EqualTo(24));
            (Vector3 min, Vector3 max) bounds = mesh.Bounds;
            Assert.That(bounds.min, Is.EqualTo(new Vector3(-1, -1, -1)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(1, 1, 1)));
        }

        [Test, CancelAfter(1000)]
        public async Task ImportThroughMeshRequest(CancellationToken cancellation)
        {
            DataSource entity = new(World, "cube.fbx", CubeFBX.bytes);
            Model cubeModel = new(World, "cube.fbx");
            Mesh cubeMesh = new(World, cubeModel);

            await cubeMesh.UntilCompliant(Simulate, cancellation);

            Assert.That(cubeMesh.VertexCount, Is.EqualTo(24));
            (Vector3 min, Vector3 max) bounds = cubeMesh.Bounds;
            Assert.That(bounds.min, Is.EqualTo(new Vector3(-1, -1, -1)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(1, 1, 1)));
        }
    }
}
