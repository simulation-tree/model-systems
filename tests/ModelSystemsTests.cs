using Data;
using Data.Messages;
using Data.Systems;
using Meshes;
using Models.Systems;
using Simulation.Tests;
using Types;
using Worlds;

namespace Models.Tests
{
    public abstract class ModelSystemsTests : SimulationTests
    {
        public World world;

        static ModelSystemsTests()
        {
            MetadataRegistry.Load<DataMetadataBank>();
            MetadataRegistry.Load<MeshesMetadataBank>();
            MetadataRegistry.Load<ModelsMetadataBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            Schema schema = new();
            schema.Load<DataSchemaBank>();
            schema.Load<MeshesSchemaBank>();
            schema.Load<ModelsSchemaBank>();
            world = new(schema);
            Simulator.Add(new DataImportSystem(Simulator, world));
            Simulator.Add(new ModelImportSystem(Simulator, world));
        }

        protected override void TearDown()
        {
            Simulator.Remove<ModelImportSystem>();
            Simulator.Remove<DataImportSystem>();
            world.Dispose();
            base.TearDown();
        }

        protected override void Update(double deltaTime)
        {
            Simulator.Broadcast(new DataUpdate(deltaTime));
        }
    }
}
