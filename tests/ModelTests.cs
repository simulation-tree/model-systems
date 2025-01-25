using Data;
using Meshes;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Worlds;

namespace Models.Tests
{
    public class ModelImportTests : ModelSystemsTests
    {
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
