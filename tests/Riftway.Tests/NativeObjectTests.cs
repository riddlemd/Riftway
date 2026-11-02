using System.Runtime.CompilerServices;
using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed unsafe class NativeObjectTests
{
    private sealed class Owner;

    [Fact]
    public void Create_RecordsTheStructSizeForCefVersioning()
    {
        var task = NativeObject.Create<cef_task_t>(new Owner());

        Assert.Equal((nuint)sizeof(cef_task_t), task->@base.size);

        NativeObject.Release(task);
    }

    [Fact]
    public void Create_StartsWithExactlyOneReference()
    {
        var task = NativeObject.Create<cef_task_t>(new Owner());

        Assert.Equal(1, task->@base.has_one_ref(&task->@base));
        Assert.Equal(1, task->@base.has_at_least_one_ref(&task->@base));

        NativeObject.Release(task);
    }

    [Fact]
    public void AddRef_ThenRelease_TracksTheCount()
    {
        var task = NativeObject.Create<cef_task_t>(new Owner());

        NativeObject.AddRef(task);
        Assert.Equal(0, task->@base.has_one_ref(&task->@base));
        Assert.Equal(1, task->@base.has_at_least_one_ref(&task->@base));

        Assert.Equal(0, task->@base.release(&task->@base));
        Assert.Equal(1, task->@base.has_one_ref(&task->@base));

        NativeObject.Release(task);
    }

    [Fact]
    public void Release_OfTheLastReference_ReportsZero()
    {
        var task = NativeObject.Create<cef_task_t>(new Owner());

        Assert.Equal(1, task->@base.release(&task->@base));
    }

    [Fact]
    public void Release_OfTheLastReference_LetsTheOwnerBeCollected()
    {
        var owner = CreateAndReleaseLastReference();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(owner.IsAlive);
    }

    [Fact]
    public void Owner_ReturnsTheObjectTheStructWasCreatedFor()
    {
        var owner = new Owner();
        var task = NativeObject.Create<cef_task_t>(owner);

        Assert.Same(owner, NativeObject.Owner<Owner>(task));

        NativeObject.Release(task);
    }

    [Fact]
    public void Owner_StaysReachableWhileCefHoldsAReference()
    {
        var weak = CreateAndKeepReference(out var task);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(weak.IsAlive);
        NativeObject.Release(task);
    }

    [Fact]
    public void Release_OfNull_IsIgnored() => NativeObject.Release(null);

    // Separate frames so no local in the test keeps the owner alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndReleaseLastReference()
    {
        var owner = new Owner();
        var task = NativeObject.Create<cef_task_t>(owner);
        NativeObject.Release(task);
        return new WeakReference(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndKeepReference(out cef_task_t* task)
    {
        var owner = new Owner();
        task = NativeObject.Create<cef_task_t>(owner);
        return new WeakReference(owner);
    }
}
