using Data;
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
        static ModelSystemsTests()
        {
            MetadataRegistry.Load<DataMetadataBank>();
            MetadataRegistry.Load<MeshesMetadataBank>();
            MetadataRegistry.Load<ModelsMetadataBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            Simulator.Add(new DataImportSystem(Simulator));
            Simulator.Add(new ModelImportSystem(Simulator));
        }

        protected override void TearDown()
        {
            Simulator.Remove<ModelImportSystem>();
            Simulator.Remove<DataImportSystem>();
            base.TearDown();
        }

        protected override Schema CreateSchema()
        {
            Schema schema = base.CreateSchema();
            schema.Load<DataSchemaBank>();
            schema.Load<MeshesSchemaBank>();
            schema.Load<ModelsSchemaBank>();
            return schema;
        }
    }
}
