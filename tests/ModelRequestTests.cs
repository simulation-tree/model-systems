using Models.Components;
using System;
using Unmanaged;

namespace Models.Tests
{
    public class ModelRequestTests
    {
        [Test]
        public void EmbeddExtension()
        {
            IsModelRequest request = new("fbx", default(ASCIIText256), default);
            Span<char> buffer = stackalloc char[8];
            int bufferLength = request.CopyExtensionCharacters(buffer);
            Assert.That(buffer.Slice(0, bufferLength).ToString(), Is.EqualTo("fbx"));
        }
    }
}
