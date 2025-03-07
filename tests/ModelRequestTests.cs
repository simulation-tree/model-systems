using Models.Components;
using Unmanaged;

namespace Models.Tests
{
    public class ModelRequestTests
    {
        [Test]
        public void EmbeddExtension()
        {
            IsModelRequest request = new("fbx", default(ASCIIText256), default);
            USpan<char> buffer = stackalloc char[8];
            uint bufferLength = request.CopyExtensionCharacters(buffer);
            Assert.That(buffer.GetSpan(bufferLength).ToString(), Is.EqualTo("fbx"));
        }
    }
}
