using Data.Systems;
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
            TypeRegistry.Load<Data.Core.TypeBank>();
            TypeRegistry.Load<Meshes.TypeBank>();
            TypeRegistry.Load<Models.TypeBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            simulator.AddSystem<DataImportSystem>();
            simulator.AddSystem<ModelImportSystem>();
        }

        protected override Schema CreateSchema()
        {
            Schema schema = base.CreateSchema();
            schema.Load<Data.Core.SchemaBank>();
            schema.Load<Meshes.SchemaBank>();
            schema.Load<Models.SchemaBank>();
            return schema;
        }
    }
}
