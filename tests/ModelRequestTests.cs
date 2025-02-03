using Models.Components;
using Unmanaged;

namespace Models.Tests
{
    public class ModelRequestTests
    {
        [Test]
        public void EmbeddExtension()
        {
            IsModelRequest request = new("fbx", default(FixedString), default);
            USpan<char> buffer = stackalloc char[8];
            uint bufferLength = request.CopyExtensionCharacters(buffer);
            Assert.That(buffer.Slice(0, bufferLength).ToString(), Is.EqualTo("fbx"));
        }
    }
}
