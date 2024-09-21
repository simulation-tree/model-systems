using Assimp;
using Assimp.Unmanaged;
using System;
using System.IO;
using Unmanaged;

namespace OpenAssetImporter
{
    public readonly struct Library : IDisposable
    {
        public Library()
        {
            //AssimpLibrary.Instance.LoadLibrary();
        }

        public readonly void Dispose()
        {
            AssimpLibrary.Instance.FreeLibrary();
        }

        public readonly Scene ImportModel(USpan<byte> bytes, PostProcessSteps flags = PostProcessSteps.Triangulate)
        {
            AssimpContext importer = new();
            AssimpLibrary library = AssimpLibrary.Instance;
            using MemoryStream stream = new(bytes.ToArray());

            //todo: efficiency: cool, this method allocates a new pointer but no way to release it... wtf?
            Scene scene = importer.ImportFileFromStream(stream, flags);
            return scene;
        }
    }
}
