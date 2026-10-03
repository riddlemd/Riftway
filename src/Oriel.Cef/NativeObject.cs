using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>CEF structs implemented in C#: <c>[header][struct]</c> in native memory, with CEF's
/// reference count in the header and a GC handle back to the managed owner.</summary>
/// <remarks>
/// CEF's C API reference rules, which callers of this type follow:
/// a struct passed into a CEF function carries one reference the callee owns (call <see cref="AddRef"/>
/// first to keep your own); a struct a callback returns must carry a reference for the caller; struct
/// arguments CEF passes into a callback carry a reference the callback must release, except <c>self</c>.
/// </remarks>
public static unsafe class NativeObject
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public nint Owner;
        public int RefCount;
    }

    /// <summary>Allocates a zeroed <typeparamref name="T"/> owned by <paramref name="owner"/>, holding one reference.</summary>
    /// <remarks>Every CEF ref-counted struct starts with <c>cef_base_ref_counted_t</c>, possibly nested.</remarks>
    public static T* Create<T>(object owner) where T : unmanaged
    {
        var header = (Header*)NativeMemory.AllocZeroed((nuint)(sizeof(Header) + sizeof(T)));
        header->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
        header->RefCount = 1;

        var self = (cef_base_ref_counted_t*)(header + 1);
        self->size = (nuint)sizeof(T);
        self->add_ref = &AddRefCallback;
        self->release = &ReleaseCallback;
        self->has_one_ref = &HasOneRefCallback;
        self->has_at_least_one_ref = &HasAtLeastOneRefCallback;
        return (T*)self;
    }

    /// <summary>The managed owner of a struct made by <see cref="Create{T}"/>.</summary>
    public static TOwner Owner<TOwner>(void* self) where TOwner : class
        => (TOwner)GCHandle.FromIntPtr(((Header*)self - 1)->Owner).Target!;

    /// <summary>Adds a reference to any CEF ref-counted struct, CEF's or ours.</summary>
    public static void AddRef(void* obj)
    {
        var self = (cef_base_ref_counted_t*)obj;
        self->add_ref(self);
    }

    /// <summary>Releases a reference to any CEF ref-counted struct; null is ignored.</summary>
    public static void Release(void* obj)
    {
        if (obj == null) return;
        var self = (cef_base_ref_counted_t*)obj;
        self->release(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AddRefCallback(cef_base_ref_counted_t* self)
        => Interlocked.Increment(ref ((Header*)self - 1)->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReleaseCallback(cef_base_ref_counted_t* self)
    {
        var header = (Header*)self - 1;
        if (Interlocked.Decrement(ref header->RefCount) != 0) return 0;

        GCHandle.FromIntPtr(header->Owner).Free();
        NativeMemory.Free(header);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HasOneRefCallback(cef_base_ref_counted_t* self)
        => Volatile.Read(ref ((Header*)self - 1)->RefCount) == 1 ? 1 : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HasAtLeastOneRefCallback(cef_base_ref_counted_t* self)
        => Volatile.Read(ref ((Header*)self - 1)->RefCount) >= 1 ? 1 : 0;
}
