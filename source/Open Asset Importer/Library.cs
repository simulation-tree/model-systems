using Silk.NET.Assimp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unmanaged;

namespace OpenAssetImporter
{
    public readonly struct Library : IDisposable
    {
        private readonly GCHandle handle;

        public readonly bool IsDisposed => !handle.IsAllocated;

        private readonly Assimp Assimp => (Assimp)(handle.Target ?? throw new ObjectDisposedException(nameof(Library)));

        public Library()
        {
            handle = GCHandle.Alloc(Assimp.GetApi());
        }

        [Conditional("DEBUG")]
        private readonly void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(Library));
            }
        }

        public readonly void Dispose()
        {
            ThrowIfDisposed();

            Assimp.Dispose();
            handle.Free();
        }

        public unsafe readonly Scene* ImportModel(USpan<byte> bytes, USpan<char> hint, PostProcessSteps flags = PostProcessSteps.Triangulate)
        {
            USpan<byte> hintBytes = stackalloc byte[(int)(hint.Length + 1)];
            for (uint i = 0; i < hint.Length; i++)
            {
                hintBytes[i] = (byte)hint[i];
            }

            hintBytes[hint.Length] = 0;
            return ImportModel(bytes, hintBytes, flags);
        }

        public unsafe readonly Scene* ImportModel(USpan<byte> bytes, USpan<byte> hint, PostProcessSteps flags = PostProcessSteps.Triangulate)
        {
            Scene* scene = Assimp.ImportFileFromMemory(bytes.AsSystemSpan(), bytes.Length, default, hint.AsSystemSpan());
            if (scene is null)
            {
                throw new Exception($"Failed to import model: {Assimp.GetErrorStringS()}");
            }

            Assimp.ApplyPostProcessing(scene, (uint)flags);
            return scene;
        }

        public unsafe void Release(Scene* scene)
        {
            Assimp.ReleaseImport(scene);
        }
    }
}
