using Data;
using Data.Systems;
using Meshes;
using Models.Systems;
using Simulation.Tests;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Models.Tests
{
    public class ModelTests : SimulationTests
    {
        protected override void SetUp()
        {
            base.SetUp();
            Simulator.AddSystem<DataImportSystem>();
            Simulator.AddSystem<ModelImportSystem>();
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
            Mesh cubeMesh = new(World, cubeModel.entity);

            await cubeMesh.UntilCompliant(Simulate, cancellation);

            Assert.That(cubeMesh.VertexCount, Is.EqualTo(24));
            (Vector3 min, Vector3 max) bounds = cubeMesh.Bounds;
            Assert.That(bounds.min, Is.EqualTo(new Vector3(-1, -1, -1)));
            Assert.That(bounds.max, Is.EqualTo(new Vector3(1, 1, 1)));
        }
    }
}
