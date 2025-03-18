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
            TypeRegistry.Load<DataTypeBank>();
            TypeRegistry.Load<MeshesTypeBank>();
            TypeRegistry.Load<ModelsTypeBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            simulator.AddSystem(new DataImportSystem());
            simulator.AddSystem(new ModelImportSystem());
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
